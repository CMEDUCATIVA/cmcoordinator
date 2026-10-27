using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
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
    private static Drawings.DrawingScheduler _drawings;
    // Last 3D capture handed to the worker; a redrawn 2D view travels with it without recapturing the model.
    private static LiveSnapshot _lastSnapshot;
    private static SyncWorker _worker;
    private static SyncTelemetry _telemetry;
    private static SyncPanelView _panel;
    private static ExternalEvent _external;
    private static ExternalEvent _captureEvent;
    private static System.Threading.Timer _wakeTimer;
    private static volatile bool _needsCapture;
    // Only Sincronizar arms the capture; connecting merely records which elements changed.
    private static volatile bool _captureArmed;
    // After the first Sincronizar the model is live: each edit is captured and published on its own
    // until Pausar, a new connection or a failed capture.
    private static volatile bool _live;
    private static bool LiveMode { get => _live; set { _live = value; if (_worker != null) _worker.Live = value; } }
    private static bool _reconciling, _resumeAfterCapture;
    private static string _message;
    private static string _documentPath;
    private static string _destinationKey;
    private static CdeDestination _remote;
    private static CdeDestination _rememberedDestination;
    private static int _restoringHistory;
    private static int _syncRequested;
    private static DateTime _nextHistoryProbe;
    public static string DestinationLabel => _remote?.Label ?? "CDE no conectado";
    public static string DestinationProjectName => _remote?.ProjectName;
    public static string DestinationModelName => _remote?.ModelName;
    public static bool IsCdeConnected => _remote != null && _worker != null && IsCapturedDocumentActive && _destinationKey == _remote.QueueKey;
    public static bool IsRevitActive => _application?.ActiveUIDocument?.Document?.IsValidObject == true;
    private static bool IsCapturedDocumentActive => _worker != null && _document?.IsValidObject == true &&
      Equals(_application?.ActiveUIDocument?.Document, _document);
    public static bool IsRevitProcessing => IsRevitActive && Equals(_application.ActiveUIDocument.Document, _document) &&
      (IsCapturing || _worker?.IsPreparing == true);
    private static bool IsCapturing => _captureArmed && _capture?.HasWork == true;
    public static bool IsSyncPreparing => IsCapturing || _worker?.IsPreparing == true || _worker?.IsAwaitingSnapshot == true;
    public static bool IsSyncRequested => Volatile.Read(ref _syncRequested) != 0;
    public static void ConnectDestination(CdeDestination destination)
    {
      if (destination != null && !Entry.CdeAccessController.MatchesDestination(destination.Address, destination.UserId))
        throw new InvalidOperationException("La conexión no corresponde a la instancia y usuario de tu sesión CDE.");
      _remote = _rememberedDestination = destination; Request("connect");
    }
    private static string _modelLabel = "Sin modelo conectado";
    public static string ModelLabel => IsRevitActive && !IsCapturedDocumentActive
      ? _application.ActiveUIDocument.Document.Title + " · " + (_application.ActiveUIDocument.ActiveView?.Name ?? "") : _modelLabel;
    public static string ModelTitle => _application?.ActiveUIDocument?.Document?.Title ?? "Sin modelo abierto";
    public static string CaptureDescription => IsRevitActive && _worker != null && !IsCapturedDocumentActive
      ? "Documento activo distinto; pulsa Sincronizar para capturarlo" : _message ?? _capture?.Description ?? "Captura inactiva";
    public static int? CapturedElements => IsCapturedDocumentActive ? _capture?.CapturedElements : null;
    public static int CaptureProgressDone => IsCapturedDocumentActive ? _capture?.ProgressDone ?? 0 : 0;
    public static int CaptureProgressTotal => IsCapturedDocumentActive ? _capture?.ProgressTotal ?? 0 : 0;
    public static bool IsCaptureStopped => _capture?.Stopped == true;
    public static bool HasSyncWorker => IsCapturedDocumentActive;
    public static string Activity => (_telemetry?.WriteError ?? "") + Environment.NewLine + (_telemetry?.Recent ?? "Sin actividad");
    public static SyncProgress Progress => _worker != null && !IsCapturedDocumentActive
      ? new SyncProgress { Stage = "Documento distinto", Detail = "Pulsa Sincronizar para capturar el RVT activo" }
      : _worker?.Progress ?? new SyncProgress();
    public static string ActiveHistoryDirectory
    {
      get
      {
        var document = _application?.ActiveUIDocument?.Document;
        if (document?.IsValidObject != true) return null;
        if (IsCapturedDocumentActive) return _worker.DirectoryPath;
        var destination = _remote ?? _rememberedDestination;
        // A fresh installation has no saved destination yet: there is no history to show.
        if (destination == null || destination.UserId <= 0 || !Entry.CdeAccessController.MatchesDestination(destination.Address, destination.UserId)) return null;
        return Path.Combine(Root, "outbox", destination.QueueKey, SourceIdentity.Read(document));
      }
    }
    public static bool IsRunning => _worker != null;
    public static bool CanChooseDrawings => IsCapturedDocumentActive && _drawings != null;
    public static string DrawingStatus
    {
      get
      {
        if (!CanChooseDrawings) return "Sincroniza el modelo para elegir sus vistas 2D";
        if (_drawings.SelectedCount == 0) return "Ninguna vista 2D elegida";
        var progress = _worker.Progress;
        var text = $"{_drawings.SelectedCount} vistas · {_drawings.DrawnCount} dibujadas" +
          (_drawings.PendingCount > 0 ? $" · {_drawings.PendingCount} por dibujar" : "");
        var delivery = !progress.DrawingsSupported ? "Guardadas en local · el CDE aún no admite planos"
          : progress.DrawingsPending ? "Pendientes de envío al CDE" : "Enviadas al CDE con la revisión";
        return text + Environment.NewLine + delivery + (_drawings.LastError == null ? "" : Environment.NewLine + "No se pudo dibujar " + _drawings.LastError);
      }
    }

    public static void Initialize(UIControlledApplication application, PushButton button)
    {
      _telemetry = new SyncTelemetry(Path.Combine(Root, "logs"));
      _rememberedDestination = CdeCredentialVault.Load();
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
      if (_remote == null)
        throw new InvalidOperationException("Conecta y valida un destino CDE antes de sincronizar.");
      if (document == null || app.ActiveUIDocument.ActiveView is not View3D view || view.IsTemplate || document.IsFamilyDocument)
        throw new InvalidOperationException("Abre una vista 3D de un proyecto para iniciar la sincronización.");
      if (_worker != null && _destinationKey == _remote.QueueKey && _document?.IsValidObject == true && Equals(document, _document) && _capture.IsValid && _capture.ViewId == view.Id && document.PathName == _documentPath)
      { _application = app; Show(app); return; }
      var source = SourceIdentity.Read(document);
      var previous = _worker; previous?.Dispose();
      _document = document; _application = app;
      _documentPath = document.PathName;
      _destinationKey = _remote.QueueKey;
      var queue = Path.Combine(Root, "outbox", _destinationKey, source);
      var receiver = new SyncReceiver(destination: _remote);
      _worker = new SyncWorker(queue, receiver, _telemetry, previous?.Completion);
      _capture = new CaptureScheduler(document, view, source, _telemetry, Path.Combine(Root, "logs"));
      _drawings = new Drawings.DrawingScheduler(document, queue, _telemetry);
      _lastSnapshot = null;
      _captureArmed = false; _needsCapture = false; LiveMode = false;
      _modelLabel = document.Title + " · " + view.Name; _reconciling = false; _resumeAfterCapture = false;
      _message = "Conectado · pulsa Sincronizar para capturar y publicar la vista 3D";
      _telemetry.Emit("session", "SESSION_OPEN", "Sesión local abierta; la captura empieza al pulsar Sincronizar");
      Show(app);
    }
    public static void Show(UIApplication app)
    { _application = app; _panel.ApplyTheme(UIThemeManager.CurrentTheme == UITheme.Dark); app.GetDockablePane(PaneId).Show(); }
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
    {
      if (string.IsNullOrEmpty(action)) return;
      if (action == "sync" && Interlocked.CompareExchange(ref _syncRequested, 1, 0) != 0) return;
      Actions.Enqueue(action); _external?.Raise();
    }
    public static void RestoreHistoryDestination()
    {
      if (_rememberedDestination == null || _rememberedDestination.UserId > 0 || _remote != null ||
          DateTime.UtcNow < _nextHistoryProbe || Interlocked.CompareExchange(ref _restoringHistory, 1, 0) != 0) return;
      var candidate = _rememberedDestination;
      if (!Entry.CdeAccessController.MatchesOrigin(candidate.Address))
      { Interlocked.Exchange(ref _restoringHistory, 0); return; }
      _nextHistoryProbe = DateTime.UtcNow.AddMinutes(1);
      _ = Task.Run(async () =>
      {
        try
        {
          using var receiver = new SyncReceiver(destination: candidate);
          using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
          var verified = candidate.Verified(await receiver.StatusAsync(timeout.Token).ConfigureAwait(false));
          if (Entry.CdeAccessController.MatchesDestination(verified.Address, verified.UserId) && _remote == null)
          { _rememberedDestination = verified; CdeCredentialVault.Save(verified); }
        }
        catch (Exception) { /* History lookup is optional; manual connection remains available. */ }
        finally { Interlocked.Exchange(ref _restoringHistory, 0); }
      });
    }
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
            _worker?.Pause(true); _captureArmed = false; _needsCapture = false; LiveMode = false;
            // A new token for the same address also needs a fresh transport instance.
            _destinationKey = null; Start(app); continue;
          }
          if (action == "sync" && _remote == null)
          { _message = "Conecta y valida un destino CDE antes de sincronizar."; Show(app); continue; }
          // A different document or destination gets a fresh session, then this same click synchronizes it.
          if (action == "sync" && (!IsCapturedDocumentActive || _destinationKey != _remote.QueueKey)) Start(app);
          if (action == "viewer")
          {
            if (_remote == null) _message = "Conecta un destino CDE para abrir su visor.";
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
          if (action == "drawings") { ChooseDrawings(); continue; }
          if (action == "drawingsFolder") { OpenDrawingsFolder(); continue; }
          if (_worker == null)
          { if (action == "reset") Start(app); else _message = "No hay una sesión de sincronización activa"; continue; }
          if (action == "stop") { _captureArmed = false; _needsCapture = false; LiveMode = false; _capture.Stop(); _worker.HoldForReconciliation(); _message = null; }
          else if (action == "pause")
          {
            if (IsCapturing) PauseCapture();
            else if (_capture.Stopped) Synchronize();
            else if (_worker.IsPaused) ResumeLive();
            else { LiveMode = false; _worker.Pause(true); _message = "En pausa · los cambios se guardan; pulsa Reanudar para volver a tiempo real"; }
          }
          else if (action is "sync" or "retry") Synchronize();
          else if (action == "reset") { _message = null; Reconcile(); _drawings?.RedrawAll(); _captureArmed = true; _needsCapture = true; }
        }
        catch (Exception error) { _message = error.Message; _telemetry.Emit("control", "ACTION_FAILED", "No se pudo ejecutar la acción: " + SyncFailure.Describe(error)); }
        finally { if (action == "sync") Interlocked.Exchange(ref _syncRequested, 0); }
      }
    }
    private static void Reconcile()
    { _worker.HoldForReconciliation(); _capture.RequestFull(); _reconciling = true; _resumeAfterCapture = true; _message = null; _needsCapture = _captureArmed; }
    // Captures pending changes (continuing an interrupted capture where it stopped) and then publishes them.
    private static void Synchronize()
    {
      _message = null;
      _worker.RequestPublish();
      if (_capture.Failed || _capture.Stopped || !_capture.HasWork && !_worker.Progress.HasPendingChanges && _worker.Progress.JobId == null) Reconcile();
      if (_capture.HasWork) _worker.AwaitSnapshot();
      _captureArmed = true; _needsCapture = _capture.HasWork; LiveMode = true;
    }
    // Reanudar returns to real time and sends what accumulated, without a full recapture.
    private static void ResumeLive()
    {
      _message = null; _worker.Pause(false); _worker.RequestPublish(); LiveMode = true;
      if (_capture.HasWork) { _worker.AwaitSnapshot(); _captureArmed = true; _needsCapture = true; }
    }
    private static void PauseCapture()
    {
      _captureArmed = false; _needsCapture = false; LiveMode = false; _worker.Pause(true);
      _message = "Captura en pausa · pulsa Sincronizar para continuar desde donde quedó";
    }
    private static void ChooseDrawings()
    {
      if (!CanChooseDrawings) { _message = "Sincroniza el modelo para elegir sus vistas 2D"; return; }
      var views = Drawings.DrawingScheduler.DrawableViews(_document)
        .Select(view => new Presentation.DrawingChoice(view.UniqueId, view.Name, view.ViewType.ToString())).ToList();
      var chosen = Presentation.DrawingPickerWindow.Ask(views, _drawings.SelectedViewIds, UIThemeManager.CurrentTheme == UITheme.Dark);
      if (chosen == null) return;
      _drawings.Select(chosen);
      // Drawings of a model already live are drawn right away; otherwise with the next Sincronizar.
      if (_lastSnapshot != null) _worker.Submit(_drawings.Attach(_lastSnapshot));
      _needsCapture = DrawingsDue;
    }
    private static void OpenDrawingsFolder()
    {
      if (_drawings == null) return;
      Directory.CreateDirectory(_drawings.FolderPath);
      Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_drawings.FolderPath}\"") { UseShellExecute = true });
    }
    // 2D views are drawn once the 3D capture is up to date, while the session is live or a publication waits.
    private static bool DrawingsDue => _drawings?.HasWork == true && _lastSnapshot != null && _worker?.IsPaused == false &&
      !(_captureArmed && _capture?.HasWork == true) && (LiveMode || _worker.IsPublishRequested);
    private static void TickDrawings()
    {
      if (!DrawingsDue || !_drawings.Tick()) return;
      _worker.Submit(_drawings.Attach(_lastSnapshot));
    }
    private static void OnChanged(object sender, DocumentChangedEventArgs args)
    {
      if (_document?.IsValidObject != true) return;
      _capture?.Changed(args);
      _drawings?.Changed(args);
      // Live mode captures the edit by itself; the scheduler waits for a short calm to group consecutive edits.
      if (LiveMode && _capture?.HasWork == true && _worker?.IsPaused == false) _captureArmed = true;
      _needsCapture = IsCapturing || DrawingsDue;
    }
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
        _modelLabel = _document.Title + " · " + _document.GetElement(_capture.ViewId)?.Name;
        _telemetry.Emit("session", "DOCUMENT_RELOCATED", "Ruta actualizada; identidad y cola conservadas");
        if (_capture.Stopped) { _needsCapture = false; return; }
        var resume = !_worker.IsPaused;
        Reconcile(); _resumeAfterCapture = resume;
      }
      if (_captureArmed)
      {
        var snapshot = _capture.Tick();
        // A failed capture releases Sincronizar instead of leaving the publication request pending.
        if (_capture.Failed) { _captureArmed = false; _needsCapture = false; LiveMode = false; _worker.CancelPublish(); return; }
        if (snapshot != null)
        {
          _lastSnapshot = snapshot;
          _worker.Submit(_drawings.Attach(snapshot), _reconciling); _reconciling = false;
          _captureArmed = _capture.HasWork;
          // In live mode an edit is published as soon as it is captured; only the changed elements travel.
          if (LiveMode && !_worker.IsPublishRequested) _worker.RequestPublish();
          if (_resumeAfterCapture) { _resumeAfterCapture = false; _worker.Pause(false); }
        }
      }
      TickDrawings();
      _needsCapture = _captureArmed && _capture.HasWork || DrawingsDue;
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
