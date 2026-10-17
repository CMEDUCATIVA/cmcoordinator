using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using OpenProject.Revit.LiveSync.Capture;
using OpenProject.Revit.LiveSync.Presentation;
using OpenProject.Revit.LiveSync.Queue;
using OpenProject.Revit.LiveSync.Telemetry;
using OpenProject.Revit.LiveSync.Transport;

namespace OpenProject.Revit.LiveSync
{
  public static class LocalLiveSync
  {
    public static readonly DockablePaneId PaneId = new(new Guid("E6A16318-60D2-46BF-BBC8-B573FCD00E6D"));
    private static readonly ConcurrentQueue<string> Actions = new();
    private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cmcoordinator", "LiveSync");
    private static Document _document;
    private static UIApplication _application;
    private static CaptureScheduler _capture;
    private static SyncWorker _worker;
    private static SyncTelemetry _telemetry;
    private static SyncPanelView _panel;
    private static ExternalEvent _external;
    private static ExternalEvent _captureEvent;
    private static System.Threading.Timer _wakeTimer;
    private static volatile bool _needsCapture;
    private static bool _reconciling, _resumeAfterCapture;
    private static string _message;
    private static string _documentPath;
    private static string _destinationKey;
    private static CdeDestination _remote;
    public static string DestinationLabel => _remote?.Label ?? "Visor local · protocolo 2";
    public static void ConnectDestination(CdeDestination destination)
    {
      if (destination != null && destination.UserId != Entry.CdeAccessController.SignedInUserId)
        throw new InvalidOperationException("La conexión pertenece a otro usuario. Genera un token con tu sesión CDE.");
      _remote = destination; Request("connect");
    }
    public static string ModelLabel { get; private set; } = "Sin modelo conectado";
    public static string CaptureDescription => _message ?? _capture?.Description ?? "Captura inactiva";
    public static string Activity => (_telemetry?.WriteError ?? "") + Environment.NewLine + (_telemetry?.Recent ?? "Sin actividad");
    public static SyncProgress Progress => _worker?.Progress ?? new SyncProgress();
    public static bool IsRunning => _worker != null;

    public static void Initialize(UIControlledApplication application, PushButton button)
    {
      _telemetry = new SyncTelemetry(Path.Combine(Root, "logs"));
      _panel = new SyncPanelView();
      application.RegisterDockablePane(PaneId, "Centro de sincronización", _panel);
      _external = ExternalEvent.Create(new ActionHandler());
      _captureEvent = ExternalEvent.Create(new CaptureHandler());
      _wakeTimer = new System.Threading.Timer(_ => {
        if (!_needsCapture) return;
        try { _captureEvent?.Raise(); } catch (Exception) { /* Revit may already be shutting down. */ }
      }, null, 150, 150);
      application.ControlledApplication.DocumentChanged += OnChanged;
      application.Idling += OnIdle;
      button.ItemText = "Centro de\nsincronización";
      button.ToolTip = "Abrir cola persistente, pausa, continuación y diagnóstico del visor local";
    }
    public static void Start(UIApplication app)
    {
      if (!Entry.CdeAccessController.CanUseLocalSync)
      { Entry.CdeAccessController.Open(app); return; }
      var document = app.ActiveUIDocument?.Document;
      if (document == null || app.ActiveUIDocument.ActiveView is not View3D view || view.IsTemplate || document.IsFamilyDocument)
        throw new InvalidOperationException("Abre una vista 3D de un proyecto para iniciar la sincronización.");
      if (_worker != null && _destinationKey == (_remote?.QueueKey ?? "local") && _document?.IsValidObject == true && Equals(document, _document) && _capture.IsValid && _capture.ViewId == view.Id && document.PathName == _documentPath)
      { _application = app; Show(app); return; }
      var source = SourceIdentity.Read(document);
      var previous = _worker; previous?.Dispose();
      _document = document; _application = app;
      _documentPath = document.PathName;
      _destinationKey = _remote?.QueueKey ?? "local";
      var queue = _remote == null ? Path.Combine(Root, "outbox", source) : Path.Combine(Root, "outbox", _destinationKey, source);
      var receiver = _remote == null ? new SyncReceiver(LocalServiceHost.EnsureAsync) : new SyncReceiver(destination: _remote);
      _worker = new SyncWorker(queue, receiver, _telemetry, previous?.Completion);
      _capture = new CaptureScheduler(document, view, source, _telemetry);
      _needsCapture = true;
      ModelLabel = document.Title + " · " + view.Name; _message = null; _reconciling = false; _resumeAfterCapture = false;
      _telemetry.Emit("session", "SESSION_OPEN", "Sesión local abierta; esperando captura cooperativa");
      Show(app); if (_remote == null) _ = OpenViewerAsync();
    }
    public static void Show(UIApplication app)
    { _panel.ApplyTheme(UIThemeManager.CurrentTheme == UITheme.Dark); app.GetDockablePane(PaneId).Show(); }
    public static string CdeSessionStatus { get; private set; } = "CDE pendiente de confirmar";
    public static void SetCdeSessionStatus(string status)
    {
      if (CdeSessionStatus == status) return;
      CdeSessionStatus = status;
      _telemetry?.Emit("access", "CDE_SESSION_STATE", status);
    }
    public static void HoldForAccountChange()
    { _worker?.Pause(true); _message = "Cambió la cuenta CDE · cola conservada; revisa y pulsa Sincronizar para continuar"; }
    public static void HoldForSignIn()
    { _worker?.Pause(true); _message = "Sesión CDE cerrada · cola conservada; inicia sesión y pulsa Sincronizar para continuar"; }
    public static void Request(string action)
    { if (!string.IsNullOrEmpty(action)) { Actions.Enqueue(action); _external?.Raise(); } }
    private static void ExecuteActions(UIApplication app)
    {
      while (Actions.TryDequeue(out var action))
      {
        try
        {
          if (!Entry.CdeAccessController.CanUseLocalSync)
          { Entry.CdeAccessController.Open(app); continue; }
          if (action == "connect")
          {
            _worker?.Pause(true);
            // A new token for the same address also needs a fresh transport instance.
            _destinationKey = null; Start(app); continue;
          }
          if (action == "sync" && _destinationKey != (_remote?.QueueKey ?? "local")) { Start(app); continue; }
          if (action == "viewer")
          {
            if (_remote == null) _ = OpenViewerAsync();
            else
            {
              Entry.RibbonButtonClickHandler.OpenBrowser(app);
              var projectId = _remote.Address.AbsolutePath.Split('/')[2];
              Entry.RibbonButtonClickHandler.IpcHandler.SendOpenUrlRequestToDesktopApp(
                new Uri(_remote.Address, "/projects/" + projectId + "/bcf").AbsoluteUri);
            }
            continue;
          }
          if (action == "export") { _ = ExportAsync(); continue; }
          if (_worker == null)
          { if (action is "sync" or "reset") Start(app); else _message = "No hay una sesión de sincronización activa"; continue; }
          if (action == "stop") { _capture.Stop(); _worker.HoldForReconciliation(); _message = null; }
          else if (action == "pause")
          { if (_capture.Stopped) Reconcile(); else _worker.Pause(!_worker.IsPaused); }
          else if (action == "sync" && !_capture.Stopped) { _message = null; _worker.Retry(); _worker.Pause(false); }
          else if (action is "sync" or "reset") Reconcile();
          else if (action == "retry")
          { _message = null; if (_capture.Failed) Reconcile(); else _worker.Retry(); }
        }
        catch (Exception error) { _message = error.Message; _telemetry.Emit("control", "ACTION_FAILED", "No se pudo ejecutar la acción: " + SyncFailure.Describe(error)); }
      }
    }
    private static void Reconcile()
    { _worker.HoldForReconciliation(); _capture.RequestFull(); _reconciling = true; _resumeAfterCapture = true; _message = null; _needsCapture = true; }
    private static void OnChanged(object sender, DocumentChangedEventArgs args)
    { if (_document?.IsValidObject == true) { _capture?.Changed(args); _needsCapture = _capture?.HasWork == true; } }
    private static void OnIdle(object sender, IdlingEventArgs args)
    { TickCapture(); }
    private static void TickCapture()
    {
      if (!Entry.CdeAccessController.CanUseLocalSync) return;
      if (_worker == null) return;
      if (_document?.IsValidObject != true)
      { if (!_capture.Stopped) { _worker.HoldForReconciliation(); _capture.Stop(); } _needsCapture = false; _message = "Documento cerrado · cola conservada; abre el modelo para reconciliar"; return; }
      if (!Equals(_application?.ActiveUIDocument?.Document, _document))
      { _message = "Captura en espera · vuelve al documento de origen"; _needsCapture = false; return; }
      if (_message?.StartsWith("Captura en espera", StringComparison.Ordinal) == true) _message = null;
      if (_document.PathName != _documentPath)
      {
        _documentPath = _document.PathName;
        ModelLabel = _document.Title + " · " + _document.GetElement(_capture.ViewId)?.Name;
        _telemetry.Emit("session", "DOCUMENT_RELOCATED", "Ruta actualizada; identidad y cola conservadas");
        if (_capture.Stopped) { _needsCapture = false; return; }
        var resume = !_worker.IsPaused;
        Reconcile(); _resumeAfterCapture = resume;
      }
      var snapshot = _capture.Tick(); _needsCapture = _capture.HasWork; if (snapshot == null) return;
      _worker.Submit(snapshot, _reconciling); _reconciling = false;
      if (_resumeAfterCapture) { _resumeAfterCapture = false; _worker.Pause(false); }
    }
    private static async Task OpenViewerAsync()
    {
      try
      {
        await LocalServiceHost.EnsureAsync().ConfigureAwait(false);
        Process.Start(new ProcessStartInfo(LocalViewerEndpoint.Url) { UseShellExecute = true });
      }
      catch (Exception error) { _telemetry.Emit("service", "VIEWER_START_FAILED", "No se pudo abrir el visor: " + SyncFailure.Describe(error)); }
    }
    private static async Task ExportAsync()
    {
      try
      {
        var path = await Task.Run(() => _telemetry.Export()).ConfigureAwait(false);
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
      }
      catch (Exception error) { _telemetry.Emit("diagnostic", "EXPORT_FAILED", "No se pudo exportar el diagnóstico: " + SyncFailure.Describe(error)); }
    }
    public static void Shutdown()
    { _needsCapture = false; _wakeTimer?.Dispose(); _worker?.Dispose(); _worker = null; _telemetry?.Dispose(); _external?.Dispose(); _captureEvent?.Dispose(); }
    private sealed class ActionHandler : IExternalEventHandler
    { public void Execute(UIApplication app) => ExecuteActions(app); public string GetName() => "Controles de sincronización local"; }
    private sealed class CaptureHandler : IExternalEventHandler
    { public void Execute(UIApplication app) { _application = app; TickCapture(); } public string GetName() => "Captura cooperativa del modelo"; }
  }
}
