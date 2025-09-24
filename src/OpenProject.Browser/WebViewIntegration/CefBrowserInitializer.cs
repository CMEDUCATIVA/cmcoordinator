using System.IO;
using CefSharp;
using CefSharp.Wpf;
using OpenProject.Shared;

namespace OpenProject.Browser.WebViewIntegration
{
  /// <summary>
  /// This class is used to initialize the embedded browser view.
  /// </summary>
  public static class CefBrowserInitializer
  {
    /// <summary>
    /// This method must be called from the main UI thread, before any instances of the embedded browser view
    /// are created anywhere in the application. This configures the global settings for the embedded browser view.
    /// </summary>
    public static void InitializeCefBrowser()
    {
      var settings = new CefSettings();

      // create custom user agent to identify the add-in from OpenProject
      var chromiumVersion = Cef.ChromiumVersion;
      var userAgent =
        $"Mozilla/5.0 (Windows NT 6.2; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{chromiumVersion} Safari/537.36" +
        $" /OpenProjectRevitAddIn {VersionsService.Version}";
      settings.UserAgent = userAgent;

      // To enable caching, e.g. of assets and cookies, we're using a temp data folder
      settings.CachePath = Path.Combine(ConfigurationConstant.OpenProjectApplicationData, "BrowserCache");
      // Additionally, we're persisting session cookies to ensure logins are persistent throughout
      // multiple sessions.
      settings.PersistSessionCookies = true;

      Cef.Initialize(settings);
    }
  }
}
