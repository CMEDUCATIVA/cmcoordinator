using System;
using CefSharp;
using Serilog;

namespace OpenProject.Browser.WebViewIntegration
{
  public sealed class OpenProjectBrowserDownloadHandler : IDownloadHandler
  {
    public event EventHandler<DownloadItem> OnBeforeDownloadFired;

    public event EventHandler<DownloadItem> OnDownloadUpdatedFired;

    public bool CanDownload(IWebBrowser chromiumWebBrowser, IBrowser browser, string url, string requestMethod)
    {
      // Allow all downloads by default
      return true;
    }

    public bool OnBeforeDownload(IWebBrowser chromiumWebBrowser, IBrowser browser, DownloadItem downloadItem,
      IBeforeDownloadCallback callback)
    {
      Log.Information("Download triggered for item '{name}'", downloadItem.SuggestedFileName);

      OnBeforeDownloadFired?.Invoke(this, downloadItem);

      if (callback.IsDisposed) return false;

      using (callback)
        callback.Continue(downloadItem.SuggestedFileName, true);

      return true;
    }

    public void OnDownloadUpdated(IWebBrowser chromiumWebBrowser, IBrowser browser, DownloadItem downloadItem,
      IDownloadItemCallback callback)
    {
      OnDownloadUpdatedFired?.Invoke(this, downloadItem);
    }
  }
}
