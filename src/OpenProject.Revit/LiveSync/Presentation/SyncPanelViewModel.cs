using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using OpenProject.Revit.LiveSync.Queue;

namespace OpenProject.Revit.LiveSync.Presentation
{
  public enum CdeConnectionDisplayState { Disconnected, Validated, Connected, Paused, Waiting, Error }

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
    public bool CanSynchronize => IsCdeConnected && !SyncRequested && !SyncPreparing && !HasActiveTransfer;
    public string SyncLabel => HasActiveTransfer ? "Sincronizando…" : SyncRequested || SyncPreparing ? "Preparando…" : "Sincronizar";
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
      : $"{Status.Done} de {Status.Total} paquetes" + (Status.BytesTotal > 0 ? $" · {FormatBytes(Status.BytesDone)} de {FormatBytes(Status.BytesTotal)}" : "") + " · " + (Status.Stage == "En espera de publicación" ? "esperando turno de publicación" : Status.Attention ? "revisión no aceptada" : "revisión pendiente");
    public string PendingLabel => Status.HasPendingChanges ? $"{Status.PendingElements} elementos agrupados pendientes de envío" : "Sin cambios preparados pendientes";
    public SyncProgress Status { get; private set; } = new();
    private string _signature;
    public void Refresh(string model, string capture, SyncProgress status, string activity, string cdeSession = "CDE pendiente de confirmar",
      string documentTitle = null, bool revitActive = false, bool revitProcessing = false,
      int? capturedElements = null, bool captureStopped = false, bool cdeConnected = false, bool hasSyncWorker = false,
      bool syncRequested = false, bool syncPreparing = false, int captureDone = 0, int captureTotal = 0)
    {
      documentTitle ??= model;
      var state = !revitActive ? "Revit inactivo" : revitProcessing ? "Revit procesando" : "Revit activo";
      var signature = cdeSession + model + documentTitle + state + capture + capturedElements + captureStopped + cdeConnected + hasSyncWorker + syncRequested + syncPreparing
        + captureDone + "/" + captureTotal
        + status.Stage + status.Detail + status.JobId + status.Done + "/" + status.Total + status.BytesDone + "/" + status.BytesTotal + status.Paused + status.Attention
        + status.ReceiverRevision + status.ViewerRevision + status.HasPendingChanges + status.PendingElements + string.Join(";", status.Jobs.Select(j => j.Id + j.State + j.Detail));
      if (_signature != signature)
      {
        _signature = signature; Model = model; DocumentTitle = documentTitle; RevitState = state;
        Capture = capture; Status = status; CdeSession = cdeSession;
        IsCaptureStopped = captureStopped; HasSyncWorker = hasSyncWorker; IsCdeConnected = cdeConnected;
        SyncRequested = syncRequested; SyncPreparing = syncPreparing; RevitProcessing = revitProcessing;
        CaptureDone = captureDone; CaptureTotal = captureTotal;
        CapturedCount = capturedElements?.ToString("N0") ?? "—";
        var measured = IsCapturing || !captureStopped && status.JobId != null && status.Total > 0 &&
          status.Stage is not ("Al día" or "Procesando revisión");
        TransferProgressVisibility = measured || syncRequested || revitProcessing && cdeConnected ? Visibility.Visible : Visibility.Collapsed;
        if (!hasSyncWorker || captureStopped) (TransferTitle, TransferBrush) = ("Detenido", Brushes.DarkGray);
        else if (status.Stage == "En espera de publicación") (TransferTitle, TransferBrush) = ("Esperando publicación", Brushes.Goldenrod);
        else if (status.Attention) (TransferTitle, TransferBrush) = ("Error", Brushes.OrangeRed);
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
          : TransferTitle == "Listo para sincronizar" ? "Pulsa Sincronizar para capturar y publicar la vista 3D"
          : TransferTitle == "Guardado local" ? "Captura guardada; conecta un destino CDE para publicar"
          : TransferTitle == "Detenido" && !hasSyncWorker ? "Abre una vista 3D para comenzar" : status.Detail;
        SyncIndicatorBrush = TransferBrush;
        foreach (var name in new[] { nameof(Model), nameof(DocumentTitle), nameof(RevitState), nameof(SyncIndicatorBrush), nameof(SyncIndicatorLabel), nameof(Capture), nameof(Status), nameof(PauseLabel), nameof(ShowPlayIcon), nameof(CanSynchronize), nameof(SyncLabel), nameof(CanPause), nameof(CanStop), nameof(CanRestartCapture), nameof(TransferTitle), nameof(TransferDetail), nameof(TransferBrush), nameof(TransferProgressVisibility), nameof(TransferIndeterminate), nameof(TransferPercent), nameof(TransferFraction), nameof(TransferPercentLabel), nameof(CapturedCount), nameof(StoredRevision), nameof(DisplayedRevision), nameof(PendingLabel), nameof(BlockLabel), nameof(CdeSession) })
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
        if (progress.Stage == "En espera de publicación") return CdeConnectionDisplayState.Waiting;
        if (progress.Attention) return CdeConnectionDisplayState.Error;
        return progress.Paused ? CdeConnectionDisplayState.Paused : CdeConnectionDisplayState.Connected;
      }
      return connectionError ? CdeConnectionDisplayState.Error : tested ? CdeConnectionDisplayState.Validated : CdeConnectionDisplayState.Disconnected;
    }
    private static string Short(string id) => string.IsNullOrEmpty(id) ? "Sin confirmar" : id.Substring(0, Math.Min(8, id.Length));
    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024
      ? $"{bytes / (1024d * 1024 * 1024):0.00} GiB" : $"{bytes / (1024d * 1024):0.00} MiB";
  }
}
