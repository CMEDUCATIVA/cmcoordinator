using System;
using System.Threading.Tasks;
using System.Windows;
using CefSharp;
using CefSharp.Wpf;
using Newtonsoft.Json;
using OpenProject.Browser.Services;
using OpenProject.Browser.Settings;
using OpenProject.Shared;
using Serilog;

namespace OpenProject.Browser.WebViewIntegration
{
  public class JavaScriptBridge
  {
    private readonly OpenProjectInstanceValidator _instanceValidator;


    /// <summary>
    /// This is the name of the global window object that's set in JavaScript, e.g.
    /// 'window.RevitBridge'.
    /// </summary>
    public const string REVIT_BRIDGE_JAVASCRIPT_NAME = "RevitBridge";

    public const string REVIT_READY_EVENT_NAME = "revit.plugin.ready";

    private bool _isLoaded;

    public JavaScriptBridge(OpenProjectInstanceValidator instanceValidator)
    {
      _instanceValidator = instanceValidator;
    }

    private ChromiumWebBrowser _webBrowser;

    public event WebUiMessageReceivedEventHandler OnWebUiMessageReceived;

    public delegate void WebUiMessageReceivedEventHandler(object sender, WebUiMessageEventArgs e);

    public event AppForegroundRequestReceivedEventHandler OnAppForegroundRequestReceived;

    public delegate void AppForegroundRequestReceivedEventHandler(object sender);

    public event NavigationEventHandler OnNavigationEventReceived;

    public delegate void NavigationEventHandler(object sender);

    private void ChangeLoadingState(object sender, object eventArgs) => _isLoaded = true;

    public void SetWebBrowser(ChromiumWebBrowser webBrowser)
    {
      if (_webBrowser != null)
      {
        _webBrowser.LoadingStateChanged -= ChangeLoadingState;
      }

      _webBrowser = webBrowser;
      _webBrowser.LoadingStateChanged += ChangeLoadingState;
    }


    public void SendMessageToRevit(string messageType, string trackingId, string messagePayload)
    {
      if (!_isLoaded)
      {
        Log.Warning("Failed to send message to Revit: bridge web browser not loaded");
        return;
      }

      Log.Information("Sending message of type {A} to Revit", messageType);
      switch (messageType)
      {
        case MessageTypes.INSTANCE_SELECTED:
          // This is the case at the beginning when the user selects which instance of OpenProject
          // should be accessed. We're not relaying this to Revit.
          HandleInstanceNameReceived(messagePayload);
          break;
        case MessageTypes.ADD_INSTANCE:
          // Simply save the instance to the white list and do nothing else.
          ConfigurationHandler.SaveSelectedInstance(messagePayload);
          break;
        case MessageTypes.REMOVE_INSTANCE:
          ConfigurationHandler.RemoveSavedInstance(messagePayload);
          break;
        case MessageTypes.ALL_INSTANCES_REQUESTED:
        {
          var allInstances = JsonConvert.SerializeObject(ConfigurationHandler.Settings.GetOpenProjectInstances());
          SendMessageToOpenProject(MessageTypes.ALL_INSTANCES, trackingId, allInstances);
          break;
        }
        case MessageTypes.FOCUS_REVIT_APPLICATION:
          RevitMainWindowHandler.SetFocusToRevit();
          break;
        case MessageTypes.GO_TO_SETTINGS:
          VisitUrl(LandingIndexPageUrl());
          break;
        case MessageTypes.SET_BROWSER_TO_FOREGROUND:
          OnAppForegroundRequestReceived?.Invoke(this);
          break;
        case MessageTypes.VALIDATE_INSTANCE:
          Task.Run(async () => await ValidateInstanceAsync(trackingId, messagePayload));
          break;
        default:
        {
          var eventArgs = new WebUiMessageEventArgs(messageType, trackingId, messagePayload);
          OnWebUiMessageReceived?.Invoke(this, eventArgs);
          // For some UI operations, revit should be focused
          RevitMainWindowHandler.SetFocusToRevit();
          break;
        }
      }

      // Hacky solution to directly send focus back to OP.
      if (messageType == MessageTypes.VIEWPOINT_DATA)
      {
        OnAppForegroundRequestReceived?.Invoke(this);
      }
    }

    public void SendMessageToOpenProject(string messageType, string trackingId, string messagePayload)
    {
      if (!_isLoaded)
      {
        Log.Warning("Failed to send message to OpenProject: bridge web browser not loaded");
        return;
      }

      Log.Information("Sending message of type {A} to OpenProject", messageType);

      switch (messageType)
      {
        case MessageTypes.CLOSE_DESKTOP_APPLICATION:
          // This message means we should exit the application
          Environment.Exit(0);
          return;
        case MessageTypes.GO_TO_SETTINGS:
          try
          {
            VisitUrl(LandingIndexPageUrl());
          }
          catch (Exception e)
          {
            Log.Error(e, "error fetching landing page index url");
            MessageHandler.ShowError("Cannot open OpenProject settings. Please contact an administrator.");
          }

          break;
        case MessageTypes.SET_BROWSER_TO_FOREGROUND:
        case MessageTypes.VIEWPOINT_GENERATED:
          OnAppForegroundRequestReceived?.Invoke(this);
          break;
      }

      var messageData = JsonConvert.SerializeObject(new { messageType, trackingId, messagePayload });
      var encodedMessage = JsonConvert.ToString(messageData);
      Application.Current.Dispatcher.Invoke(() =>
      {
        _webBrowser?.GetMainFrame()
          .ExecuteJavaScriptAsync($"{REVIT_BRIDGE_JAVASCRIPT_NAME}.sendMessageToOpenProject({encodedMessage})");
      });
    }

    private void VisitUrl(string url)
    {
      Log.Information("Opened new URL '{url}' in browser.", url);
      Application.Current.Dispatcher.Invoke(() => { _webBrowser.Load(url); });
      OnNavigationEventReceived?.Invoke(this);
    }

    private static string LandingIndexPageUrl() => EmbeddedLandingPageHandler.GetEmbeddedLandingPageIndexUrl();

    private void HandleInstanceNameReceived(string instanceName)
    {
      ConfigurationHandler.SaveSelectedInstance(instanceName);
      VisitUrl(instanceName);
    }

    private async Task ValidateInstanceAsync(string trackingId, string message)
    {
      var (isValid, instanceBaseUrl) = await _instanceValidator.IsValidOpenProjectInstanceAsync(message);

      SendMessageToOpenProject(MessageTypes.VALIDATED_INSTANCE,
        trackingId,
        JsonConvert.SerializeObject(new { isValid, instanceBaseUrl }));
    }
  }
}
