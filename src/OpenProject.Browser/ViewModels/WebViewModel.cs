using System;
using System.Linq;
using CefSharp.Wpf;
using OpenProject.Browser.Services;
using OpenProject.Browser.WebViewIntegration;
using OpenProject.Shared;
using Version = OpenProject.Browser.Models.Version;

namespace OpenProject.Browser.ViewModels
{
  public sealed class WebViewModel
  {
    private readonly JavaScriptBridge _javaScriptBridge;
    private readonly IGitHubService _gitHubService;
    private readonly BrowserManager _browserManager;

    public ChromiumWebBrowser Browser => _browserManager.Browser;

    public WebViewModel(IGitHubService gitHubService, BrowserManager browserManager, JavaScriptBridge javaScriptBridge)
    {
      _gitHubService = gitHubService;
      _browserManager = browserManager;
      _javaScriptBridge = javaScriptBridge;

      _browserManager.Initialize();
      InitializeIpcConnection();
      CheckForUpdates();
    }

    private void InitializeIpcConnection()
    {
      var commandLineArgs = Environment.GetCommandLineArgs();

      if (commandLineArgs.All(arg => arg != "ipc")) return;

      var args = commandLineArgs.SkipWhile(arg => arg != "ipc").Skip(1).Take(2).ToList();
      if (args.Count < 2) return;

      var serverPort = int.Parse(args[0]);
      var clientPort = int.Parse(args[1]);
      IpcManager.StartIpcCommunication(_javaScriptBridge, serverPort, clientPort);
    }

    private void CheckForUpdates()
    {
      if (!ConfigurationHandler.Settings.CheckForUpdates)
        return;

      var latestRelease = _gitHubService.GetLatestRelease();

      latestRelease.MatchSome(release =>
      {
        Version latestVersion = release.Version();
        Version currentVersion = Version.Parse(VersionsService.Version);
        if (latestVersion.CompareTo(currentVersion) > 0)
        {
          MessageHandler.ShowUpdateDialog(
            currentVersion.ToString(),
            latestVersion.ToString(),
            release.PublishedAt(),
            release.DownloadUrl());
        }
      });
    }
  }
}
