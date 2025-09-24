using System.Net.Http;
using CefSharp;
using CefSharp.Wpf;
using Microsoft.Extensions.DependencyInjection;
using OpenProject.Browser.ViewModels;
using OpenProject.Browser.Views;
using OpenProject.Browser.WebViewIntegration;

namespace OpenProject.Browser.Services
{
  internal static class ServiceProviderConfiguration
  {
    internal static IServiceCollection ConfigureIoCContainer()
    {
      var services = new ServiceCollection();

      // Views
      services.AddSingleton<MainWindowView>();
      services.AddSingleton<WebView>();

      // View Models
      services.AddSingleton<MainWindowViewModel>();
      services.AddSingleton<WebViewModel>();

      // other services
      services.AddSingleton<BrowserManager>();
      services.AddSingleton<BcfierJavascriptInterop>();
      services.AddSingleton<JavaScriptBridge>();
      services.AddTransient<OpenProjectInstanceValidator>();

      CefBrowserInitializer.InitializeCefBrowser();
      services.AddSingleton<ChromiumWebBrowser>();

      // Interface implementations
      services.AddSingleton<IGitHubService, GitHubService>();
      services.AddSingleton<IDownloadHandler, OpenProjectBrowserDownloadHandler>();
      services.AddSingleton<IRequestHandler, OpenProjectBrowserRequestHandler>();
      services.AddSingleton<ILifeSpanHandler, OpenProjectBrowserLifeSpanHandler>();

      // We're using HttpClientFactory to ensure that we don't hit any problems with
      // port exhaustion or stale DNS entries in long-lived HttpClients
      services.AddHttpClient(nameof(OpenProjectInstanceValidator))
        .ConfigureHttpMessageHandlerBuilder(h =>
        {
          if (h.PrimaryHandler is HttpClientHandler httpClientHandler)
          {
            // It defaults to true, but let's ensure it stays that way
            httpClientHandler.AllowAutoRedirect = true;
          }
        });

      return services;
    }
  }
}
