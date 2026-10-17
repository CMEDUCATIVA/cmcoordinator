using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using System;
using System.Threading;
using OpenProject.Revit.LiveSync.Transport;

namespace OpenProject.Revit.LiveSync.Presentation
{
  public partial class SyncPanelView : UserControl, IDockablePaneProvider
  {
    private readonly DispatcherTimer _timer;
    private readonly SyncPanelViewModel _model = new();
    private CdeDestination _testedDestination;
    private int _connectionGeneration;
    public SyncPanelView()
    {
      InitializeComponent(); DataContext = _model;
      var saved = CdeCredentialVault.Load();
      if (saved != null) { CdeLink.Text = saved.Address.AbsoluteUri; CdeToken.Password = saved.Token; }
      _timer = new DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(500) };
      _timer.Tick += (_, _) => Refresh();
      Loaded += (_, _) => { _timer.Start(); Refresh(); };
      Unloaded += (_, _) => _timer.Stop();
    }
    private void Refresh()
    {
      _model.Refresh(LocalLiveSync.ModelLabel, LocalLiveSync.CaptureDescription, LocalLiveSync.Progress, LocalLiveSync.Activity, LocalLiveSync.CdeSessionStatus);
      DestinationText.Text = "Vista 3D → " + LocalLiveSync.DestinationLabel;
    }
    private void ConnectionChanged(object sender, RoutedEventArgs args)
    {
      _connectionGeneration++; _testedDestination = null;
      if (ConnectCdeButton != null) ConnectCdeButton.IsEnabled = false;
      if (ConnectionResult != null) ConnectionResult.Text = "Datos modificados · vuelve a probar la conexión.";
    }
    private async void TestConnectionClick(object sender, RoutedEventArgs args)
    {
      TestConnectionButton.IsEnabled = false; ConnectCdeButton.IsEnabled = false;
      var generation = ++_connectionGeneration; _testedDestination = null;
      ConnectionResult.Text = "Comprobando proyecto, modelo y permisos…";
      try
      {
        var candidate = new CdeDestination(CdeLink.Text, CdeToken.Password);
        using var receiver = new SyncReceiver(destination: candidate);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var status = await receiver.StatusAsync(timeout.Token);
        if (generation != _connectionGeneration) return;
        candidate = candidate.Verified(status);
        if (candidate.UserId != Entry.CdeAccessController.SignedInUserId)
          throw new InvalidOperationException("El token pertenece a otro usuario. Genera la conexión con tu cuenta CDE.");
        _testedDestination = candidate; ConnectCdeButton.IsEnabled = true;
        ConnectionResult.Text = "Conexión válida · " + candidate.Label + " · " + status.Count + " elementos guardados";
      }
      catch (Exception error)
      {
        if (generation == _connectionGeneration)
          ConnectionResult.Text = error is ArgumentException or InvalidOperationException or ReceiverException ? error.Message : "No se pudo contactar con el CDE. Comprueba la red y vuelve a probar.";
      }
      finally { TestConnectionButton.IsEnabled = true; }
    }
    private void ConnectCdeClick(object sender, RoutedEventArgs args)
    {
      if (_testedDestination == null) return;
      try
      {
        CdeCredentialVault.Save(_testedDestination);
        LocalLiveSync.ConnectDestination(_testedDestination);
        ConnectionResult.Text = "Destino seleccionado · " + _testedDestination.Label + ". Pulsa Sincronizar para publicar la vista 3D.";
      }
      catch (Exception) { ConnectionResult.Text = "No se pudo guardar o activar la conexión. Revisa tu sesión y vuelve a probar."; }
    }
    private void UseLocalClick(object sender, RoutedEventArgs args)
    { LocalLiveSync.ConnectDestination(null); ConnectionResult.Text = "Visor local seleccionado; cola CDE conservada."; }
    private void ActionClick(object sender, RoutedEventArgs args)
    { if (sender is Button button) LocalLiveSync.Request(button.Tag as string); }
    public void SetupDockablePane(DockablePaneProviderData data)
    { data.FrameworkElement = this; data.InitialState = new DockablePaneState { DockPosition = DockPosition.Right }; }
    public void ApplyTheme(bool dark)
    {
      if (SystemParameters.HighContrast)
      {
        Resources["PanelBackground"] = SystemColors.WindowBrush; Resources["PanelSurface"] = SystemColors.ControlBrush;
        Resources["PanelText"] = Resources["PanelMuted"] = SystemColors.WindowTextBrush;
        Resources["PanelBorder"] = SystemColors.WindowTextBrush; Resources["PanelAccent"] = SystemColors.HighlightBrush;
        Resources["AccentText"] = SystemColors.HighlightTextBrush; return;
      }
      var colors = dark ? new[] { "#101C27", "#142431", "#E3EDF1", "#A6BBC9", "#2B4050", "#78DDC4", "#0C2924" }
        : new[] { "#FFFFFF", "#F3F6F8", "#182B38", "#506475", "#D6E0E6", "#126853", "#FFFFFF" };
      var keys = new[] { "PanelBackground", "PanelSurface", "PanelText", "PanelMuted", "PanelBorder", "PanelAccent", "AccentText" };
      for (var i = 0; i < keys.Length; i++) Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
    }
  }
}
