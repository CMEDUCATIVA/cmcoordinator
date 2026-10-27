using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace OpenProject.Revit.LiveSync.Drawings
{
  // Builds the SVG of one 2D view from points already projected on the view plane (metres, v up).
  // Strokes are grouped per element so the CDE can link each drawn element to its 3D element.
  public sealed class SvgDrawingWriter
  {
    public const int MaxBytes = 16 * 1024 * 1024;
    private sealed class Group
    {
      public string Key, ElementId, Category;
      public readonly Dictionary<string, StringBuilder> Strokes = new(StringComparer.Ordinal);
      public readonly Dictionary<string, (double U, double V)> Ends = new(StringComparer.Ordinal);
      public readonly StringBuilder Fills = new();
      public readonly StringBuilder Texts = new();
      public bool Empty => Strokes.Count == 0 && Fills.Length == 0 && Texts.Length == 0;
    }
    private readonly List<Group> _groups = new();
    private readonly Dictionary<string, Group> _byKey = new(StringComparer.Ordinal);
    private readonly Group _loose = new();
    private Group _current;
    private long _size;
    private double _minU = double.MaxValue, _minV = double.MaxValue, _maxU = double.MinValue, _maxV = double.MinValue;

    public IReadOnlyList<string> Keys => _groups.Where(group => !group.Empty && group.Key != null).Select(group => group.Key).ToList();
    public bool HasContent => !_loose.Empty || _groups.Any(group => !group.Empty);
    // Bounds of the drawing in view metres: minU, minV, maxU, maxV.
    public double[] Bounds => HasContent ? new[] { _minU, _minV, _maxU, _maxV } : new double[] { 0, 0, 0, 0 };

    // Revit draws a view in several passes (model, then annotations): an element seen again continues its group.
    public void BeginElement(string key, string elementId, string category)
    {
      if (key == null) { _current = _loose; return; }
      if (!_byKey.TryGetValue(key, out var group))
      {
        group = new Group { Key = key, ElementId = elementId, Category = category };
        _byKey[key] = group; _groups.Add(group);
      }
      _current = group;
    }
    public void EndElement() => _current = null;

    // Consecutive strokes of the same style that continue each other become one path.
    public void Line(IReadOnlyList<(double U, double V)> points, string color, double width)
    {
      if (points == null || points.Count < 2) return;
      var group = _current ?? _loose;
      var style = Color(color) + "|" + Number(Math.Max(0.1, width));
      if (!group.Strokes.TryGetValue(style, out var path)) group.Strokes[style] = path = new StringBuilder();
      var start = Round(points[0]);
      var continues = group.Ends.TryGetValue(style, out var end) && end == start;
      var before = path.Length;
      if (!continues) path.Append('M').Append(Number(start.U)).Append(' ').Append(Number(-start.V));
      Grow(start);
      for (var i = 1; i < points.Count; i++)
      {
        var point = Round(points[i]);
        path.Append('L').Append(Number(point.U)).Append(' ').Append(Number(-point.V));
        Grow(point);
      }
      group.Ends[style] = Round(points[points.Count - 1]);
      Account(path.Length - before);
    }

    public void Fill(IReadOnlyList<(double U, double V)> points, string color)
    {
      if (points == null || points.Count < 3) return;
      var group = _current ?? _loose;
      var before = group.Fills.Length;
      group.Fills.Append("<path d=\"");
      for (var i = 0; i < points.Count; i++)
      {
        var point = Round(points[i]); Grow(point);
        group.Fills.Append(i == 0 ? 'M' : 'L').Append(Number(point.U)).Append(' ').Append(Number(-point.V));
      }
      group.Fills.Append("Z\" fill=\"").Append(Color(color)).Append("\" stroke=\"none\"/>");
      Account(group.Fills.Length - before);
    }

    // height: height of the capital M in metres; angle: degrees from the view's right towards its up.
    public void Text(string text, double u, double v, double height, double angle, string anchor, string baseline,
      string color, string font, bool bold, bool italic)
    {
      if (string.IsNullOrWhiteSpace(text) || !(height > 0)) return;
      var group = _current ?? _loose;
      var before = group.Texts.Length;
      var position = Round((u, v)); Grow(position);
      // A capital M is about 0.7 of the font size.
      var size = Number(Math.Round(height / 0.7, 4));
      var x = Number(position.U); var y = Number(-position.V);
      group.Texts.Append("<text x=\"").Append(x).Append("\" y=\"").Append(y).Append("\" font-size=\"").Append(size)
        .Append("\" font-family=\"").Append(Escape(string.IsNullOrWhiteSpace(font) ? "Arial" : font)).Append("\" fill=\"").Append(Color(color))
        .Append("\" text-anchor=\"").Append(anchor ?? "start").Append("\" dominant-baseline=\"").Append(baseline ?? "alphabetic").Append('"');
      if (bold) group.Texts.Append(" font-weight=\"bold\"");
      if (italic) group.Texts.Append(" font-style=\"italic\"");
      // SVG rotates clockwise with y down, so the view angle changes sign.
      if (Math.Abs(angle) > 0.01) group.Texts.Append(" transform=\"rotate(").Append(Number(Math.Round(-angle, 2))).Append(' ').Append(x).Append(' ').Append(y).Append(")\"");
      group.Texts.Append('>');
      var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
      if (lines.Length == 1) group.Texts.Append(Escape(text));
      else
        for (var i = 0; i < lines.Length; i++)
          group.Texts.Append("<tspan x=\"").Append(x).Append("\" dy=\"").Append(i == 0 ? "0" : "1.2em").Append("\">").Append(Escape(lines[i])).Append("</tspan>");
      group.Texts.Append("</text>");
      Account(group.Texts.Length - before);
    }

    public string ToSvg()
    {
      var bounds = Bounds;
      var margin = Math.Max(0.5, Math.Max(bounds[2] - bounds[0], bounds[3] - bounds[1]) * 0.02);
      var svg = new StringBuilder("<svg xmlns=\"http://www.w3.org/2000/svg\" data-units=\"m\" viewBox=\"");
      svg.Append(Number(bounds[0] - margin)).Append(' ').Append(Number(-bounds[3] - margin)).Append(' ')
        .Append(Number(bounds[2] - bounds[0] + 2 * margin)).Append(' ').Append(Number(bounds[3] - bounds[1] + 2 * margin)).Append("\">");
      foreach (var group in _groups.Where(group => !group.Empty)) Write(svg, group);
      if (!_loose.Empty) Write(svg, _loose);
      return svg.Append("</svg>").ToString();
    }

    private static void Write(StringBuilder svg, Group group)
    {
      svg.Append("<g");
      if (group.Key != null) svg.Append(" data-key=\"").Append(Escape(group.Key)).Append('"');
      if (group.ElementId != null) svg.Append(" data-element-id=\"").Append(Escape(group.ElementId)).Append('"');
      if (group.Category != null) svg.Append(" data-category=\"").Append(Escape(group.Category)).Append('"');
      svg.Append('>').Append(group.Fills);
      foreach (var pair in group.Strokes)
      {
        var style = pair.Key.Split('|');
        svg.Append("<path d=\"").Append(pair.Value).Append("\" fill=\"none\" stroke=\"").Append(style[0])
          .Append("\" stroke-width=\"").Append(style[1]).Append("\" stroke-linecap=\"round\" vector-effect=\"non-scaling-stroke\"/>");
      }
      svg.Append(group.Texts).Append("</g>");
    }

    private void Grow((double U, double V) point)
    {
      _minU = Math.Min(_minU, point.U); _maxU = Math.Max(_maxU, point.U);
      _minV = Math.Min(_minV, point.V); _maxV = Math.Max(_maxV, point.V);
    }
    private void Account(int characters)
    {
      _size += characters;
      if (_size > MaxBytes) throw new InvalidOperationException($"El dibujo de la vista supera {MaxBytes / (1024 * 1024)} MiB; reduce su alcance o detalle.");
    }
    // 1 mm is below anything a plan shows and keeps coordinates short.
    private static (double U, double V) Round((double U, double V) point) => (Finite(Math.Round(point.U, 3)), Finite(Math.Round(point.V, 3)));
    private static double Finite(double value) => NetCompat.IsFinite(value) && Math.Abs(value) <= 1e7 ? value
      : throw new InvalidOperationException("Dibujo con coordenadas fuera de los límites del visor.");
    internal static string Number(double value) => (value == 0 ? 0 : value).ToString("0.###", CultureInfo.InvariantCulture);
    private static string Color(string color) => color is { Length: 7 } && color[0] == '#' && color.Skip(1).All(Uri.IsHexDigit) ? color : "#000000";
    internal static string Escape(string text)
    {
      var result = new StringBuilder(text.Length);
      foreach (var c in text)
      {
        // XML 1.0 forbids most control characters; they never belong in a drawing label.
        if (c < 0x20 && c != '\t') continue;
        result.Append(c switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", '\'' => "&apos;", _ => c.ToString() });
      }
      return result.ToString();
    }
  }
}
