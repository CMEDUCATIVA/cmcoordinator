using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using OpenProject.Revit.LiveSync.Telemetry;

namespace OpenProject.Revit.LiveSync.Capture
{
  internal sealed class CaptureScheduler
  {
    private readonly Document _document;
    private readonly View3D _view;
    private readonly string _source;
    private readonly ISyncTelemetrySink _log;
    private readonly ElementGeometryReader _reader = new();
    private readonly Queue<ElementId> _queue = new();
    private readonly HashSet<ElementId> _queued = new();
    private readonly HashSet<Document> _links = new();
    private HashSet<ElementId> _visible = new();
    private bool _visibilityDirty;
    private readonly Dictionary<ElementId, List<string>> _rootKeys = new();
    private Dictionary<string, LiveElement> _elements = new(StringComparer.Ordinal);
    private bool _full = true, _changed, _failed;
    private int _processed, _total, _vertices;
    private DateTime _lastChange = DateTime.MinValue;
    private DateTime _firstChange = DateTime.MinValue;
    private Stopwatch _captureClock = Stopwatch.StartNew();
    public bool Stopped { get; private set; }
    public string Description { get; private set; } = "Preparando captura inicial";
    public bool Failed => _failed;
    public bool IsValid => _document.IsValidObject && _view.IsValidObject;
    public ElementId ViewId => _view.Id;
    public bool HasWork => !Stopped && !_failed && (_full || _queue.Count > 0 || _changed);
    public CaptureScheduler(Document document, View3D view, string source, ISyncTelemetrySink log)
    { _document = document; _view = view; _source = source; _log = log; }

    public void Changed(DocumentChangedEventArgs args)
    {
      var document = args.GetDocument();
      if (!Equals(document, _document)) { if (_links.Contains(document)) RequestFull(); return; }
      _lastChange = DateTime.UtcNow;
      if (_queue.Count == 0) _firstChange = _lastChange;
      _visibilityDirty = true;
      if (Stopped) { _full = true; return; }
      foreach (var id in args.GetAddedElementIds().Concat(args.GetModifiedElementIds()))
      {
        if (document.GetElement(id) is ElementType or Material or View or RevitLinkInstance or Level or Phase or ProjectInfo or ProjectLocation) _full = true;
        Enqueue(id);
      }
      foreach (var id in args.GetDeletedElementIds())
      { if (!_rootKeys.ContainsKey(id)) _full = true; Enqueue(id); }
    }
    public void Stop() { Stopped = true; Description = "Captura detenida · se reconciliará al continuar"; }
    public void RequestFull() { _full = true; _failed = false; Stopped = false; _lastChange = DateTime.MinValue; }
    private void Enqueue(ElementId id) { if (!_changed) _captureClock.Restart(); if (_queued.Add(id)) { _queue.Enqueue(id); _changed = true; } }

    public LiveSnapshot Tick()
    {
      if (Stopped || _failed) return null;
      try
      {
        if (_full) Begin();
        // Read current visibility once per cycle, rather than traversing the whole export once per element.
        if (_queue.Count == 0) return Finish();
        if ((DateTime.UtcNow - _lastChange).TotalMilliseconds < 500 && (DateTime.UtcNow - _firstChange).TotalSeconds < 2) return null;
        if (_visibilityDirty)
        { _visible = new(new FilteredElementCollector(_document, _view.Id).WhereElementIsNotElementType().ToElementIds()); _visibilityDirty = false; }
        var watch = Stopwatch.StartNew();
        var count = 0;
        while (_queue.Count > 0 && count < 12 && watch.ElapsedMilliseconds < 40)
        {
          var id = _queue.Dequeue(); _queued.Remove(id);
          var one = Stopwatch.StartNew();
          var captured = _visible.Contains(id) ? _reader.Read(_document, _view, id) : new Dictionary<string, LiveElement>();
          if (_rootKeys.Remove(id, out var keys))
            foreach (var key in keys)
              if (_elements.Remove(key, out var old)) _vertices -= Vertices(old);
          foreach (var pair in captured) { _elements[pair.Key] = pair.Value; _vertices += Vertices(pair.Value); }
          _rootKeys[id] = captured.Keys.ToList();
          if (_elements.Count > 20000 || _vertices > 750000)
            throw new InvalidOperationException("La vista excede 20.000 elementos o 750.000 vértices; reduce su alcance.");
          if (one.ElapsedMilliseconds > 100)
            _log.Emit("capture", "SLOW_CAPTURE_UNIT", "Una unidad geométrica superó el presupuesto; considera reducir detalle o vínculos", durationMs: one.ElapsedMilliseconds);
          _processed++; count++;
        }
        Description = $"Preparando geometría · {_processed}/{Math.Max(_total, _processed + _queue.Count)} unidades · {_elements.Count} elementos";
        return _queue.Count == 0 ? Finish() : null;
      }
      catch (Exception error)
      {
        _failed = true; Description = "Error de captura: " + error.Message;
        _log.Emit("capture", "CAPTURE_FAILED", "Falló la captura: " + SyncFailure.Describe(error));
        return null;
      }
    }
    private void Begin()
    {
      _captureClock.Restart();
      _queue.Clear(); _queued.Clear(); _elements = new(StringComparer.Ordinal); _rootKeys.Clear(); _links.Clear(); _vertices = 0;
      _visible = new(new FilteredElementCollector(_document, _view.Id).WhereElementIsNotElementType().ToElementIds());
      foreach (var id in _visible) Enqueue(id);
      _visibilityDirty = false;
      foreach (RevitLinkInstance link in new FilteredElementCollector(_document).OfClass(typeof(RevitLinkInstance)))
      { var linked = link.GetLinkDocument(); if (linked != null) _links.Add(linked); }
      _full = false; _changed = true; _processed = 0; _total = _queue.Count;
      Description = $"Preparando geometría · {_total} unidades";
    }
    private LiveSnapshot Finish()
    {
      if (!_changed) return null;
      _changed = false; Description = $"Captura al día · {_elements.Count} elementos";
      _captureClock.Stop();
      _log.Emit("capture", "CAPTURE_READY", "Captura completada y entregada al almacenamiento local", durationMs: _captureClock.ElapsedMilliseconds, elements: _elements.Count);
      return new LiveSnapshot(_source, _document.Title, _view.Name, _elements, Bim.BimCatalog.Read(_document, _view, _elements.Values));
    }
    private static int Vertices(LiveElement element) => element.Meshes.Sum(mesh => mesh.Positions.Length / 3);
  }
}
