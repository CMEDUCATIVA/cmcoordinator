using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace OpenProject.Revit.LiveSync.Drawings
{
  // Draws one 2D view as Revit shows it (IExportContext2D). Model curves come back tessellated by Revit with their
  // line properties; annotation curves never do, so the curves an element sends are kept and drawn with their
  // graphics style only when Revit sent no tessellated lines for that element.
  internal sealed class DrawingExportContext : IExportContext2D
  {
    private readonly Document _host;
    private readonly XYZ _origin, _right, _up;
    private readonly SvgDrawingWriter _writer = new();
    private readonly Stack<Transform> _transforms = new();
    private readonly List<(List<(double U, double V)> Points, string Color, double Width)> _pending = new();
    private Document _document;
    private Element _element;
    private bool _tessellated;
    public SvgDrawingWriter Writer => _writer;

    private DrawingExportContext(Document document, View view)
    {
      _host = _document = document;
      _origin = view.Origin; _right = view.RightDirection; _up = view.UpDirection;
      _transforms.Push(Transform.Identity);
    }

    // drawn: the host document's elements present in the view, to know which edits change this drawing.
    public static SvgDrawingWriter Draw(Document document, View view, out HashSet<ElementId> drawn)
    {
      var context = new DrawingExportContext(document, view);
      using var exporter = new CustomExporter(document, context)
      {
        IncludeGeometricObjects = true, ShouldStopOnError = false,
        Export2DIncludingAnnotationObjects = true, Export2DGeometricObjectsIncludingPatternLines = true
      };
      exporter.Export(view);
      drawn = context._drawn;
      return context._writer;
    }
    private readonly HashSet<ElementId> _drawn = new();

    public bool Start() => true;
    public void Finish() { }
    public bool IsCanceled() => false;
    public RenderNodeAction OnViewBegin(ViewNode node) => RenderNodeAction.Proceed;
    public void OnViewEnd(ElementId elementId) { }
    public RenderNodeAction OnElementBegin(ElementId elementId) => RenderNodeAction.Proceed;
    public void OnElementEnd(ElementId elementId) { }

    public RenderNodeAction OnElementBegin2D(ElementNode node)
    {
      _document = node.Document ?? _host;
      _element = _document.GetElement(node.ElementId);
      _tessellated = false; _pending.Clear();
      // Linked elements carry the same key as in the 3D model: link instance / element.
      var linkId = RevitCompat.LinkInstanceId(node);
      var link = linkId != ElementId.InvalidElementId ? _host.GetElement(linkId) : null;
      var key = _element == null ? null : (link != null ? link.UniqueId + "/" : "") + _element.UniqueId;
      _drawn.Add(link?.Id ?? node.ElementId);
      _writer.BeginElement(key, _element?.Id.ToString(), _element?.Category?.Name);
      return RenderNodeAction.Proceed;
    }

    public void OnElementEnd2D(ElementNode node)
    {
      if (!_tessellated) foreach (var (points, color, width) in _pending) _writer.Line(points, color, width);
      _pending.Clear(); _writer.EndElement(); _element = null; _document = _host;
    }

    public RenderNodeAction OnInstanceBegin(InstanceNode node)
    { _transforms.Push(_transforms.Peek().Multiply(node.GetTransform())); return RenderNodeAction.Proceed; }
    public void OnInstanceEnd(InstanceNode node) { if (_transforms.Count > 1) _transforms.Pop(); }
    public RenderNodeAction OnLinkBegin(LinkNode node)
    { _transforms.Push(_transforms.Peek().Multiply(node.GetTransform())); return RenderNodeAction.Proceed; }
    public void OnLinkEnd(LinkNode node) { if (_transforms.Count > 1) _transforms.Pop(); }

    // Faces, edges and silhouettes are left to Revit, which sends them as segments with their line properties.
    public RenderNodeAction OnFaceEdge2D(FaceEdgeNode node) => RenderNodeAction.Proceed;
    public RenderNodeAction OnFaceSilhouette2D(FaceSilhouetteNode node) => RenderNodeAction.Proceed;
    public RenderNodeAction OnFaceBegin(FaceNode node) => RenderNodeAction.Proceed;
    public void OnFaceEnd(FaceNode node) { }

    public RenderNodeAction OnCurve(CurveNode node)
    {
      var curve = node.GetCurve();
      if (curve != null) Keep(curve.Tessellate(), curve.GraphicsStyleId);
      return RenderNodeAction.Proceed;
    }

    public RenderNodeAction OnPolyline(PolylineNode node)
    {
      var polyline = node.GetPolyline();
      if (polyline != null) Keep(polyline.GetCoordinates(), polyline.GraphicsStyleId);
      return RenderNodeAction.Proceed;
    }

    public void OnLineSegment(LineSegment segment)
    {
      _tessellated = true;
      var (color, width) = Style(segment.LineProperties);
      _writer.Line(new[] { Project(segment.StartPoint), Project(segment.EndPoint) }, color, width);
    }

    public void OnPolylineSegments(PolylineSegments segments)
    {
      _tessellated = true;
      var points = segments.GetVertices().Select(Project).ToList();
      var (color, width) = Style(segments.LineProperties);
      if (segments.IsFilled) _writer.Fill(points, color);
      else _writer.Line(points, color, width);
    }

    public void OnText(TextNode node)
    {
      var position = Project(node.Position);
      var baseDirection = _transforms.Peek().OfVector(node.BaseDirection);
      var angle = Math.Atan2(baseDirection.DotProduct(_up), baseDirection.DotProduct(_right)) * 180 / Math.PI;
      var horizontal = node.HorizontalAlignment.ToString();
      var vertical = node.VerticalAlignment.ToString();
      _writer.Text(node.Text, position.U, position.V, node.FontHeight * 0.3048, angle,
        horizontal.Contains("Center") ? "middle" : horizontal.Contains("Right") ? "end" : "start",
        vertical.Contains("Top") ? "hanging" : vertical.Contains("Middle") ? "middle" : "alphabetic",
        Hex(node.Color), node.FontName, node.IsBold, node.IsItalic);
    }

    public void OnMaterial(MaterialNode node) { }
    public void OnPolymesh(PolymeshTopology node) { }
    public void OnRPC(RPCNode node) { }
    public void OnLight(LightNode node) { }

    private void Keep(IList<XYZ> points, ElementId styleId)
    {
      if (points == null || points.Count < 2) return;
      var (color, width) = Style(styleId);
      _pending.Add((points.Select(Project).ToList(), color, width));
    }

    private (double U, double V) Project(XYZ point)
    {
      var world = _transforms.Peek().OfPoint(point) - _origin;
      return (world.DotProduct(_right) * 0.3048, world.DotProduct(_up) * 0.3048);
    }

    private static (string Color, double Width) Style(LineProperties properties)
    {
      if (properties?.IsValidObject != true) return ("#000000", 1);
      return (Hex(properties.Color), properties.LineWidth > 0 ? properties.LineWidth : 1);
    }

    // Annotation curves take the colour and projection weight of their line style, or of their category.
    private (string Color, double Width) Style(ElementId styleId)
    {
      var category = (_document.GetElement(styleId) as GraphicsStyle)?.GraphicsStyleCategory ?? _element?.Category;
      if (category == null) return ("#000000", 1);
      var weight = category.GetLineWeight(GraphicsStyleType.Projection) ?? 1;
      return (Hex(category.LineColor), PenWidth(weight));
    }

    // Revit pens 1–16, from hairline to heavy, as on-screen pixels.
    internal static double PenWidth(int pen) => Math.Round(0.5 + 0.35 * (NetCompat.Clamp(pen, 1, 16) - 1), 2);

    private static string Hex(Color color) => color?.IsValid == true ? $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}" : "#000000";
  }
}
