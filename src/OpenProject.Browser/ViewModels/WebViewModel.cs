using System;
using System.Diagnostics;
using System.Linq;
using CefSharp.Wpf;
using OpenProject.Browser.Services;
using OpenProject.Browser.WebViewIntegration;

namespace OpenProject.Browser.ViewModels
{
  public sealed class WebViewModel
  {
    private readonly JavaScriptBridge _javaScriptBridge;
    private readonly BrowserManager _browserManager;
    private readonly PinsService _pinsService;
    private CdeSessionMonitor _sessionMonitor;

    public ChromiumWebBrowser Browser => _browserManager.Browser;

    public WebViewModel(BrowserManager browserManager, JavaScriptBridge javaScriptBridge, PinsService pinsService)
    {
      _browserManager = browserManager;
      _javaScriptBridge = javaScriptBridge;
      _pinsService = pinsService;

      _browserManager.Initialize();
      InitializeIpcConnection();
      var sessionArgs = Environment.GetCommandLineArgs();
      var nonce = sessionArgs.SkipWhile(arg => arg != "session").Skip(1).FirstOrDefault();
      if (!string.IsNullOrWhiteSpace(nonce)) _sessionMonitor = new CdeSessionMonitor(Browser, nonce);
      CloseWhenRevitIsGone(sessionArgs);
    }

    // After Revit dies this browser would hold its cache: the next Revit's browser would hand its arguments to it, which
    // opens them as addresses, and the new Revit would stay without a CDE session.
    private static void CloseWhenRevitIsGone(string[] arguments)
    {
      if (!int.TryParse(arguments.SkipWhile(arg => arg != "revit").Skip(1).FirstOrDefault(), out var revitId)) return;
      Process revit;
      try { revit = Process.GetProcessById(revitId); }
      catch (ArgumentException) { Environment.Exit(0); return; }
      revit.EnableRaisingEvents = true;
      revit.Exited += (_, _) => Environment.Exit(0);
    }

    private void InitializeIpcConnection()
    {
      var commandLineArgs = Environment.GetCommandLineArgs();

      if (commandLineArgs.All(arg => arg != "ipc")) return;

      var args = commandLineArgs.SkipWhile(arg => arg != "ipc").Skip(1).Take(2).ToList();
      if (args.Count < 2) return;

      var serverPort = int.Parse(args[0]);
      var clientPort = int.Parse(args[1]);
      IpcManager.StartIpcCommunication(_javaScriptBridge, _pinsService, serverPort, clientPort);
    }

  }
}
