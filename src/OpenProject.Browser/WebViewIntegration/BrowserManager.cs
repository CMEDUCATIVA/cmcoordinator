using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CefSharp;
using CefSharp.Wpf;
using OpenProject.Browser.Services;
using OpenProject.Browser.Settings;

namespace OpenProject.Browser.WebViewIntegration
{
  public sealed class BrowserManager
  {
    private readonly JavaScriptBridge _javaScriptBridge;
    private readonly BcfierJavascriptInterop _javascriptInterop;
    private readonly ChromiumWebBrowser _chromiumWebBrowser;
    private readonly IDownloadHandler _downloadHandler;
    private readonly IRequestHandler _requestHandler;
    private readonly ILifeSpanHandler _lifeSpanHandler;
    public ChromiumWebBrowser Browser { get; private set; }

    public BrowserManager(
      JavaScriptBridge javaScriptBridge,
      BcfierJavascriptInterop javascriptInterop,
      ChromiumWebBrowser chromiumWebBrowser,
      IDownloadHandler downloadHandler,
      IRequestHandler requestHandler,
      ILifeSpanHandler lifeSpanHandler)
    {
      _javaScriptBridge = javaScriptBridge;
      /_javascriptInterop = javascriptInterop;
      _chromiumWebBrowser = chromiumWebBrowser;
      _downloadHandler = downloadHandler;
      _requestHandler = requestHandler;
      _lifeSpanHandler = lifeSpanHandler;
    }

    public void Initialize()
    {
      Browser = _chromiumWebBrowser;

      // Injected target URLs
      var injectBase = "https://cmproyectos.cmeducativa.es";
      var injectUrl = "https://cmproyectos.cmeducativa.es/login?back_url=https%3A%2F%2Fcmproyectos.cmeducativa.es%2F";

      var knownGoodUrls = ConfigurationHandler.Settings.GetOpenProjectInstances();
      var lastVisitedPage = ConfigurationHandler.LastVisitedPage();
      var isWhiteListedUrl = knownGoodUrls.Any(goodUrl =>
        lastVisitedPage.StartsWith(goodUrl, StringComparison.InvariantCultureIgnoreCase));

      // If nothing valid is persisted, seed it with our injected values
      if (string.IsNullOrWhiteSpace(lastVisitedPage) || !isWhiteListedUrl)
      {
        // 1) Ensure base instance is whitelisted so StartsWith() passes
        ConfigurationHandler.SaveSelectedInstance(injectBase);
        // 2) Persist the login URL as the last visited page
        ConfigurationHandler.SaveLastVisitedPage(injectUrl);

        // Update local variables for this run
        lastVisitedPage = injectUrl;
        isWhiteListedUrl = true;
      }

      Browser.Address =
        isWhiteListedUrl ? lastVisitedPage : EmbeddedLandingPageHandler.GetEmbeddedLandingPageIndexUrl();

      Browser.DownloadHandler = _downloadHandler;
      // This handles checking of valid urls, otherwise they're opened in
      // the system browser
      Browser.RequestHandler = _requestHandler;
      // This one prevents popups or additional browser windows
      Browser.LifeSpanHandler = _lifeSpanHandler;

      _javaScriptBridge.SetWebBrowser(Browser);

      Browser.LoadingStateChanged += (s, e) =>
      {
        if (!e.IsLoading) // Not loading means the load is complete
        {
          Application.Current.Dispatcher.Invoke(async () => { await InitializeRevitBridgeIfNotPresentAsync(); });
        }
      };

      // Save all page changes, so that the user can resume to the same
      // page as last time she used the add-in.
      DependencyPropertyDescriptor
        .FromProperty(ChromiumWebBrowser.AddressProperty, typeof(ChromiumWebBrowser))
        .AddValueChanged(Browser, (s, e) =>
        {
          var newUrl = Browser.Address;
          // Don't save local file addresses such as the settings page.
          if (newUrl.StartsWith("http"))
          {
            ConfigurationHandler.SaveLastVisitedPage(Browser.Address);
          }
        });

      if (!ConfigurationHandler.Settings.EnableDevelopmentTools) return;

      var devToolsEnabled = false;
      Browser.IsBrowserInitializedChanged += (s, e) =>
      {
        if (devToolsEnabled) return;

        Browser.ShowDevTools();
        devToolsEnabled = true;
      };
    }

    private async Task InitializeRevitBridgeIfNotPresentAsync()
    {
      JavascriptResponse revitBridgeIsPresentCheckResponse = await Browser.GetMainFrame()
        .EvaluateScriptAsync("window." + JavaScriptBridge.REVIT_BRIDGE_JAVASCRIPT_NAME);
      if (revitBridgeIsPresentCheckResponse?.Result != null)
      {
        // No need to register the bridge since it's already bound
        return;
      }

      // Register the bridge between JS and C#
      // This also registers the callback that should be bound to by OpenProject to receive messages from BCFier
      Browser.JavascriptObjectRepository.UnRegisterAll();
      Browser.GetMainFrame()
        .ExecuteJavaScriptAsync($"CefSharp.DeleteBoundObject('{JavaScriptBridge.REVIT_BRIDGE_JAVASCRIPT_NAME}');");
      Browser.JavascriptObjectRepository.Register(
        JavaScriptBridge.REVIT_BRIDGE_JAVASCRIPT_NAME, _javascriptInterop, true);

      Browser.GetMainFrame().ExecuteJavaScriptAsync(@"(async function(){
await CefSharp.BindObjectAsync(""" + JavaScriptBridge.REVIT_BRIDGE_JAVASCRIPT_NAME + @""", ""bound"");
window." + JavaScriptBridge.REVIT_BRIDGE_JAVASCRIPT_NAME +
                                                    @".sendMessageToOpenProject = (message) => {console.log(JSON.parse(message))}; // This is the callback to be used by OpenProject for receiving messages
window.dispatchEvent(new Event('" + JavaScriptBridge.REVIT_READY_EVENT_NAME + @"'));
})();");
      // Now in JS, call this: openProjectBridge.messageFromOpenProject('Message from JS');
    }
  }
}
