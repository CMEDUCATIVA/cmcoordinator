using Autodesk.Revit.UI;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using OpenProject.Revit.Services;
using OpenProject.Shared;
using Serilog;
using ZetaIpc.Runtime.Helper;

namespace OpenProject.Revit.Entry
{
  public static class RibbonButtonClickHandler
  {
#if Version2022
    public const string RevitVersion = "2022";
#elif Version2021
    public const string RevitVersion = "2021";
#elif Version2020
    public const string RevitVersion = "2020";
#elif Version2019
    public const string RevitVersion = "2019";
#endif

    private static Process _opBrowserProcess;
    public static IpcHandler IpcHandler { get; private set; }

    public static Result OpenMainPluginWindow(ExternalCommandData commandData, ref string message)
    {
      try
      {
        EnsureExternalOpenProjectAppIsRunning(commandData);
        IpcHandler.SendBringBrowserToForegroundRequestToDesktopApp();

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
        EnsureExternalOpenProjectAppIsRunning(commandData);
        IpcHandler.SendOpenSettingsRequestToDesktopApp();
        IpcHandler.SendBringBrowserToForegroundRequestToDesktopApp();
        return Result.Succeeded;
      }
      catch (Exception exception)
      {
        message = exception.Message;
        Log.Error(exception, message);
        return Result.Failed;
      }
    }

    private static void EnsureExternalOpenProjectAppIsRunning(ExternalCommandData commandData)
    {
      //Version check
      if (!commandData.Application.Application.VersionName.Contains(RevitVersion))
      {
        MessageHandler.ShowWarning(
          "Unexpected version",
          "The Revit version does not match the expectations.",
          $"This Add-In was built and tested only for Revit {RevitVersion}. Further usage is at your own risk");
      }

      if (_opBrowserProcess is { HasExited: false })
        return;

      IpcHandler = new IpcHandler(commandData.Application);
      var revitServerPort = IpcHandler.StartLocalServerAndReturnPort();

      var openProjectBrowserExecutablePath = GetOpenProjectBrowserExecutable();
      if (!File.Exists(openProjectBrowserExecutablePath))
        throw new SystemException("Browser executable not found.");

      var opBrowserServerPort = FreePortHelper.GetFreePort();
      var processArguments = $"ipc {opBrowserServerPort} {revitServerPort}";
      _opBrowserProcess = Process.Start(openProjectBrowserExecutablePath, processArguments);
      IpcHandler.StartLocalClient(opBrowserServerPort);
      Log.Information("IPC bridge started between port {port1} and {port2}.",
        opBrowserServerPort, revitServerPort);
    }

    private static string GetOpenProjectBrowserExecutable()
    {
      var currentAssemblyPathUri = Assembly.GetExecutingAssembly().CodeBase;
      var currentAssemblyPath = Uri.UnescapeDataString(new Uri(currentAssemblyPathUri).AbsolutePath).Replace("/", "\\");
      var currentFolder = Path.GetDirectoryName(currentAssemblyPath) ?? string.Empty;

      return Path.Combine(currentFolder, ConfigurationConstant.OpenProjectBrowserExecutablePath);
    }
  }
}
