using System;
using System.Collections.Generic;
using System.Linq;
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
      if (element is Autodesk.Revit.DB.Architecture.Stairs stairs && ReadStairs(document, view, stairs) is { } parts) return parts;
      using var geometry = element.get_Geometry(new Options { View = view, ComputeReferences = false, IncludeNonVisibleObjects = false });
      if (geometry == null) return new();
      var result = LiveMetadata.Read(element, element.UniqueId);
      Visit(geometry, Transform.Identity, document, view, result, 0);
      return result.Meshes.Count == 0 && !IsAssembly(element) ? new() : new() { [result.Id] = result };
    }
    // As in the IFC exporter, a stair is an IfcStair aggregating its flights, landings and stringers,
    // each sent as its own element with its own geometry. Null keeps the stair whole (no readable parts).
    private Dictionary<string, LiveElement> ReadStairs(Document document, View3D view, Autodesk.Revit.DB.Architecture.Stairs stairs)
    {
      var result = new Dictionary<string, LiveElement>();
      var inView = new Options { View = view, ComputeReferences = false, IncludeNonVisibleObjects = false };
      // Revit does not treat stair parts as elements of the view, so the view may return no geometry for them;
      // the same detail level without a view gives the part's own solids.
      var detailed = new Options { DetailLevel = view.DetailLevel, ComputeReferences = false, IncludeNonVisibleObjects = false };
      foreach (var partId in stairs.GetStairsRuns().Concat(stairs.GetStairsLandings()).Concat(stairs.GetStairsSupports()))
      {
        if (document.GetElement(partId) is not { } part) continue;
        var piece = LiveMetadata.Read(part, part.UniqueId);
        foreach (var options in new[] { inView, detailed })
        {
          using var geometry = part.get_Geometry(options);
          if (geometry != null) Visit(geometry, Transform.Identity, document, view, piece, 0);
          if (piece.Meshes.Count > 0) break;
        }
        if (piece.Meshes.Count == 0) continue;
        if (piece.Bim != null) piece.Bim.AggregateParentId = stairs.UniqueId;
        result[piece.Id] = piece;
      }
      if (result.Count == 0) return null;
      var root = LiveMetadata.Read(stairs, stairs.UniqueId);
      result[root.Id] = root;
      return result;
    }
    // Curtain walls and glazed roofs draw nothing themselves; their panels and mullions carry the geometry.
    // They are still sent as the IfcCurtainWall/IfcRoof that aggregates those parts.
    internal static bool IsAssembly(Element element) =>
      element is Wall { WallType.Kind: WallKind.Curtain } or CurtainSystem or BeamSystem ||
      element is FootPrintRoof { CurtainGrids.Size: > 0 } || element is ExtrusionRoof { CurtainGrids.Size: > 0 };
    private void Visit(GeometryElement geometry, Transform placement, Document document, View view, LiveElement result, int depth)
    {
      if (depth > 32) throw new InvalidOperationException("Se excedió la profundidad de instancias geométricas.");
      foreach (var item in geometry)
      {
        if (HiddenSubcategory(item, document, view)) continue;
        if (item is GeometryInstance instance)
        {
          using var nested = instance.GetSymbolGeometry();
          Visit(nested, placement.Multiply(instance.Transform), document, view, result, depth + 1);
        }
        else if (item is Solid solid)
          foreach (Face face in solid.Faces) Add(face.Triangulate(), face.MaterialElementId, placement, document, result);
        else if (item is Mesh mesh) Add(mesh, mesh.MaterialElementId, placement, document, result);
      }
    }
    // get_Geometry applies the view to categories but not to subcategories, so family parts the view hides
    // (light sources, hidden family subcategories) are skipped here, as Revit draws them.
    private static bool HiddenSubcategory(GeometryObject item, Document document, View view)
    {
      if (view == null || item.GraphicsStyleId == ElementId.InvalidElementId) return false;
      if (document.GetElement(item.GraphicsStyleId) is not GraphicsStyle { GraphicsStyleCategory: { } category }) return false;
      try { return category.Parent != null && view.GetCategoryHidden(category.Id); }
      catch (Autodesk.Revit.Exceptions.ArgumentException) { return false; }
    }
    private void Add(Mesh mesh, ElementId materialId, Transform placement, Document document, LiveElement result)
    {
      if (mesh == null || mesh.NumTriangles == 0) return;
      var vertices = mesh.Vertices;
      _vertices += vertices.Count;
      if (_vertices > CaptureLimits.ElementVertices) throw new InvalidOperationException($"Un elemento supera {CaptureLimits.ElementVertices:N0} vértices; reduce detalle o alcance.");
      var positions = new double[vertices.Count * 3];
      for (var i = 0; i < vertices.Count; i++)
      {
        var point = placement.OfPoint(vertices[i]);
        // 0.1 mm is finer than any viewer can show and halves the text size of every coordinate.
        positions[i * 3] = Finite(Math.Round(point.X * 0.3048, 4)); positions[i * 3 + 1] = Finite(Math.Round(point.Y * 0.3048, 4)); positions[i * 3 + 2] = Finite(Math.Round(point.Z * 0.3048, 4));
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
    private static double Finite(double value) => NetCompat.IsFinite(value) && Math.Abs(value) <= 1e9 ? value
      : throw new InvalidOperationException("Geometría con coordenadas fuera de los límites del visor.");
  }
}
