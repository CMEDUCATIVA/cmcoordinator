using System;
using System.Threading.Tasks;
using CefSharp;
using Newtonsoft.Json;
using OpenProject.Browser.Services;
using Serilog;

namespace OpenProject.Browser.WebViewIntegration
{
  public sealed class OpenProjectBrowserResourceRequestHandler : IResourceRequestHandler
  {
    private readonly PinsService _pinsService;

    public OpenProjectBrowserResourceRequestHandler(PinsService pinsService)
    {
      _pinsService = pinsService;
    }

    public ICookieAccessFilter GetCookieAccessFilter(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
      IRequest request)
    {
      return null;
    }

    public CefReturnValue OnBeforeResourceLoad(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
      IRequest request, IRequestCallback callback)
    {
      try
      {
        var method = request?.Method ?? string.Empty;
        var url = request?.Url ?? string.Empty;
        if (url.IndexOf("/viewpoints/", StringComparison.OrdinalIgnoreCase) >= 0)
        {
          var referrer = request?.ReferrerUrl ?? frame?.Url ?? string.Empty;
          var workPackageId = WorkPackageIdResolver.FromUrl(referrer);
          Log.Information("Viewpoint request detected method={Method} url={Url} referrer={Referrer} workPackageId={WorkPackageId}",
            method, url, referrer, workPackageId);
          if (!string.IsNullOrWhiteSpace(workPackageId))
            _pinsService.SetLastViewpointWorkPackageId(workPackageId);

          if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
              && url.IndexOf("/snapshot", StringComparison.OrdinalIgnoreCase) >= 0
              && !string.IsNullOrWhiteSpace(workPackageId))
          {
            _pinsService.UpdateSnapshotUrl(workPackageId, url);
            TryFetchViewpointJson(chromiumWebBrowser, workPackageId, url);
          }

          if (string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase))
          {
            if (!string.IsNullOrWhiteSpace(workPackageId))
              _pinsService.RemovePin(workPackageId);
            else
              Log.Warning("Viewpoint DELETE detected but workPackageId not resolved. url={Url} referrer={Referrer}",
                url, referrer);
          }
        }
      }
      catch (Exception ex)
      {
        Log.Warning(ex, "Viewpoint DELETE detection failed");
      }

      return CefReturnValue.Continue;
    }

    private void TryFetchViewpointJson(IWebBrowser chromiumWebBrowser, string workPackageId, string snapshotUrl)
    {
      if (!_pinsService.NeedsViewpointJson(workPackageId))
        return;

      if (!_pinsService.TryBeginViewpointFetch(workPackageId))
        return;

      var viewpointUrl = GetViewpointUrlFromSnapshot(snapshotUrl);
      if (string.IsNullOrWhiteSpace(viewpointUrl))
      {
        _pinsService.EndViewpointFetch(workPackageId);
        return;
      }

      _ = FetchViewpointJsonAsync(chromiumWebBrowser, workPackageId, viewpointUrl);
    }

    private async Task FetchViewpointJsonAsync(IWebBrowser chromiumWebBrowser, string workPackageId, string viewpointUrl)
    {
      try
      {
        var encodedUrl = JsonConvert.ToString(viewpointUrl);
        var script = $"(async () => {{ const r = await fetch({encodedUrl}, {{ credentials: 'include' }}); if (!r.ok) return ''; return await r.text(); }})()";
        var result = await chromiumWebBrowser.EvaluateScriptAsync(script).ConfigureAwait(false);
        if (!result.Success || result.Result == null)
        {
          Log.Warning("Viewpoint fetch failed workPackageId={WorkPackageId} success={Success} message={Message}",
            workPackageId, result.Success, result.Message ?? string.Empty);
          return;
        }

        var json = result.Result.ToString();
        if (string.IsNullOrWhiteSpace(json))
        {
          Log.Warning("Viewpoint fetch returned empty payload workPackageId={WorkPackageId}", workPackageId);
          return;
        }

        Log.Information("Viewpoint fetch succeeded workPackageId={WorkPackageId} url={Url} length={Length}",
          workPackageId, viewpointUrl, json.Length);

        _pinsService.UpdateViewpointJson(workPackageId, json);
      }
      catch (Exception ex)
      {
        Log.Warning(ex, "Viewpoint fetch failed workPackageId={WorkPackageId} url={Url}", workPackageId, viewpointUrl);
      }
      finally
      {
        _pinsService.EndViewpointFetch(workPackageId);
      }
    }

    private static string GetViewpointUrlFromSnapshot(string snapshotUrl)
    {
      if (string.IsNullOrWhiteSpace(snapshotUrl))
        return null;

      var idx = snapshotUrl.IndexOf("/snapshot", StringComparison.OrdinalIgnoreCase);
      return idx <= 0 ? snapshotUrl : snapshotUrl.Substring(0, idx);
    }

    public IResourceHandler GetResourceHandler(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
      IRequest request)
    {
      return null;
    }

    public void OnResourceRedirect(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request,
      IResponse response, ref string newUrl)
    {
    }

    public bool OnResourceResponse(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request,
      IResponse response)
    {
      return false;
    }

    public IResponseFilter GetResourceResponseFilter(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
      IRequest request, IResponse response)
    {
      return null;
    }

    public void OnResourceLoadComplete(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request,
      IResponse response, UrlRequestStatus status, long receivedContentLength)
    {
    }

    public bool OnProtocolExecution(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request)
    {
      return false;
    }

    public void Dispose()
    {
    }
  }
}
