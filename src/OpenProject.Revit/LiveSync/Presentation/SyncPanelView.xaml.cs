using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using System;
using System.Threading;
using System.Threading.Tasks;
using OpenProject.Revit.LiveSync.Queue;
using OpenProject.Revit.LiveSync.Transport;

namespace OpenProject.Revit.LiveSync.Presentation
{
  public partial class SyncPanelView : UserControl, IDockablePaneProvider
  {
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _copyTimer;
    private readonly SyncPanelViewModel _model = new();
    private CdeDestination _testedDestination;
    private int _connectionGeneration;
    private bool _updatingToken, _connectionError, _confirming;
    public SyncPanelView()
    {
      InitializeComponent(); DataContext = _model;
      var saved = CdeCredentialVault.Load();
      if (saved != null) { CdeLink.Text = saved.Address.AbsoluteUri; CdeToken.Password = saved.Token; }
      _timer = new DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(500) };
      _timer.Tick += (_, _) => Refresh();
      _copyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
      _copyTimer.Tick += (_, _) => { _copyTimer.Stop(); CopyFeedback.Visibility = Visibility.Collapsed; };
      Loaded += (_, _) => { _timer.Start(); Refresh(); CheckUpdatesOnLoad(); };
      Unloaded += (_, _) => { _timer.Stop(); _copyTimer.Stop(); ConcealToken(); };
    }
    // Runs from WPF (Loaded, timer, clicks): an exception here would end Revit, so a failed refresh is logged
    // and the next tick tries again.
    private void Refresh()
    {
      try { RefreshCore(); }
      catch (Exception error) { Serilog.Log.Warning(error, "No se pudo refrescar el centro de sincronización."); }
    }
    private void RefreshCore()
    {
      LocalLiveSync.RestoreHistoryDestination();
      var progress = LocalLiveSync.Progress;
      _model.Refresh(LocalLiveSync.ModelLabel, LocalLiveSync.CaptureDescription, progress, LocalLiveSync.Activity,
        LocalLiveSync.CdeSessionStatus, LocalLiveSync.ModelTitle, LocalLiveSync.IsRevitActive, LocalLiveSync.IsRevitProcessing,
        LocalLiveSync.CapturedElements, LocalLiveSync.IsCaptureStopped, LocalLiveSync.IsCdeConnected, LocalLiveSync.HasSyncWorker,
        LocalLiveSync.IsSyncRequested, LocalLiveSync.IsSyncPreparing,
        LocalLiveSync.CaptureProgressDone, LocalLiveSync.CaptureProgressTotal, LocalLiveSync.Access,
        LocalLiveSync.IsLiveMode, LocalLiveSync.SyncChosen);
      var connected = LocalLiveSync.IsCdeConnected;
      _model.SetDestination(connected ? LocalLiveSync.DestinationProjectName : _testedDestination?.ProjectName,
        connected ? LocalLiveSync.DestinationModelName : _testedDestination?.ModelName);
      _model.SetConnectionState(SyncPanelViewModel.ResolveConnectionState(connected, progress, _connectionError, _testedDestination != null));
      _model.SetDrawings(LocalLiveSync.DrawingStatus, LocalLiveSync.CanChooseDrawings);
      HistoryView.Update(LocalLiveSync.ActiveHistoryDirectory, progress);
    }
    private void ConnectionChanged(object sender, RoutedEventArgs args)
    {
      if (_updatingToken) return;
      _connectionGeneration++; _testedDestination = null; _connectionError = false;
      if (ConnectCdeButton != null) ConnectCdeButton.IsEnabled = false;
      if (ConnectionResult != null) ConnectionResult.Text = "Datos modificados · vuelve a probar la conexión.";
      Refresh();
    }
    private void VisibleTokenChanged(object sender, TextChangedEventArgs args)
    {
      if (_updatingToken || CdeToken == null) return;
      _updatingToken = true;
      try { CdeToken.Password = VisibleToken.Text; }
      finally { _updatingToken = false; }
      ConnectionChanged(sender, args);
    }
    private void ToggleTokenClick(object sender, RoutedEventArgs args)
    {
      _updatingToken = true;
      try
      {
        var reveal = VisibleToken.Visibility != Visibility.Visible;
        if (reveal)
        {
          VisibleToken.Text = CdeToken.Password; VisibleToken.Visibility = Visibility.Visible;
          CdeToken.Visibility = Visibility.Collapsed; EyeIcon.Visibility = Visibility.Collapsed;
          EyeOffIcon.Visibility = Visibility.Visible; TokenRevealButton.ToolTip = "Ocultar token";
          System.Windows.Automation.AutomationProperties.SetName(TokenRevealButton, "Ocultar token");
        }
        else ConcealToken();
        if (reveal) VisibleToken.Focus(); else CdeToken.Focus();
      }
      finally { _updatingToken = false; }
    }
    private void ConcealToken()
    {
      if (VisibleToken.Visibility != Visibility.Visible) return;
      _updatingToken = true;
      try { CdeToken.Password = VisibleToken.Text; VisibleToken.Clear(); }
      finally { _updatingToken = false; }
      VisibleToken.Visibility = Visibility.Collapsed; CdeToken.Visibility = Visibility.Visible;
      EyeIcon.Visibility = Visibility.Visible; EyeOffIcon.Visibility = Visibility.Collapsed;
      TokenRevealButton.ToolTip = "Mostrar token";
      System.Windows.Automation.AutomationProperties.SetName(TokenRevealButton, "Mostrar token");
    }
    private void CopyLinkClick(object sender, RoutedEventArgs args) => Copy(CdeLink.Text, "Enlace copiado");
    private void CopyTokenClick(object sender, RoutedEventArgs args) => Copy(CdeToken.Password, "Token copiado");
    private void Copy(string value, string confirmation)
    {
      if (string.IsNullOrEmpty(value)) { CopyFeedback.Text = "No hay contenido para copiar"; }
      else
      {
        try { Clipboard.SetText(value); CopyFeedback.Text = confirmation; }
        catch (Exception) { CopyFeedback.Text = "No se pudo acceder al portapapeles"; }
      }
      CopyFeedback.Visibility = Visibility.Visible;
      _copyTimer.Stop(); _copyTimer.Start();
    }
    private async void TestConnectionClick(object sender, RoutedEventArgs args)
    {
      TestConnectionButton.IsEnabled = false; ConnectCdeButton.IsEnabled = false;
      TestConnectionLabel.Text = "Probando…";
      var generation = ++_connectionGeneration; _testedDestination = null; _connectionError = false;
      ConnectionResult.Text = "Comprobando proyecto, modelo y permisos…";
      try
      {
        var candidate = new CdeDestination(CdeLink.Text, CdeToken.Password);
        if (!Entry.CdeAccessController.MatchesOrigin(candidate.Address))
          throw new InvalidOperationException("El enlace no corresponde a la instancia donde iniciaste sesión.");
        using var receiver = new SyncReceiver(destination: candidate);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var status = await receiver.StatusAsync(timeout.Token);
        if (generation != _connectionGeneration) return;
        candidate = candidate.Verified(status);
        if (!Entry.CdeAccessController.MatchesDestination(candidate.Address, candidate.UserId))
          throw new InvalidOperationException("El enlace o token no corresponde a la instancia y usuario de tu sesión CDE.");
        _testedDestination = candidate; ConnectCdeButton.IsEnabled = true;
        var access = CdeModelAccess.From(status);
        ConnectionResult.Text = "Conexión válida · " + candidate.Label + " · " + status.Count + " elementos guardados" +
          (access.ReadOnly ? " · Grupo Lectura: podrás ver el modelo, no enviar cambios" : access.Known ? " · Grupo " + access.GroupName : "");
        Refresh();
      }
      catch (Exception error)
      {
        if (generation == _connectionGeneration)
        {
          _connectionError = true;
          ConnectionResult.Text = error is ArgumentException or InvalidOperationException or ReceiverException ? error.Message : "No se pudo contactar con el CDE. Comprueba la red y vuelve a probar.";
          Refresh();
        }
      }
      finally { TestConnectionButton.IsEnabled = true; TestConnectionLabel.Text = "Probar conexión"; }
    }
    private void ConnectCdeClick(object sender, RoutedEventArgs args)
    {
      if (_testedDestination == null) return;
      try
      {
        CdeCredentialVault.Save(_testedDestination);
        LocalLiveSync.ConnectDestination(_testedDestination);
        ConcealToken();
        _connectionError = false; ConnectCdeButton.IsEnabled = false;
        ConnectionResult.Text = "Destino seleccionado · " + _testedDestination.Label + ". Pulsa Sincronizar para publicar la vista 3D.";
        Refresh();
      }
      catch (Exception) { _connectionError = true; ConnectionResult.Text = "No se pudo guardar o activar la conexión. Revisa tu sesión y vuelve a probar."; Refresh(); }
    }
    private void ActionClick(object sender, RoutedEventArgs args)
    {
      if (sender is not Button button) return;
      if (button.Tag as string == "sync" && _model.IsRevoked) { BeginReconnect(); return; }
      if (button.Tag as string == "sync" && _model.IsLive) return;
      if (button.Tag as string == "sync" && _model.Status.NeedsConfirmation) { ConfirmFullMerge(); return; }
      // The binding to CanSynchronize disables the button; assigning IsEnabled here would detach it for good.
      LocalLiveSync.Request(button.Tag as string);
      Refresh();
    }
    // UI-8 #29: a first merge that differs from the CDE in nearly everything is sent only after this confirmation.
    private void ConfirmFullMerge()
    {
      if (_confirming) return;
      _confirming = true;
      try
      {
        var dialog = new TaskDialog("Sincronizar")
        {
          MainIcon = TaskDialogIcon.TaskDialogIconWarning,
          MainInstruction = "Tu Revit difiere del CDE en casi todo",
          MainContent = SyncWorker.ConfirmDetail(_model.Status.Comparison).Replace(" Pulsa Sincronizar para confirmarlo.", "") +
            "\n\nTodos verán recargarse el modelo. Si esperabas que coincidiera, revisa que el modelo y la vista 3D sean los correctos.",
          CommonButtons = TaskDialogCommonButtons.Cancel, DefaultButton = TaskDialogResult.Cancel, AllowCancellation = true
        };
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Sincronizar de todos modos", "Fusiona tu captura con el modelo del CDE");
        if (dialog.Show() == TaskDialogResult.CommandLink1) LocalLiveSync.Request("confirmMerge");
      }
      catch (Exception error) { Serilog.Log.Warning(error, "No se pudo confirmar la sincronización."); }
      finally { _confirming = false; Refresh(); }
    }
    // A revoked connection is not repaired from here: the person pastes the new link and token that Administración gives.
    private void BeginReconnect()
    {
      ConnectionToggle.IsChecked = true;
      ConcealToken(); CdeToken.Clear();
      ConnectionResult.Text = "Pega el enlace y el token nuevos que te dé Administración y pulsa Probar conexión.";
      CdeLink.Focus(); CdeLink.SelectAll();
    }
    // Replacing removes from the CDE model everything absent from this capture, for everyone: it is always confirmed,
    // showing first what it would change (huellas of the CDE against the last local capture).
    private async void ReplaceClick(object sender, RoutedEventArgs args)
    {
      if (!_model.CanReplace || _confirming) return;
      _confirming = true;
      try
      {
        ReplacePreview preview = null;
        try { preview = await WithTimeout(LocalLiveSync.PreviewReplaceAsync(), 20); }
        catch (Exception error) { Serilog.Log.Warning(error, "No se pudo comparar el modelo con el CDE antes de reemplazarlo."); }
        if (!_model.CanReplace) return;
        var model = string.IsNullOrWhiteSpace(_model.DestinationName) ? "el modelo" : "«" + _model.DestinationName + "»";
        var dialog = new TaskDialog("Reemplazar modelo")
        {
          MainIcon = TaskDialogIcon.TaskDialogIconWarning,
          MainInstruction = "¿Reemplazar " + model + " en el CDE?",
          MainContent = SyncPanelViewModel.ReplaceSummary(preview),
          FooterText = "Se guarda un punto de restauración: Administración puede volver al modelo anterior con «Restaurar modelo anterior…». " +
            "Para añadir o editar sin quitar nada, usa Sincronizar.",
          CommonButtons = TaskDialogCommonButtons.Cancel, DefaultButton = TaskDialogResult.Cancel, AllowCancellation = true
        };
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Reemplazar modelo", "Captura completa que sustituye la revisión vigente del CDE");
        if (dialog.Show() != TaskDialogResult.CommandLink1) return;
        LocalLiveSync.Request("replace");
      }
      catch (Exception error) { Serilog.Log.Warning(error, "No se pudo preparar el reemplazo del modelo."); }
      finally { _confirming = false; Refresh(); }
    }
    private async void RestoreClick(object sender, RoutedEventArgs args)
    {
      if (!_model.CanRestore || _confirming) return;
      _confirming = true;
      try
      {
        var dialog = new TaskDialog("Restaurar modelo anterior")
        {
          MainIcon = TaskDialogIcon.TaskDialogIconWarning,
          MainInstruction = "¿Restaurar el modelo anterior al último reemplazo?",
          MainContent = SyncPanelViewModel.RestoreSummary(_model.Status.RestorePoint),
          CommonButtons = TaskDialogCommonButtons.Cancel, DefaultButton = TaskDialogResult.Cancel, AllowCancellation = true
        };
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Restaurar modelo anterior", "El CDE vuelve a la revisión que había antes del reemplazo");
        if (dialog.Show() != TaskDialogResult.CommandLink1) return;
        try
        {
          await WithTimeout(LocalLiveSync.RestoreAsync(), 60);
          TaskDialog.Show("Restaurar modelo anterior", "Modelo restaurado en el CDE. La sincronización queda en pausa: tu Revit aún tiene la captura " +
            "que reemplazó el modelo, y al reanudar se enviaría lo que falte de tu vista 3D.");
        }
        catch (Exception error)
        {
          TaskDialog.Show("Restaurar modelo anterior", "No se pudo restaurar el modelo. " +
            (error is ReceiverException or InvalidOperationException or TimeoutException ? error.Message : "Revisa la conexión y vuelve a intentarlo."));
        }
      }
      catch (Exception error) { Serilog.Log.Warning(error, "No se pudo restaurar el modelo."); }
      finally { _confirming = false; Refresh(); }
    }
    private static async Task<T> WithTimeout<T>(Task<T> task, int seconds)
    {
      await WithTimeout((Task)task, seconds);
      return await task;
    }
    private static async Task WithTimeout(Task task, int seconds)
    {
      if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds))) != task) throw new TimeoutException("El CDE no respondió a tiempo.");
      await task;
    }
    public void SetupDockablePane(DockablePaneProviderData data)
    { data.FrameworkElement = this; data.InitialState = new DockablePaneState { DockPosition = DockPosition.Right }; }
    public void ApplyTheme(bool dark)
    {
      if (SystemParameters.HighContrast)
      {
        Resources["PanelBackground"] = SystemColors.WindowBrush; Resources["PanelSurface"] = SystemColors.ControlBrush;
        Resources["PanelText"] = Resources["PanelMuted"] = SystemColors.WindowTextBrush;
        Resources["PanelBorder"] = SystemColors.WindowTextBrush; Resources["PanelAccent"] = SystemColors.HighlightBrush;
        Resources["AccentText"] = SystemColors.HighlightTextBrush; Resources["PanelDanger"] = SystemColors.WindowTextBrush;
        Resources["NoticeInfoBackground"] = Resources["NoticeWarningBackground"] = SystemColors.WindowBrush;
        Resources["NoticeSuccessBackground"] = Resources["NoticeDangerBackground"] = SystemColors.WindowBrush;
        Resources["NoticeInfoBorder"] = Resources["NoticeWarningBorder"] = Resources["NoticeSuccessBorder"] = Resources["NoticeDangerBorder"] = SystemColors.WindowTextBrush; return;
      }
      var colors = dark ? new[] { "#101C27", "#142431", "#E3EDF1", "#A6BBC9", "#2B4050", "#78DDC4", "#0C2924", "#F97066", "#12304A", "#2F6A9A", "#3A2E10", "#8A6A1F", "#0F2A1E", "#2F7A55", "#3A1A18", "#9A3A31" }
        : new[] { "#FFFFFF", "#F3F6F8", "#182B38", "#506475", "#D6E0E6", "#126853", "#FFFFFF", "#B42318", "#EAF3FB", "#9CC3EA", "#FFF7E0", "#E2BC5A", "#ECFDF3", "#75E0A7", "#FEF3F2", "#FDA29B" };
      var keys = new[] { "PanelBackground", "PanelSurface", "PanelText", "PanelMuted", "PanelBorder", "PanelAccent", "AccentText",
        "PanelDanger", "NoticeInfoBackground", "NoticeInfoBorder", "NoticeWarningBackground", "NoticeWarningBorder",
        "NoticeSuccessBackground", "NoticeSuccessBorder", "NoticeDangerBackground", "NoticeDangerBorder" };
      for (var i = 0; i < keys.Length; i++) Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
    }
  }
}
