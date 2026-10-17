using Autodesk.Revit.UI;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Interop;
using OpenProject.Revit.Services;
using OpenProject.Revit.Views;
using OpenProject.Shared;
using Serilog;
using ZetaIpc.Runtime.Helper;

namespace OpenProject.Revit.Entry
{
  public static class RibbonButtonClickHandler
  {
#if Version2027
    public const string RevitVersion = "2027";
#elif Version2026
    public const string RevitVersion = "2026";
#elif Version2025
    public const string RevitVersion = "2025";
#elif Version2024
    public const string RevitVersion = "2024";
#elif Version2022
    public const string RevitVersion = "2022";
#elif Version2021
    public const string RevitVersion = "2021";
#elif Version2020
    public const string RevitVersion = "2020";
#elif Version2019
    public const string RevitVersion = "2019";
#else
    public const string RevitVersion = "";
#endif

    private static Process _opBrowserProcess;
    private static McpSettingsWindow _settingsWindow;
    public static IpcHandler IpcHandler { get; private set; }
    public static bool IsBrowserRunning => _opBrowserProcess is { HasExited: false };

    public static void OpenBrowser(UIApplication app)
    {
      EnsureExternalOpenProjectAppIsRunning(app);
      IpcHandler.SendBringBrowserToForegroundRequestToDesktopApp();
      IpcHandler.SendSessionCheckRequest();
    }

    public static Result OpenMainPluginWindow(ExternalCommandData commandData, ref string message)
    {
      try
      {
        CdeAccessController.Open(commandData.Application);

        return Result.Succeeded;
      }
      catch (Exception exception)
      {
        message = exception.Message;
        Log.Error(exception, message);
        return Result.Failed;
      }
    }

    public static Result OpenSettingsPluginWindow(ExternalCommandData commandData, ref string message)
    {
      try
      {
        ShowMcpSettingsWindow(commandData.Application);
        return Result.Succeeded;
      }
      catch (Exception exception)
      {
        message = exception.Message;
        Log.Error(exception, message);
        return Result.Failed;
      }
    }

    private static void ShowMcpSettingsWindow(UIApplication uiApp)
    {
      if (_settingsWindow == null || !_settingsWindow.IsVisible)
      {
        _settingsWindow = new McpSettingsWindow();
        var helper = new WindowInteropHelper(_settingsWindow);
        helper.Owner = uiApp.MainWindowHandle;
        _settingsWindow.Show();
        return;
      }

      _settingsWindow.Activate();
    }

    private static void EnsureExternalOpenProjectAppIsRunning(UIApplication app)
    {
      //Version check
      if (!app.Application.VersionName.Contains(RevitVersion))
      {
        MessageHandler.ShowWarning(
          "Unexpected version",
          "The Revit version does not match the expectations.",
          $"This Add-In was built and tested only for Revit {RevitVersion}. Further usage is at your own risk");
      }

      if (_opBrowserProcess is { HasExited: false })
        return;

      IpcHandler?.Dispose();
      var nonce = Guid.NewGuid().ToString("N");
      CdeAccessController.BeginBrowser(nonce);
      IpcHandler = new IpcHandler(app);
      var revitServerPort = IpcHandler.StartLocalServerAndReturnPort();

      var openProjectBrowserExecutablePath = GetOpenProjectBrowserExecutable();
      if (!File.Exists(openProjectBrowserExecutablePath))
        throw new SystemException(
          "Browser executable not found. Checked: " +
          string.Join(", ", GetOpenProjectBrowserExecutableCandidates()));

      var opBrowserServerPort = FreePortHelper.GetFreePort();
      var processArguments = $"ipc {opBrowserServerPort} {revitServerPort} session {nonce}";
      _opBrowserProcess = Process.Start(openProjectBrowserExecutablePath, processArguments);
      IpcHandler.StartLocalClient(opBrowserServerPort);
      PinsPaneManager.PushCurrentPinsToDesktopApp();
      Log.Information("IPC bridge started between port {port1} and {port2}.",
        opBrowserServerPort, revitServerPort);
    }

    public static void CloseBrowserIfRunning()
    {
      try
      {
        if (_opBrowserProcess is { HasExited: false })
        {
          Log.Information("Closing OpenProject browser process...");
          IpcHandler?.SendShutdownRequestToDesktopApp();

          if (!_opBrowserProcess.WaitForExit(2000))
          {
            Log.Warning("OpenProject browser did not exit gracefully. Killing process.");
            _opBrowserProcess.Kill();
          }
        }
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Failed to close OpenProject browser process.");
      }
      finally
      {
        _opBrowserProcess = null;
        IpcHandler?.Dispose();
        IpcHandler = null;
      }
    }

    private static string GetOpenProjectBrowserExecutable()
    {
      var candidates = GetOpenProjectBrowserExecutableCandidates();
      var existingPath = candidates.FirstOrDefault(File.Exists);
      if (!string.IsNullOrWhiteSpace(existingPath))
      {
        Log.Information("Using OpenProject browser executable at path={Path}", existingPath);
        return existingPath;
      }

      // Keep returning the first candidate as fallback path in case caller wants to log/inspect it.
      return candidates[0];
    }

    private static string[] GetOpenProjectBrowserExecutableCandidates()
    {
      var currentAssemblyPath = Assembly.GetExecutingAssembly().Location;
      var currentFolder = Path.GetDirectoryName(currentAssemblyPath) ?? string.Empty;

      return new[]
      {
        Path.Combine(currentFolder, ConfigurationConstant.OpenProjectBrowserExecutablePath),
        Path.Combine(currentFolder, "OpenProject.Browser", ConfigurationConstant.OpenProjectBrowserExecutablePath)
      };
    }
  }
}
