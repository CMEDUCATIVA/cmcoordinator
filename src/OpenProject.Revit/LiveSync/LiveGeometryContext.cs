using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace OpenProject.Revit.LiveSync
{
  // IExportContext is also used by the Everse reference exporter; no glTF file/UI is needed here.
  internal sealed class LiveGeometryContext : IExportContext
  {
    private Document _document;
    private string _placement = "";
    private readonly ISet<ElementId> _filter;
    private readonly Stack<Transform> _transforms = new();
    private readonly Stack<Tuple<Document, string>> _links = new();
    private readonly Stack<Tuple<Element, string>> _elements = new();
    private string _color = "#8bb8c9";
    private double _opacity = 1;
    private string _materialId;
    private int _vertices;
    public Dictionary<string, LiveElement> Elements { get; } = new(StringComparer.Ordinal);

    private LiveGeometryContext(Document document, ISet<ElementId> filter)
    {
      _document = document;
      _filter = filter;
      _transforms.Push(Transform.Identity);
    }

    public static Dictionary<string, LiveElement> Capture(Document doc, View3D view, ISet<ElementId> filter)
    {
      var context = new LiveGeometryContext(doc, filter);
      using var exporter = new CustomExporter(doc, context)
      {
        IncludeGeometricObjects = false, ShouldStopOnError = true
      };
      exporter.Export(view);
      return context.Elements;
    }

    public bool Start() => true;
    public void Finish() { }
    public bool IsCanceled() => false;
    public RenderNodeAction OnViewBegin(ViewNode node) => RenderNodeAction.Proceed;
    public void OnViewEnd(ElementId id) { }

    public RenderNodeAction OnElementBegin(ElementId id)
    {
      var element = _document?.GetElement(id);
      var key = element == null ? null : _placement + element.UniqueId;
      _elements.Push(Tuple.Create(element, key));
      _color = "#8bb8c9";
      _opacity = 1;
      _materialId = null;
      return element == null || (_links.Count == 0 && _filter != null && !_filter.Contains(id))
        ? RenderNodeAction.Skip : RenderNodeAction.Proceed;
    }

    public void OnElementEnd(ElementId id) { if (_elements.Count > 0) _elements.Pop(); }

    public RenderNodeAction OnInstanceBegin(InstanceNode node)
    {
      _transforms.Push(_transforms.Peek().Multiply(node.GetTransform()));
      return RenderNodeAction.Proceed;
    }

    public void OnInstanceEnd(InstanceNode node) => _transforms.Pop();

    public RenderNodeAction OnLinkBegin(LinkNode node)
    {
      _links.Push(Tuple.Create(_document, _placement));
      _placement = (_elements.Count > 0 ? _elements.Peek().Item2 : _placement) + "/";
      _document = node.GetDocument();
      _transforms.Push(_transforms.Peek().Multiply(node.GetTransform()));
      return _document == null ? RenderNodeAction.Skip : RenderNodeAction.Proceed;
    }

    public void OnLinkEnd(LinkNode node)
    {
      var previous = _links.Pop();
      _document = previous.Item1;
      _placement = previous.Item2;
      _transforms.Pop();
    }

    public void OnMaterial(MaterialNode node)
    {
      _materialId = _document.GetElement(node.MaterialId)?.UniqueId;
      _color = $"#{node.Color.Red:X2}{node.Color.Green:X2}{node.Color.Blue:X2}";
      _opacity = Math.Max(0, Math.Min(1, 1 - node.Transparency));
    }

    public void OnPolymesh(PolymeshTopology node)
    {
      if (_elements.Count == 0 || _elements.Peek().Item1 == null) return;
      var points = node.GetPoints();
      var facets = node.GetFacets();
      if (points.Count == 0 || facets.Count == 0) return;
      _vertices += points.Count;
      // A linked model is extracted as a whole, so it shares the view limit rather than the per-element one.
      if (_vertices > OpenProject.Revit.LiveSync.Capture.CaptureLimits.ViewVertices)
        throw new InvalidOperationException($"La vista supera {OpenProject.Revit.LiveSync.Capture.CaptureLimits.ViewVertices:N0} vértices por extracción; reduce su alcance.");
      var positions = new double[points.Count * 3];
      var transform = _transforms.Peek();
      for (var i = 0; i < points.Count; i++)
      {
        var point = transform.OfPoint(points[i]);
        // Same 0.1 mm precision as directly captured elements.
        positions[i * 3] = Finite(Math.Round(point.X * 0.3048, 4));
        positions[i * 3 + 1] = Finite(Math.Round(point.Y * 0.3048, 4));
        positions[i * 3 + 2] = Finite(Math.Round(point.Z * 0.3048, 4));
      }
      var indices = new int[facets.Count * 3];
      var mirror = transform.Determinant < 0;
      for (var i = 0; i < facets.Count; i++)
      {
        indices[i * 3] = facets[i].V1;
        indices[i * 3 + 1] = mirror ? facets[i].V3 : facets[i].V2;
        indices[i * 3 + 2] = mirror ? facets[i].V2 : facets[i].V3;
      }
      var frame = _elements.Peek();
      if (!Elements.TryGetValue(frame.Item2, out var element))
      {
        element = LiveMetadata.Read(frame.Item1, frame.Item2);
        Elements.Add(element.Id, element);
      }
      element.Meshes.Add(new LiveMesh { Positions = positions, Indices = indices, Color = _color, Opacity = _opacity, MaterialId = _materialId });
    }

    private static double Finite(double value) => double.IsNaN(value) || double.IsInfinity(value)
      ? throw new InvalidOperationException("Coordenada no finita en la geometría.") : value;

    public RenderNodeAction OnFaceBegin(FaceNode node) => RenderNodeAction.Proceed;
    public void OnFaceEnd(FaceNode node) { }
    public void OnLight(LightNode node) { }
    public void OnRPC(RPCNode node) { }
  }
}
