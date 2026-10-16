using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Windows;
using OpenProject.Revit.Services.Updates;
using Serilog;

namespace OpenProject.Revit.Views
{
  public partial class McpSettingsWindow
  {
    private CancellationTokenSource _updateCancellation;
    private bool _updatePrepared;

    private void InitializeUpdates()
    {
      InstalledVersionText.Text = "Versión instalada: " + Assembly.GetExecutingAssembly().GetName().Version;
      UpdateStatusText.Text = UpdateInstallation.PendingStatus() ?? "Busca publicaciones estables en CMEDUCATIVA/cmcoordinator.";
    }

    private async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
      var pending = UpdateInstallation.PendingStatus();
      if (pending?.StartsWith("Actualización preparada") == true || pending == "Instalación en curso.")
      { UpdateStatusText.Text = pending; return; }
      UpdateButton.IsEnabled = false;
      CancelUpdateButton.Visibility = Visibility.Visible;
      _updateCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
      var token = _updateCancellation.Token;
      try
      {
        UpdateStatusText.Text = "Consultando la última publicación estable…";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var client = new GitHubUpdateClient(http);
        var release = await client.LatestAsync(token);
        if (release == null) { UpdateStatusText.Text = "Todavía no hay una Release estable publicada. Una etiqueta sola no incluye instalador."; return; }
        var assembly = Assembly.GetExecutingAssembly();
        if (UpdateInstallation.IsCurrent(release, assembly.Location, assembly.GetName().Version))
        { UpdateStatusText.Text = "Cmcoordinator ya está actualizado (" + release.Tag + ")."; return; }
        var folder = Path.Combine(UpdateInstallation.Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var installer = Path.Combine(folder, "Cmcoordinator-setup.exe");
        var progress = new Progress<int>(value => { if (_updateCancellation != null && !token.IsCancellationRequested && !_updatePrepared) UpdateStatusText.Text = $"Descargando {release.Tag}: {value}%"; });
        await client.DownloadAsync(release, installer, progress, token);
        token.ThrowIfCancellationRequested();
        var helper = Path.Combine(Path.GetDirectoryName(assembly.Location), "Updater", "apply-update.ps1");
        UpdateInstallation.Schedule(release, installer, assembly.Location, helper);
        _updatePrepared = true;
        UpdateStatusText.Text = $"{release.Tag} preparada y verificada. Guarda tu trabajo y cierra todas las ventanas de Revit; se abrirá el instalador.";
        Log.Information("Cmcoordinator update prepared tag={Tag} asset={Asset}", release.Tag, release.AssetId);
      }
      catch (OperationCanceledException)
      { UpdateStatusText.Text = "Descarga cancelada o tiempo de espera agotado. Puedes reintentar."; }
      catch (Exception error)
      {
        UpdateStatusText.Text = "No se preparó la actualización: " + error.Message;
        Log.Warning("Cmcoordinator update failed type={Type} message={Message}", error.GetType().Name, error.Message);
      }
      finally
      {
        _updateCancellation.Dispose(); _updateCancellation = null;
        CancelUpdateButton.Visibility = Visibility.Collapsed;
        UpdateButton.IsEnabled = !_updatePrepared;
      }
    }

    private void OnCancelUpdateClick(object sender, RoutedEventArgs e) => _updateCancellation?.Cancel();
    protected override void OnClosed(EventArgs e)
    { _updateCancellation?.Cancel(); base.OnClosed(e); }
  }
}
