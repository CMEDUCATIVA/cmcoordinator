using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CefSharp;
using CefSharp.Wpf;
using OpenProject.Shared.Authentication;

namespace OpenProject.Browser.WebViewIntegration
{
  // Native only: authentication messages are never accepted from page JavaScript.
  internal sealed class CdeSessionMonitor : IDisposable
  {
    private readonly ChromiumWebBrowser _browser;
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _stop = new();
    private readonly string _nonce;
    private long _sequence;
    private bool _checking;
    private bool _disposed;
    public CdeSessionMonitor(ChromiumWebBrowser browser, string nonce)
    {
      _browser = browser; _nonce = nonce;
      _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
      _timer.Tick += OnTick;
      browser.FrameLoadStart += OnNavigation;
      browser.FrameLoadEnd += OnLoaded;
      browser.Dispatcher.ShutdownStarted += OnShutdown;
      IpcManager.SessionCheckRequested += OnCheckRequested;
      _timer.Start();
    }
    private void OnShutdown(object sender, EventArgs args) => Dispose();
    private void OnCheckRequested() => _browser.Dispatcher.BeginInvoke(new Action(() => OnTick(this, EventArgs.Empty)));
    private void OnNavigation(object sender, FrameLoadStartEventArgs args)
    {
      if (!args.Frame.IsMain) return;
      _browser.Dispatcher.BeginInvoke(new Action(() =>
      {
        ++_sequence; // Discard a probe from the previous page without declaring a logout.
      }));
    }
    private void OnLoaded(object sender, FrameLoadEndEventArgs args)
    { if (args.Frame.IsMain) _browser.Dispatcher.BeginInvoke(new Action(() => OnTick(this, EventArgs.Empty))); }
    private async void OnTick(object sender, EventArgs args)
    {
      if (_disposed || _checking || _browser.IsLoading || !CdeSessionPolicy.IsCde(_browser.Address)) return;
      _checking = true;
      var address = _browser.Address;
      var sequence = ++_sequence;
      var observation = new CdeSessionObservation(CdeSessionStatus.Unavailable);
      try
      {
        if (CdeSessionPolicy.IsCde(address) && !_browser.IsLoading)
        {
          using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
          deadline.CancelAfter(TimeSpan.FromSeconds(8));
          var cookieTask = _browser.GetCookieManager().VisitUrlCookiesAsync(CdeSessionPolicy.CurrentUser, true);
          if (await Task.WhenAny(cookieTask, Task.Delay(TimeSpan.FromSeconds(6), deadline.Token)) != cookieTask)
            throw new OperationCanceledException(deadline.Token);
          var cookies = await cookieTask;
          if (_disposed) return;
          using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
          using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
          observation = await new CdeSessionProbe().CheckAsync(client,
            string.Join("; ", cookies.Select(cookie => cookie.Name + "=" + cookie.Value)), deadline.Token);
        }
      }
      catch (Exception error) when (error is HttpRequestException || error is OperationCanceledException || error is System.IO.IOException || error is ObjectDisposedException)
      { observation = new CdeSessionObservation(CdeSessionStatus.Unavailable); }
      catch (Exception)
      { observation = new CdeSessionObservation(CdeSessionStatus.Unavailable); Serilog.Log.Warning("No se pudo comprobar la sesión web del CDE"); }
      finally { _checking = false; }
      if (!_disposed && sequence == _sequence && address == _browser.Address) Publish(observation);
    }
    private void Publish(CdeSessionObservation observation)
    {
      if (_disposed) return;
      IpcManager.SendCdeSession(_nonce, ++_sequence, observation);
    }
    public void Dispose()
    {
      if (_disposed) return;
      _disposed = true; _timer.Stop(); _stop.Cancel();
      _browser.FrameLoadStart -= OnNavigation; _browser.FrameLoadEnd -= OnLoaded;
      _browser.Dispatcher.ShutdownStarted -= OnShutdown;
      IpcManager.SessionCheckRequested -= OnCheckRequested;
      _stop.Dispose();
    }
  }
}
