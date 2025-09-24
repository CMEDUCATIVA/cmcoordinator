using System;
using CefSharp;
using Serilog;

namespace OpenProject.Browser.WebViewIntegration
{
  public sealed class OpenProjectBrowserDownloadHandler : IDownloadHandler
  {
    public event EventHandler<DownloadItem> OnBeforeDownloadFired;

    public event EventHandler<DownloadItem> OnDownloadUpdatedFired;

    public void OnBeforeDownload(IWebBrowser chromiumWebBrowser, IBrowser browser, DownloadItem downloadItem,
      IBeforeDownloadCallback callback)
    {
      Log.Information("Download triggered for item '{name}'", downloadItem.SuggestedFileName);

      OnBeforeDownloadFired?.Invoke(this, downloadItem);

      if (callback.IsDisposed) return;

      using (callback)
        callback.Continue(downloadItem.SuggestedFileName, true);
    }

    public void OnDownloadUpdated(IWebBrowser chromiumWebBrowser, IBrowser browser, DownloadItem downloadItem,
      IDownloadItemCallback callback)
    {
      OnDownloadUpdatedFired?.Invoke(this, downloadItem);
    }
  }
}
