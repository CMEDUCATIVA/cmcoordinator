using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using OpenProject.Shared.Logging;
using OpenProject.Revit.Views;
using OpenProject.Revit.Services;
using OpenProject.Revit.LiveSync;

namespace OpenProject.Revit.Entry
{
  [Transaction(TransactionMode.Manual)]
  public class AppMain : IExternalApplication
  {
    private readonly string _path = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

    #region Revit IExternalApplciation Implementation

    /// <summary>
    /// Startup
    /// </summary>
    /// <param name="application"></param>
    /// <returns></returns>
    public Result OnStartup(UIControlledApplication application)
    {
      try
      {
        Logger.ConfigureLogger("OpenProject.Revit.Log..txt");
        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        var lastWrite = File.GetLastWriteTime(assemblyPath);
        Serilog.Log.Information("OpenProject.Revit build stamp path={Path} lastWrite={LastWrite}", assemblyPath, lastWrite);
        RevitBridgeClient.Initialize();
        RevitBridgeClient.ApplySettings(McpSettingsStore.Load());

        // Tab
        const string tabName = "Cmcoordinator";
        const string panelName = "BCF management";
        application.CreateRibbonTab(tabName);
        RibbonPanel panel = application.CreateRibbonPanel(tabName, panelName);

        // Button Data
        RibbonItem browserButtonItem = panel.AddItem(
          new PushButtonData("Cmcoordinator",
            "Cmcoordinator",
            Path.Combine(_path, "OpenProject.Revit.dll"),
            "OpenProject.Revit.Entry.CmdMain"));

        if (browserButtonItem is PushButton browserButton)
        {
          browserButton.Image = LoadPngImgSource("OpenProject.Revit.Assets.CM__16.png");
          browserButton.LargeImage = LoadPngImgSource("OpenProject.Revit.Assets.CM__32.png");
          browserButton.ToolTip = "Cmcoordinator browser";
        }

        RibbonItem settingsButtonItem = panel.AddItem(
          new PushButtonData("Settings",
            "Settings",
            Path.Combine(_path, "OpenProject.Revit.dll"),
            "OpenProject.Revit.Entry.CmdMainSettings"));

        if (settingsButtonItem is PushButton settingsButton)
        {
          settingsButton.Image = LoadPngImgSource("OpenProject.Revit.Assets.Settings32.png");
          settingsButton.LargeImage = LoadPngImgSource("OpenProject.Revit.Assets.Settings32.png");
          settingsButton.ToolTip = "Cmcoordinator Revit Add-in settings";
        }

        var livePanel = application.CreateRibbonPanel(tabName, "Visor local");
        var liveButton = livePanel.AddItem(new PushButtonData("LocalLiveSync", "Iniciar\nvisor local",
          assemblyPath, "OpenProject.Revit.Entry.CmdLocalLiveSync")) as PushButton;
        LocalLiveSync.Initialize(application, liveButton);

        // Dockable pins pane
        var pinsView = new PinsPaneView();
        var paneProvider = new PinsDockablePaneProvider(pinsView);
        application.RegisterDockablePane(PinsPaneManager.PaneId, "Centro CDE para gestión del modelo", paneProvider);
      }
      catch (Exception exception)
      {
        MessageBox.Show("exception: " + exception);
        return Result.Failed;
      }

      return Result.Succeeded;
    }

    /// <summary>
    /// Shut Down
    /// </summary>
    /// <param name="application"></param>
    /// <returns></returns>
    public Result OnShutdown(UIControlledApplication application)
    {
      try
      {
        LocalLiveSync.Shutdown();
        RevitBridgeClient.Stop();
        RibbonButtonClickHandler.CloseBrowserIfRunning();
      }
      catch
      {
        // TODO -> What to do when Bcfier.Win can't be stopped?
      }

      return Result.Succeeded;
    }

    #endregion

    #region Private Members

    /// <summary>
    /// Load an Image Source from File
    /// </summary>
    /// <param name="sourceName"></param>
    /// <param name="path"></param>
    /// <returns></returns>
    private ImageSource LoadPngImgSource(string resourceName)
    {
      try
      {
        // Assembly & Stream
        var assembly = typeof(AppMain).Assembly;
        var icon = assembly.GetManifestResourceStream(resourceName);

        // Decoder
        PngBitmapDecoder m_decoder =
          new PngBitmapDecoder(icon, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.Default);

        // Source
        ImageSource m_source = m_decoder.Frames[0];
        return (m_source);
      }
      catch
      {
        // ignored
      }

      // Fail
      return null;
    }

    #endregion
  }
}
