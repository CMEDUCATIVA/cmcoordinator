using System;
using System.ComponentModel;
using System.Linq;
using OpenProject.Revit.LiveSync.Queue;

namespace OpenProject.Revit.LiveSync.Presentation
{
  public sealed class SyncPanelViewModel : INotifyPropertyChanged
  {
    public event PropertyChangedEventHandler PropertyChanged;
    public string Model { get; private set; } = "Abre una vista 3D";
    public string Capture { get; private set; } = "Captura inactiva";
    public string Activity { get; private set; } = "Sin actividad";
    public string CdeSession { get; private set; } = "CDE pendiente de confirmar";
    public string PauseLabel => Status.Paused ? "▶ Continuar" : "Ⅱ Pausar";
    public string StoredRevision => Short(Status.ReceiverRevision);
    public string DisplayedRevision => Short(Status.ViewerRevision);
    public string BlockLabel => Status.Total == 0 ? "Sin transferencia pendiente"
      : $"{Status.Done} de {Status.Total} bloques recibidos · " + (Status.Attention ? "revisión no aceptada" : "revisión pendiente");
    public string PendingLabel => Status.HasPendingChanges ? $"{Status.PendingElements} elementos agrupados pendientes de envío" : "Sin cambios preparados pendientes";
    public SyncProgress Status { get; private set; } = new();
    private string _signature;
    public void Refresh(string model, string capture, SyncProgress status, string activity, string cdeSession = "CDE pendiente de confirmar")
    {
      var signature = cdeSession + model + capture + status.Stage + status.Detail + status.Done + "/" + status.Total + status.Paused + status.Attention
        + status.ReceiverRevision + status.ViewerRevision + status.HasPendingChanges + status.PendingElements + string.Join(";", status.Jobs.Select(j => j.Id + j.State + j.Detail));
      if (_signature != signature)
      {
        _signature = signature; Model = model; Capture = capture; Status = status; CdeSession = cdeSession;
        foreach (var name in new[] { nameof(Model), nameof(Capture), nameof(Status), nameof(PauseLabel), nameof(StoredRevision), nameof(DisplayedRevision), nameof(PendingLabel), nameof(BlockLabel), nameof(CdeSession) })
          PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
      }
      if (activity != Activity) { Activity = activity; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Activity))); }
    }
    private static string Short(string id) => string.IsNullOrEmpty(id) ? "Sin confirmar" : id.Substring(0, Math.Min(8, id.Length));
  }
}
