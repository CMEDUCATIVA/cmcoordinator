using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using OpenProject.Revit.LiveSync.Queue;
using OpenProject.Revit.LiveSync.Transport;

namespace OpenProject.Revit.LiveSync.Presentation
{
  public enum CdeConnectionDisplayState { Disconnected, Validated, Connected, Paused, Waiting, Error, Revoked }
  public enum PanelNoticeKind { None, Info, Warning, Success, Danger }

  public sealed class SyncPanelViewModel : INotifyPropertyChanged
  {
    public event PropertyChangedEventHandler PropertyChanged;
    public string Model { get; private set; } = "Abre una vista 3D";
    public string DocumentTitle { get; private set; } = "Sin modelo abierto";
    public string RevitState { get; private set; } = "Revit inactivo";
    public Brush SyncIndicatorBrush { get; private set; } = Brushes.DarkGray;
    public string SyncIndicatorLabel => "Estado de sincronización: " + TransferTitle;
    public string ProjectName { get; private set; } = "Sin proyecto";
    public string DestinationName { get; private set; } = "Sin destino";
    public string ConnectionState { get; private set; } = "Sin conexión";
    public Brush ConnectionStateBrush { get; private set; } = Brushes.DarkGray;
    public string Capture { get; private set; } = "Captura inactiva";
    public string Activity { get; private set; } = "Sin actividad";
    public string CdeSession { get; private set; } = "CDE pendiente de confirmar";
    public string PauseLabel => ShowPlayIcon ? "Reanudar" : "Pausar";
    public bool ShowPlayIcon => Status.Paused && !IsCapturing || IsCaptureStopped;
    public bool CanSynchronize => IsRevoked || !IsLive && IsCdeConnected && Access.CanSynchronize && !SyncRequested && !SyncPreparing && !HasActiveTransfer && !IsNoPermission;
    // «En vivo» keeps the button at full colour (pressed look) although clicking it does nothing.
    public bool SyncButtonEnabled => CanSynchronize || IsLive;
    // One button, several states: what it does depends on the group the CDE gives this connection.
    public string SyncLabel => IsRevoked ? "Volver a conectar" : IsLive ? "En vivo" : HasActiveTransfer ? "Sincronizando…" : SyncRequested || SyncPreparing ? "Preparando…"
      : IsCdeConnected && Access.ReadOnly ? "Solo lectura" : IsCdeConnected && Access.CanCreate ? "Crear modelo"
      : Status.NeedsConfirmation ? "Confirmar sincronización…" : "Sincronizar";
    // The CDE no longer accepts this connection: the main button opens the connection form for a new link and token.
    public bool IsRevoked => IsCdeConnected && Status.Revoked;
    // UI-8 logic 4: without «Ver modelos IFC» nothing is sent; a new token would not help, so no «Volver a conectar».
    public bool IsNoPermission => IsCdeConnected && !IsRevoked && Status.NoPermission;
    // UI-8 #26: synchronized and in real time, the button stays pressed as «En vivo»; edits leave by themselves. It returns to
    // Sincronizar with a lost connection, an error, a pause or a revocation, so the person verifies before going on.
    public bool IsLive => IsCdeConnected && LiveModeOn && Status.Joined && !IsRevoked && !Access.ReadOnly && !Status.Attention && !Status.Paused
      && !Status.NeedsConfirmation && Status.Stage != "Esperando conexión";
    public string SyncHint => IsRevoked ? "Pega el enlace y el token nuevos que te dé Administración del modelo"
      : !IsCdeConnected ? "Conecta un destino CDE para sincronizar"
      : IsLive ? "En vivo: cada cambio de tu vista 3D se envía solo. Vuelve a Sincronizar si se pierde la conexión."
      : Access.ReadOnly ? "Tu grupo en este modelo es Lectura; el administrador del proyecto puede pasarte a Editor"
      : Access.CanCreate ? "Publica la primera captura completa de la vista 3D en el modelo vacío"
      : Status.NeedsConfirmation ? "Casi todo tu modelo difiere del CDE: confirma antes de enviarlo"
      : "Te une al modelo y envía solo lo que cambies desde que te conectaste; no reemplaza el modelo";
    public CdeModelAccess Access { get; private set; } = CdeModelAccess.Unknown;
    public string GroupLabel => Access.GroupName ?? "";
    // UI-8 #27: who is connected, with the group, under the route («Francis Vin · Administración»).
    public string IdentityName => Access.UserName ?? "Grupo en el modelo";
    public Visibility GroupVisibility => IsCdeConnected && Access.Known ? Visibility.Visible : Visibility.Collapsed;
    public string GroupHint => Access.Level switch
    {
      "admin" => "Sincroniza y puede reemplazar el modelo",
      "editor" => "Agrega y edita; no reemplaza",
      "reader" => "Ve el modelo; no envía cambios",
      _ => ""
    };
    private bool ReplaceAvailable => IsCdeConnected && !IsRevoked && !SyncRequested && !SyncPreparing && !HasActiveTransfer;
    // UI-8 #25: after choosing Sincronizar on this connection «Reemplazar» stays visible but off until a new connection.
    public bool CanReplace => ReplaceAvailable && Access.CanReplace && !SyncChosen;
    public string ReplaceHint => SyncChosen ? "Ya estás unido al modelo con Sincronizar. Para reemplazarlo, desconecta y vuelve a conectar."
      : "Quita el modelo del CDE y publica tu captura completa (queda un punto de restauración)";
    public Visibility ReplaceVisibility => IsCdeConnected && !IsRevoked && Access.CanReplace ? Visibility.Visible : Visibility.Collapsed;
    // One level of undo after a replacement: Administración returns the CDE model to what it replaced.
    public bool CanRestore => ReplaceAvailable && Access.CanRestore;
    public Visibility RestoreVisibility => IsCdeConnected && !IsRevoked && Access.CanRestore ? Visibility.Visible : Visibility.Collapsed;
    private enum Notice { None, Revoked, NoPermission, ReadOnly, Confirm, Empty, Promoted, Compared }
    // The most urgent notice wins: revoked, Lectura, a merge to confirm, empty model, the change to Editor, then the
    // comparison read when connecting, shown until this connection chooses what to do (UI-8 #24).
    private Notice Current => !IsCdeConnected ? Notice.None : IsRevoked ? Notice.Revoked : IsNoPermission ? Notice.NoPermission : Access.ReadOnly ? Notice.ReadOnly
      : Status.NeedsConfirmation ? Notice.Confirm : Access.CanCreate ? Notice.Empty : IsPromoted && Access.Known ? Notice.Promoted
      : Status.Comparison != null && !SyncChosen && !Status.Joined ? Notice.Compared : Notice.None;
    public PanelNoticeKind NoticeKind => Current switch
    {
      Notice.Revoked => PanelNoticeKind.Danger,
      Notice.NoPermission or Notice.ReadOnly or Notice.Confirm => PanelNoticeKind.Warning,
      Notice.Empty or Notice.Compared => PanelNoticeKind.Info,
      Notice.Promoted => PanelNoticeKind.Success,
      _ => PanelNoticeKind.None
    };
    public Visibility NoticeVisibility => NoticeKind == PanelNoticeKind.None ? Visibility.Collapsed : Visibility.Visible;
    public string NoticeTitle => Current switch
    {
      Notice.Revoked => "Conexión revocada",
      Notice.NoPermission => "Tu rol ya no puede ver modelos IFC",
      Notice.Promoted => "Ahora eres " + Access.GroupName,
      Notice.ReadOnly => "Solo lectura en este modelo",
      Notice.Confirm => "Tu Revit difiere del CDE en casi todo",
      Notice.Empty => "El modelo está vacío",
      Notice.Compared => ComparedTitle(Status.Comparison),
      _ => ""
    };
    public string NoticeText => Current switch
    {
      Notice.Revoked => "Administración retiró esta conexión o venció su token. Lo publicado se conserva en el CDE y tus cambios quedan guardados en este equipo. Pide un enlace y un token nuevos y pulsa Volver a conectar.",
      Notice.NoPermission => "Pídeselo a quien administre el proyecto. Cuando lo tengas, seguirá sola; tus cambios quedan guardados en este equipo.",
      Notice.Promoted => (Access.CanReplace ? "Sincronizas tus cambios y también puedes reemplazar el modelo." : "Ya puedes sincronizar: tus cambios se fusionan con los del resto sin reemplazar el modelo.")
        + (Status.HasPendingChanges ? " Tienes cambios guardados en este equipo: pulsa Sincronizar para enviarlos." : ""),
      Notice.ReadOnly => "Ves el modelo y su estado, pero no envías cambios. " + (Status.HasPendingChanges
        ? "Tus cambios quedan guardados en este equipo y se enviarán cuando el administrador del proyecto te pase a Editor."
        : "Pide al administrador del proyecto que te pase a Editor; Cmcoordinator lo detecta solo."),
      Notice.Confirm => SyncWorker.ConfirmDetail(Status.Comparison),
      Notice.Empty => "Crear modelo publica tu primera captura completa de la vista 3D. Después, cada cambio se sincroniza en tiempo real.",
      Notice.Compared => ComparedText(Status.Comparison, Access.CanReplace),
      _ => ""
    };
    // UI-8 #24: what connecting found, before anything is sent.
    public static string ComparedTitle(ReplacePreview c) => c == null ? "" : !c.Compared ? "Modelo con contenido en el CDE"
      : c.Matches ? "Coincide con el CDE" : "Tu Revit difiere del CDE";
    public static string ComparedText(ReplacePreview c, bool canReplace)
    {
      if (c == null) return "";
      var options = "Sincronizar te une al modelo y envía solo lo que cambies desde que te conectaste" + (canReplace ? "; Reemplazar quita el modelo y publica tu captura" : "") + "; Pausar no envía nada.";
      if (!c.Compared) return $"El CDE tiene {c.RemoteCount:N0} elementos" + (string.IsNullOrEmpty(c.LastPublishedBy) ? "" : $", última revisión de {c.LastPublishedBy}") +
        ". Al sincronizar, tu vista 3D se compara con ellos antes de enviar nada. " + options;
      if (c.Matches) return $"{c.Unchanged:N0} elementos iguales en tu Revit y en el CDE. " + options;
      return $"{c.Unchanged:N0} iguales · +{c.Added:N0} nuevos · {c.Changed:N0} cambiados · −{c.Removed:N0} que no tienes en tu Revit" +
        (c.Exact ? "" : " (cifras aproximadas)") + ". " + options;
    }
    // «Ahora eres Editor…» from the moment Lectura becomes Editor or Administración until the first synchronization (or 2 min).
    private string _lastLevel;
    private DateTime _promotedUntil;
    private bool IsPromoted { get; set; }
    public bool CanPause => HasSyncWorker;
    public bool CanStop => HasSyncWorker && !IsCaptureStopped;
    public bool CanRestartCapture => HasSyncWorker;
    public string TransferTitle { get; private set; } = "Detenido";
    public string TransferDetail { get; private set; } = "Abre una vista 3D para comenzar";
    public Brush TransferBrush { get; private set; } = Brushes.DarkGray;
    public Visibility TransferProgressVisibility { get; private set; } = Visibility.Collapsed;
    public bool TransferIndeterminate => TransferProgressVisibility == Visibility.Visible &&
      (IsCapturing ? CaptureTotal == 0 : Status.Total == 0);
    public int TransferPercent => IsCapturing ? (int)(100L * CaptureDone / Math.Max(1, CaptureTotal)) : Status.Percent;
    public double TransferFraction => TransferPercent / 100d;
    public string TransferPercentLabel => TransferProgressVisibility == Visibility.Visible ? $"{TransferPercent} %" : "";
    public string CapturedCount { get; private set; } = "—";
    private bool IsCaptureStopped { get; set; }
    private bool HasSyncWorker { get; set; }
    private bool IsCdeConnected { get; set; }
    private bool SyncRequested { get; set; }
    private bool SyncPreparing { get; set; }
    private bool LiveModeOn { get; set; }
    private bool SyncChosen { get; set; }
    private bool RevitProcessing { get; set; }
    private int CaptureDone { get; set; }
    private int CaptureTotal { get; set; }
    private bool IsCapturing => RevitProcessing && CaptureTotal > 0 && CaptureDone < CaptureTotal;
    private bool HasActiveTransfer => Status.JobId != null &&
      (Status.Stage is "Sincronizando" or "Enviando" or "Validando revisión");
    public string StoredRevision => Short(Status.ReceiverRevision);
    public string DisplayedRevision => Short(Status.ViewerRevision);
    public string BlockLabel => IsCapturing
      ? $"{CaptureDone:N0} de {CaptureTotal:N0} unidades del modelo · {TransferPercent} %"
      : Status.Stage == "Preparando paquetes" && Status.Total > 0
      ? $"{Status.Done:N0} de {Status.Total:N0} elementos · {Status.Percent} %" +
        (Status.BytesDone > 0 ? $" · {FormatBytes(Status.BytesDone)} preparados" : "")
      : Status.Total == 0 ? TransferTitle switch
      {
        "Procesando en CDE" => "Revisión recibida; aplicación en CDE pendiente",
        "Sincronizado" => "Revisión confirmada en CDE",
        _ => "Sin transferencia pendiente"
      }
      : $"{Status.Done} de {Status.Total} paquetes" + (Status.BytesTotal > 0 ? $" · {FormatBytes(Status.BytesDone)} de {FormatBytes(Status.BytesTotal)}" : "") + " · " + (Status.Stage is "En espera de publicación" or "En cola del CDE" ? "esperando turno de publicación" : Status.Attention ? "revisión no aceptada" : "revisión pendiente");
    public string PendingLabel => Status.HasPendingChanges ? $"{Status.PendingElements} elementos agrupados pendientes de envío" : "Sin cambios preparados pendientes";
    public SyncProgress Status { get; private set; } = new();
    private string _signature;
    public void Refresh(string model, string capture, SyncProgress status, string activity, string cdeSession = "CDE pendiente de confirmar",
      string documentTitle = null, bool revitActive = false, bool revitProcessing = false,
      int? capturedElements = null, bool captureStopped = false, bool cdeConnected = false, bool hasSyncWorker = false,
      bool syncRequested = false, bool syncPreparing = false, int captureDone = 0, int captureTotal = 0, CdeModelAccess access = null,
      bool liveMode = false, bool syncChosen = false)
    {
      documentTitle ??= model;
      access ??= CdeModelAccess.Unknown;
      if (access.Known && cdeConnected)
      {
        if (_lastLevel == "reader" && access.Level is "editor" or "admin") _promotedUntil = DateTime.UtcNow.AddMinutes(2);
        if (access.ReadOnly || syncRequested || status.JobId != null) _promotedUntil = DateTime.MinValue;
        _lastLevel = access.Level;
      }
      var promoted = DateTime.UtcNow < _promotedUntil;
      var state = !revitActive ? "Revit inactivo" : revitProcessing ? "Revit procesando" : "Revit activo";
      var signature = cdeSession + model + documentTitle + state + capture + capturedElements + captureStopped + cdeConnected + hasSyncWorker + syncRequested + syncPreparing
        + captureDone + "/" + captureTotal + access + access.UserName + promoted + status.Revoked + status.QueuePosition + (status.RestorePoint?.RevisionId)
        + liveMode + syncChosen + status.Joined + status.NeedsConfirmation + (status.Comparison == null ? "" : $"{status.Comparison.Compared}{status.Comparison.Local}/{status.Comparison.Added}/{status.Comparison.Changed}/{status.Comparison.Removed}")
        + status.Stage + status.Detail + status.JobId + status.Done + "/" + status.Total + status.BytesDone + "/" + status.BytesTotal + status.Paused + status.Attention
        + status.ReceiverRevision + status.ViewerRevision + status.HasPendingChanges + status.PendingElements + string.Join(";", status.Jobs.Select(j => j.Id + j.State + j.Detail));
      if (_signature != signature)
      {
        _signature = signature; Model = model; DocumentTitle = documentTitle; RevitState = state;
        Capture = capture; Status = status; CdeSession = cdeSession;
        IsCaptureStopped = captureStopped; HasSyncWorker = hasSyncWorker; IsCdeConnected = cdeConnected;
        SyncRequested = syncRequested; SyncPreparing = syncPreparing; RevitProcessing = revitProcessing;
        CaptureDone = captureDone; CaptureTotal = captureTotal; Access = access; IsPromoted = promoted;
        LiveModeOn = liveMode; SyncChosen = syncChosen;
        CapturedCount = capturedElements?.ToString("N0") ?? "—";
        var measured = IsCapturing || !captureStopped && status.JobId != null && status.Total > 0 &&
          status.Stage is not ("Al día" or "Procesando revisión");
        TransferProgressVisibility = measured || syncRequested || revitProcessing && cdeConnected ? Visibility.Visible : Visibility.Collapsed;
        if (!hasSyncWorker || captureStopped) (TransferTitle, TransferBrush) = ("Detenido", Brushes.DarkGray);
        else if (cdeConnected && status.Revoked) (TransferTitle, TransferBrush) = ("Conexión revocada", Brushes.OrangeRed);
        else if (status.Stage == "En espera de publicación") (TransferTitle, TransferBrush) = ("Esperando publicación", Brushes.Goldenrod);
        else if (status.Stage == "En cola del CDE")
          (TransferTitle, TransferBrush) = (status.QueuePosition > 0 ? $"En cola · posición {status.QueuePosition}" : "En cola del CDE", Brushes.Goldenrod);
        else if (cdeConnected && access.ReadOnly && !HasActiveTransfer) (TransferTitle, TransferBrush) = ("Solo lectura", Brushes.Goldenrod);
        else if (status.Attention) (TransferTitle, TransferBrush) = ("Error", Brushes.OrangeRed);
        else if (status.NeedsConfirmation && cdeConnected) (TransferTitle, TransferBrush) = (SyncWorker.ConfirmStage, Brushes.Goldenrod);
        else if (revitProcessing && cdeConnected) (TransferTitle, TransferBrush) = ("Preparando cambios", Brushes.Goldenrod);
        else if (status.Paused) (TransferTitle, TransferBrush) = ("Pausado", Brushes.Goldenrod);
        else if (HasActiveTransfer) (TransferTitle, TransferBrush) = ("Sincronizando", Brushes.DodgerBlue);
        else if (status.Stage == "Esperando captura" && cdeConnected && !syncPreparing)
          (TransferTitle, TransferBrush) = ("Listo para sincronizar", Brushes.DarkGray);
        else if (status.HasPendingChanges && cdeConnected) (TransferTitle, TransferBrush) = ("Cambios pendientes", Brushes.Goldenrod);
        else if (status.Stage == "Procesando revisión" && cdeConnected)
          (TransferTitle, TransferBrush) = ("Procesando en CDE", Brushes.Goldenrod);
        // The CDE validates and stores the revision through the authorised link; an open viewer is not required.
        else if (status.Stage == "Al día" && cdeConnected && !status.HasPendingChanges &&
          capturedElements.HasValue && !string.IsNullOrEmpty(status.ReceiverRevision))
          (TransferTitle, TransferBrush) = ("Sincronizado", Brushes.LimeGreen);
        else if (status.Stage == "Al día" && cdeConnected)
          (TransferTitle, TransferBrush) = ("Listo para sincronizar", Brushes.DarkGray);
        else if (status.Stage == "Al día" && !cdeConnected)
          (TransferTitle, TransferBrush) = ("Guardado local", Brushes.DarkGray);
        else (TransferTitle, TransferBrush) = (status.Stage, Brushes.DarkGray);
        TransferDetail = TransferTitle == "Sincronizado"
            ? status.ReceiverRevision == status.ViewerRevision ? "Revisión guardada en el CDE y mostrada en el visor" : "Revisión guardada en el CDE"
          : TransferTitle == "Procesando en CDE" ? "Revisión recibida; el CDE está aplicando los cambios"
          : TransferTitle == "Preparando cambios" ? "Capturando cambios del modelo antes del envío"
          : TransferTitle == "Cambios pendientes" ? "Cambios guardados en la cola; envío pendiente"
          : TransferTitle == "Listo para sincronizar" ? access.CanCreate ? "Pulsa Crear modelo para publicar la primera captura de la vista 3D"
            : "Pulsa Sincronizar para capturar y publicar la vista 3D"
          : TransferTitle == "Guardado local" ? "Captura guardada; conecta un destino CDE para publicar"
          : TransferTitle == "Solo lectura" ? "Los cambios se guardan en este equipo; no se envían al CDE"
          : TransferTitle == "Conexión revocada" ? "Envíos detenidos · cambios guardados en este equipo"
          : TransferTitle == "Detenido" && !hasSyncWorker ? "Abre una vista 3D para comenzar" : status.Detail;
        SyncIndicatorBrush = TransferBrush;
        foreach (var name in new[] { nameof(Model), nameof(DocumentTitle), nameof(RevitState), nameof(SyncIndicatorBrush), nameof(SyncIndicatorLabel), nameof(Capture), nameof(Status), nameof(PauseLabel), nameof(ShowPlayIcon), nameof(CanSynchronize), nameof(SyncLabel), nameof(CanPause), nameof(CanStop), nameof(CanRestartCapture), nameof(TransferTitle), nameof(TransferDetail), nameof(TransferBrush), nameof(TransferProgressVisibility), nameof(TransferIndeterminate), nameof(TransferPercent), nameof(TransferFraction), nameof(TransferPercentLabel), nameof(CapturedCount), nameof(StoredRevision), nameof(DisplayedRevision), nameof(PendingLabel), nameof(BlockLabel), nameof(CdeSession),
          nameof(Access), nameof(SyncHint), nameof(GroupLabel), nameof(GroupVisibility), nameof(GroupHint), nameof(CanReplace), nameof(ReplaceVisibility), nameof(NoticeKind), nameof(NoticeVisibility), nameof(NoticeTitle), nameof(NoticeText),
          nameof(IsRevoked), nameof(CanRestore), nameof(RestoreVisibility), nameof(IsLive), nameof(ReplaceHint), nameof(IdentityName), nameof(SyncButtonEnabled) })
          PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
      }
      if (activity != Activity) { Activity = activity; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Activity))); }
    }
    public string DrawingStatus { get; private set; } = "Sincroniza el modelo para elegir sus vistas 2D";
    public bool CanChooseDrawings { get; private set; }
    public void SetDrawings(string status, bool canChoose)
    {
      status ??= "";
      if (DrawingStatus != status) { DrawingStatus = status; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DrawingStatus))); }
      if (CanChooseDrawings != canChoose) { CanChooseDrawings = canChoose; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanChooseDrawings))); }
    }
    public void SetDestination(string project, string model)
    {
      project = string.IsNullOrWhiteSpace(project) ? "Sin proyecto" : project;
      model = string.IsNullOrWhiteSpace(model) ? "Sin destino" : model;
      if (ProjectName != project) { ProjectName = project; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProjectName))); }
      if (DestinationName != model) { DestinationName = model; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DestinationName))); }
    }
    public void SetConnectionState(CdeConnectionDisplayState state)
    {
      var (label, brush) = state switch
      {
        CdeConnectionDisplayState.Validated => ("Conexión validada", Brushes.DodgerBlue),
        CdeConnectionDisplayState.Connected => ("Conectado", Brushes.LimeGreen),
        CdeConnectionDisplayState.Paused => ("Pausado", Brushes.Goldenrod),
        CdeConnectionDisplayState.Waiting => ("Esperando publicación", Brushes.Goldenrod),
        CdeConnectionDisplayState.Error => ("Error de conexión", Brushes.OrangeRed),
        CdeConnectionDisplayState.Revoked => ("Revocada", Brushes.OrangeRed),
        _ => ("Sin conexión", Brushes.DarkGray)
      };
      if (ConnectionState != label)
      {
        ConnectionState = label; ConnectionStateBrush = brush;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionState)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionStateBrush)));
      }
    }
    public static CdeConnectionDisplayState ResolveConnectionState(bool connected, SyncProgress progress, bool connectionError, bool tested)
    {
      if (connected)
      {
        if (progress.Revoked) return CdeConnectionDisplayState.Revoked;
        if (progress.NoPermission) return CdeConnectionDisplayState.Error;
        if (progress.Stage == "En espera de publicación") return CdeConnectionDisplayState.Waiting;
        if (progress.Attention) return CdeConnectionDisplayState.Error;
        return progress.Paused ? CdeConnectionDisplayState.Paused : CdeConnectionDisplayState.Connected;
      }
      return connectionError ? CdeConnectionDisplayState.Error : tested ? CdeConnectionDisplayState.Validated : CdeConnectionDisplayState.Disconnected;
    }
    // Text of the «Reemplazar modelo…» warning (UI-8 #9): size and author of the CDE model and what the capture would change.
    public static string ReplaceSummary(ReplacePreview preview)
    {
      const string removes = "se quitará del modelo para todas las personas conectadas.";
      if (preview == null)
        return "Existe un modelo con contenido, pero no se pudo comparar con el CDE. Se publicará una captura completa de tu vista 3D y lo que no esté en ella " + removes;
      var last = string.IsNullOrEmpty(preview.LastPublishedBy) ? "" : ", última revisión de " + preview.LastPublishedBy +
        (preview.LastPublishedAt is { } at ? " (" + at.ToLocalTime().ToString("dd/MM HH:mm") + ")" : "");
      return $"Este modelo ya existe en el CDE: {preview.RemoteCount:N0} elementos{last}.\n\n" + (preview.Compared
        ? $"Según tu última captura: +{preview.Added:N0} nuevos · {preview.Changed:N0} cambiados · −{preview.Removed:N0} se quitarán para todas las personas conectadas." +
          (preview.Exact ? "" : " Cifras aproximadas: parte del modelo se publicó sin huellas.")
        : "Aún no hay una captura local con la que comparar. Se capturará tu vista 3D completa y lo que no esté en ella " + removes);
    }
    public static string RestoreSummary(RestorePointInfo point)
    {
      var origin = point == null ? "" : $" de {point.ElementCount:N0} elementos" +
        (string.IsNullOrEmpty(point.CreatedBy) ? "" : $", reemplazada por {point.CreatedBy}") +
        (point.CreatedAt is { } at ? " el " + at.ToLocalTime().ToString("dd/MM HH:mm") : "");
      return "El CDE volverá a la revisión" + origin + " que había antes del último reemplazo.\n\n" +
        "Lo publicado después del reemplazo se descarta para todas las personas conectadas. Solo se guarda un punto de restauración: el del último reemplazo.";
    }
    private static string Short(string id) => string.IsNullOrEmpty(id) ? "Sin confirmar" : id.Substring(0, Math.Min(8, id.Length));
    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024
      ? $"{bytes / (1024d * 1024 * 1024):0.00} GiB" : $"{bytes / (1024d * 1024):0.00} MiB";
  }
}
