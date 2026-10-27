using System;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using OpenProject.Revit.Services.Updates;
using OpenProject.Revit.Views;

namespace OpenProject.Revit.LiveSync.Presentation
{
  public partial class SyncPanelView
  {
    private bool _updateCheckStarted;

    private async void CheckUpdatesOnLoad()
    {
      if (_updateCheckStarted) return;
      _updateCheckStarted = true;
      var assembly = Assembly.GetExecutingAssembly();
      PluginVersionText.Text = "Versión instalada: " + assembly.GetName().Version.ToString(3);
      try
      {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var release = await new GitHubUpdateClient(http).LatestAsync(cancellation.Token);
        if (release == null)
        {
          UpdateAvailabilityText.Text = "Sin publicación estable disponible";
          return;
        }
        if (UpdateInstallation.IsCurrent(release, assembly.Location, assembly.GetName().Version))
        {
          UpdateAvailabilityText.Text = "Versión actualizada";
          return;
        }
        UpdateAvailabilityText.Text = "Nueva versión disponible: " + release.Tag;
        var neon = (Color)ColorConverter.ConvertFromString("#39FF14");
        PanelUpdateButton.BorderBrush = new SolidColorBrush(neon);
        var glow = new DropShadowEffect { Color = neon, BlurRadius = 15, ShadowDepth = 0, Opacity = 0.7 };
        PanelUpdateButton.Effect = glow;
        if (SystemParameters.ClientAreaAnimation)
          glow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(0.25, 0.9,
            TimeSpan.FromSeconds(1.2)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
      }
      catch (Exception)
      {
        UpdateAvailabilityText.Text = "No se pudo consultar GitHub; puedes reintentar";
      }
    }

    private void PanelUpdateClick(object sender, RoutedEventArgs e)
    {
      var window = new McpSettingsWindow();
      window.ShowUpdates(startDownload: true);
    }
  }
}
