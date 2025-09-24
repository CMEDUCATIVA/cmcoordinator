using System;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using CefSharp;
using OpenProject.Browser.Services;
using OpenProject.Browser.Settings;

namespace OpenProject.Browser.WebViewIntegration
{
  /// <summary>
  /// This class is used to check if a request is in the whitelist and can thus be
  /// opened in the CefSharp browser window. If this is not the case, the default system
  /// browser is used to open the page, e.g. for external links
  /// </summary>
  public class OpenProjectBrowserRequestHandler : IRequestHandler
  {
    public bool GetAuthCredentials(IWebBrowser chromiumWebBrowser, IBrowser browser, string originUrl, bool isProxy,
      string host, int port, string realm, string scheme, IAuthCallback callback)
    {
      return true;
    }

    public IResourceRequestHandler GetResourceRequestHandler(IWebBrowser chromiumWebBrowser, IBrowser browser,
      IFrame frame, IRequest request, bool isNavigation, bool isDownload, string requestInitiator,
      ref bool disableDefaultHandling)
    {
      return null;
    }

    public bool OnBeforeBrowse(
      IWebBrowser chromiumWebBrowser,
      IBrowser browser,
      IFrame frame,
      IRequest request,
      bool userGesture,
      bool isRedirect)
    {
      if (ShouldOpenRequestInEmbeddedBrowser(request, isRedirect))
        return false;

      OpenRequestInStandardBrowser(request);
      return true;
    }

    public void OnDocumentAvailableInMainFrame(IWebBrowser chromiumWebBrowser, IBrowser browser)
    {
    }

    private static bool ShouldOpenRequestInEmbeddedBrowser(IRequest request, bool isRedirect)
    {
      // We're allowing all embedded requests to just go through
      if (request.ResourceType != ResourceType.MainFrame)
        return true;

      if (isRedirect)
        return true;

      var isValidLocalUrl = request.Url.StartsWith("file://", StringComparison.InvariantCultureIgnoreCase)
                            || request.Url.StartsWith("devtools://", StringComparison.InvariantCultureIgnoreCase);
      if (isValidLocalUrl)
        return true;

      var knownGoodUrls = ConfigurationHandler.Settings.GetOpenProjectInstances();
      var isValidExternalUrl = knownGoodUrls.Any(goodUrl =>
        request.Url.StartsWith(goodUrl, StringComparison.InvariantCultureIgnoreCase));

      return isValidExternalUrl || IsRequestOnSameDomain(request);
    }

    private static void OpenRequestInStandardBrowser(IRequest request)
    {
      var cmdUrl = request.Url.Replace("&", "^&");
      Process.Start(new ProcessStartInfo("cmd", $"/c start {cmdUrl}") { CreateNoWindow = true });
    }

    private static bool IsRequestOnSameDomain(IRequest request)
    {
      if (string.IsNullOrEmpty(request.ReferrerUrl)) return false;

      var referrer = new Uri(request.ReferrerUrl);
      var requestUrl = new Uri(request.Url);

      return referrer.Host == requestUrl.Host;
    }

    public bool OnCertificateError(IWebBrowser chromiumWebBrowser, IBrowser browser, CefErrorCode errorCode,
      string requestUrl, ISslInfo sslInfo, IRequestCallback callback)
    {
      return true;
    }

    public bool OnOpenUrlFromTab(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, string targetUrl,
      WindowOpenDisposition targetDisposition, bool userGesture)
    {
      return true;
    }

    public void OnPluginCrashed(IWebBrowser chromiumWebBrowser, IBrowser browser, string pluginPath)
    {
    }

    public bool OnQuotaRequest(IWebBrowser chromiumWebBrowser, IBrowser browser, string originUrl, long newSize,
      IRequestCallback callback)
    {
      return true;
    }

    public void OnRenderProcessTerminated(IWebBrowser chromiumWebBrowser, IBrowser browser, CefTerminationStatus status)
    {
    }

    public void OnRenderViewReady(IWebBrowser chromiumWebBrowser, IBrowser browser)
    {
    }

    public bool OnSelectClientCertificate(IWebBrowser chromiumWebBrowser, IBrowser browser, bool isProxy, string host,
      int port, X509Certificate2Collection certificates, ISelectClientCertificateCallback callback)
    {
      return true;
    }
  }
}
