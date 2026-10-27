using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using OpenProject.Revit.LiveSync.Telemetry;

namespace OpenProject.Revit.LiveSync.Drawings
{
  // Keeps the chosen 2D views drawn while the model changes. A view is redrawn when it holds a changed or deleted
  // element, when it changes itself, or (all of them) when elements are added or types, levels or phases change.
  // One view is drawn per idle cycle, after the same short calm the 3D capture waits for.
  internal sealed class DrawingScheduler
  {
    private static readonly HashSet<ViewType> Drawable = new()
    {
      ViewType.FloorPlan, ViewType.CeilingPlan, ViewType.AreaPlan, ViewType.EngineeringPlan,
      ViewType.Section, ViewType.Elevation, ViewType.Detail, ViewType.DraftingView, ViewType.Legend
    };
    private readonly Document _document;
    private readonly string _queueDirectory;
    private readonly ISyncTelemetrySink _log;
    private readonly Dictionary<string, LiveDrawing> _drawings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<ElementId>> _contents = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);
    private HashSet<string> _selected;
    private DateTime _lastChange = DateTime.MinValue;
    public int SelectedCount => _selected.Count;
    public int PendingCount => _dirty.Count;
    public int DrawnCount => _drawings.Count;
    public bool HasWork => _dirty.Count > 0;
    public string FolderPath => Path.Combine(_queueDirectory, "vistas2d");
    public IReadOnlyCollection<string> SelectedViewIds => _selected;
    public string LastError { get; private set; }

    public DrawingScheduler(Document document, string queueDirectory, ISyncTelemetrySink log)
    {
      _document = document; _queueDirectory = queueDirectory; _log = log;
      _selected = new HashSet<string>(DrawingSelection.Load(queueDirectory), StringComparer.Ordinal);
      _dirty.UnionWith(_selected);
    }

    public static bool CanDraw(View view) => view != null && !view.IsTemplate && Drawable.Contains(view.ViewType);

    public static IReadOnlyList<View> DrawableViews(Document document) =>
      new FilteredElementCollector(document).OfClass(typeof(View)).Cast<View>().Where(CanDraw)
        .OrderBy(view => view.ViewType.ToString()).ThenBy(view => view.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public void Select(IEnumerable<string> viewIds)
    {
      var chosen = new HashSet<string>(viewIds, StringComparer.Ordinal);
      DrawingSelection.Save(_queueDirectory, chosen);
      foreach (var removed in _selected.Except(chosen).ToList())
      { _drawings.Remove(LiveDrawing.KeyFor(removed)); _contents.Remove(removed); _dirty.Remove(removed); }
      foreach (var added in chosen.Except(_selected)) _dirty.Add(added);
      _selected = chosen;
      _lastChange = DateTime.MinValue;
      _log.Emit("drawing", "DRAWING_SELECTED", $"Vistas 2D elegidas: {chosen.Count}");
    }

    public void Changed(DocumentChangedEventArgs args)
    {
      if (_selected.Count == 0 || !Equals(args.GetDocument(), _document)) return;
      var touched = false;
      if (args.GetAddedElementIds().Count > 0) { _dirty.UnionWith(_selected); touched = true; }
      foreach (var id in args.GetModifiedElementIds().Concat(args.GetDeletedElementIds()))
      {
        var element = _document.GetElement(id);
        // Types, levels, phases, materials and view templates can change how every chosen view looks.
        if (element is ElementType or Level or Phase or Material or ProjectInfo || element is View { IsTemplate: true })
        { _dirty.UnionWith(_selected); touched = true; break; }
        var uniqueId = element?.UniqueId;
        foreach (var view in _selected)
          if (uniqueId == view || _contents.TryGetValue(view, out var ids) && ids.Contains(id))
          { _dirty.Add(view); touched = true; }
      }
      if (touched) _lastChange = DateTime.UtcNow;
    }

    public void RedrawAll() { _dirty.UnionWith(_selected); _lastChange = DateTime.MinValue; }

    // Draws one pending view; true when the set of drawings changed.
    public bool Tick()
    {
      if (_dirty.Count == 0 || (DateTime.UtcNow - _lastChange).TotalMilliseconds < 2000) return false;
      var viewId = _dirty.First(); _dirty.Remove(viewId);
      var key = LiveDrawing.KeyFor(viewId);
      if (_document.GetElement(viewId) is not View view || !CanDraw(view))
      { _contents.Remove(viewId); return _drawings.Remove(key); }
      var watch = Stopwatch.StartNew();
      try
      {
        var writer = DrawingExportContext.Draw(_document, view, out var drawn);
        var drawing = new LiveDrawing
        {
          Id = key, ViewId = view.UniqueId, ElementId = view.Id.ToString(), Name = view.Name, ViewType = view.ViewType.ToString(),
          Scale = view.Scale, LevelId = (view as ViewPlan)?.GenLevel?.UniqueId,
          Origin = Metres(view.Origin), Right = Unit(view.RightDirection), Up = Unit(view.UpDirection), Direction = Unit(view.ViewDirection),
          Bounds = writer.Bounds, Elements = writer.Keys.ToList(), Svg = writer.ToSvg()
        };
        _drawings[key] = drawing; _contents[viewId] = drawn; LastError = null;
        WriteLocalCopy(drawing);
        watch.Stop();
        _log.Emit("drawing", "DRAWING_EXPORTED", $"Vista 2D «{view.Name}» dibujada · {drawing.Elements.Count} elementos",
          durationMs: watch.ElapsedMilliseconds, bytes: Encoding.UTF8.GetByteCount(drawing.Svg), elements: drawing.Elements.Count);
        if (watch.ElapsedMilliseconds > 2000)
          _log.Emit("drawing", "DRAWING_SLOW", $"La vista «{view.Name}» tardó más de 2 s en dibujarse; considera reducir su alcance", durationMs: watch.ElapsedMilliseconds);
        return true;
      }
      // A view that cannot be drawn keeps its previous drawing and never stops the 3D synchronization.
      catch (Exception error) when (error is not OutOfMemoryException)
      {
        LastError = $"«{view.Name}»: {error.Message}";
        _log.Emit("drawing", "DRAWING_FAILED", $"No se pudo dibujar la vista «{view.Name}»: " + SyncFailure.Describe(error));
        return false;
      }
    }

    public LiveSnapshot Attach(LiveSnapshot snapshot) =>
      snapshot?.WithDrawings(new Dictionary<string, LiveDrawing>(_drawings, StringComparer.Ordinal));

    // A copy next to the queue to check the drawing in a browser while the CDE cannot show it yet.
    private void WriteLocalCopy(LiveDrawing drawing)
    {
      try
      {
        Directory.CreateDirectory(FolderPath);
        var name = new string(drawing.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
        var suffix = drawing.ViewId.Length > 8 ? drawing.ViewId.Substring(drawing.ViewId.Length - 8) : drawing.ViewId;
        File.WriteAllText(Path.Combine(FolderPath, $"{name} ({drawing.ViewType} {suffix}).svg"), drawing.Svg, new UTF8Encoding(false));
      }
      catch (Exception error) when (error is IOException or UnauthorizedAccessException)
      { _log.Emit("drawing", "DRAWING_COPY_FAILED", "No se pudo guardar la copia local del dibujo: " + error.Message); }
    }

    private static double[] Metres(XYZ point) => new[] { Math.Round(point.X * 0.3048, 4), Math.Round(point.Y * 0.3048, 4), Math.Round(point.Z * 0.3048, 4) };
    private static double[] Unit(XYZ vector) => new[] { Math.Round(vector.X, 6), Math.Round(vector.Y, 6), Math.Round(vector.Z, 6) };
  }
}
