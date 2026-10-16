using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace OpenProject.Revit.LiveSync.Capture
{
  internal sealed class ElementGeometryReader
  {
    private int _vertices;
    public Dictionary<string, LiveElement> Read(Document document, View3D view, ElementId id)
    {
      _vertices = 0;
      var element = document.GetElement(id);
      if (element == null) return new();
      // Linked view rules are delegated to Revit's exporter. This unit can exceed the idle budget.
      if (element is RevitLinkInstance)
        return LiveGeometryContext.Capture(document, view, new HashSet<ElementId> { id });
      using var geometry = element.get_Geometry(new Options { View = view, ComputeReferences = false, IncludeNonVisibleObjects = false });
      if (geometry == null) return new();
      var result = LiveMetadata.Read(element, element.UniqueId);
      Visit(geometry, Transform.Identity, document, result, 0);
      return result.Meshes.Count == 0 ? new() : new() { [result.Id] = result };
    }
    private void Visit(GeometryElement geometry, Transform placement, Document document, LiveElement result, int depth)
    {
      if (depth > 32) throw new InvalidOperationException("Se excedió la profundidad de instancias geométricas.");
      foreach (var item in geometry)
      {
        if (item is GeometryInstance instance)
        {
          using var nested = instance.GetSymbolGeometry();
          Visit(nested, placement.Multiply(instance.Transform), document, result, depth + 1);
        }
        else if (item is Solid solid)
          foreach (Face face in solid.Faces) Add(face.Triangulate(), face.MaterialElementId, placement, document, result);
        else if (item is Mesh mesh) Add(mesh, mesh.MaterialElementId, placement, document, result);
      }
    }
    private void Add(Mesh mesh, ElementId materialId, Transform placement, Document document, LiveElement result)
    {
      if (mesh == null || mesh.NumTriangles == 0) return;
      var vertices = mesh.Vertices;
      _vertices += vertices.Count;
      if (_vertices > 750000) throw new InvalidOperationException("Un elemento supera 750.000 vértices; reduce detalle o alcance.");
      var positions = new double[vertices.Count * 3];
      for (var i = 0; i < vertices.Count; i++)
      {
        var point = placement.OfPoint(vertices[i]);
        positions[i * 3] = Finite(point.X * 0.3048); positions[i * 3 + 1] = Finite(point.Y * 0.3048); positions[i * 3 + 2] = Finite(point.Z * 0.3048);
      }
      var indices = new int[mesh.NumTriangles * 3];
      var mirrored = placement.Determinant < 0;
      for (var i = 0; i < mesh.NumTriangles; i++)
      {
        var triangle = mesh.get_Triangle(i);
        indices[i * 3] = (int)triangle.get_Index(0);
        indices[i * 3 + 1] = (int)triangle.get_Index(mirrored ? 2 : 1);
        indices[i * 3 + 2] = (int)triangle.get_Index(mirrored ? 1 : 2);
      }
      var material = document.GetElement(materialId) as Material;
      var color = material?.Color;
      result.Meshes.Add(new LiveMesh { Positions = positions, Indices = indices,
        Color = color?.IsValid == true ? $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}" : "#8bb8c9",
        Opacity = material == null ? 1 : 1 - material.Transparency / 100.0, MaterialId = material?.UniqueId });
    }
    private static double Finite(double value) => double.IsFinite(value) && Math.Abs(value) <= 1e9 ? value
      : throw new InvalidOperationException("Geometría con coordenadas fuera de los límites del visor.");
  }
}
