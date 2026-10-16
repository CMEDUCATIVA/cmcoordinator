using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.UI;

namespace OpenProject.Revit.LiveSync.Presentation
{
  public partial class SyncPanelView : UserControl, IDockablePaneProvider
  {
    private readonly DispatcherTimer _timer;
    private readonly SyncPanelViewModel _model = new();
    public SyncPanelView()
    {
      InitializeComponent(); DataContext = _model;
      _timer = new DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(500) };
      _timer.Tick += (_, _) => Refresh();
      Loaded += (_, _) => { _timer.Start(); Refresh(); };
      Unloaded += (_, _) => _timer.Stop();
    }
    private void Refresh() => _model.Refresh(LocalLiveSync.ModelLabel, LocalLiveSync.CaptureDescription, LocalLiveSync.Progress, LocalLiveSync.Activity);
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
