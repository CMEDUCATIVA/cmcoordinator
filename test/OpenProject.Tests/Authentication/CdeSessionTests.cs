using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using OpenProject.Browser.WebViewIntegration;
using OpenProject.Shared.Authentication;
using Xunit;

namespace OpenProject.Tests.Authentication
{
  public class CdeSessionTests
  {
    private const string User = "{\"_type\":\"User\",\"status\":\"active\",\"id\":42,\"login\":\"modelador\"}";
    [Theory]
    [InlineData("https://cmproyectos.cmeducativa.es/", true)]
    [InlineData("https://cmproyectos.cmeducativa.es/login", true)]
    [InlineData("https://cmproyectos.cmeducativa.es.evil.test/", false)]
    [InlineData("https://evil.test/?https://cmproyectos.cmeducativa.es", false)]
    [InlineData("https://cmproyectos.cmeducativa.es:444/", false)]
    [InlineData("https://user@cmproyectos.cmeducativa.es/", false)]
    [InlineData("http://cmproyectos.cmeducativa.es/", false)]
    public void OnlyCanonicalSecureOrigin(string url, bool expected) => Assert.Equal(expected, CdeSessionPolicy.IsCde(url));

    [Theory]
    [InlineData("<html>login</html>")]
    [InlineData("{\"_type\":\"Error\",\"id\":42}")]
    [InlineData("{\"_type\":\"User\",\"status\":\"active\",\"id\":3,\"login\":\"\"}")]
    [InlineData("{\"_type\":\"User\",\"status\":\"locked\",\"id\":42,\"login\":\"modelador\"}")]
    [InlineData("{\"_type\":\"User\",\"status\":\"active\",\"id\":\"42\",\"login\":\"modelador\"}")]
    [InlineData("{\"_type\":{}}")]
    public void RejectAnonymousLockedAndMalformedIdentity(string body) => Assert.Equal(0, CdeSessionPolicy.ReadUser(body));

    [Fact]
    public void LeaseRejectsReplayOtherBrowserAndDelayedMessages()
    {
      var lease = new CdeSessionLease(); var now = DateTime.UtcNow;
      lease.Reset("browser-one");
      Assert.True(lease.Accept("browser-one", 1, 42, now, now));
      Assert.True(lease.IsValid(now));
      Assert.False(lease.Accept("browser-two", 2, 77, now, now));
      Assert.False(lease.Accept("browser-one", 1, 77, now, now));
      Assert.False(lease.Accept("browser-one", 2, 42, now.AddSeconds(-16), now));
      Assert.False(lease.Accept("browser-one", 2, 42, now.AddSeconds(10), now));
      Assert.False(lease.IsValid(now.AddSeconds(21)));
      Assert.True(lease.Accept("browser-one", 3, 0, now, now));
      Assert.False(lease.IsValid(now));
      lease.Reset("browser-two");
      Assert.False(lease.Accept("browser-one", 99, 42, now, now));
      Assert.True(lease.Accept("browser-two", 1, 77, now, now));
    }

    [Theory]
    [InlineData(200, "application/hal+json", true)]
    [InlineData(200, "text/html", false)]
    [InlineData(401, "application/hal+json", false)]
    [InlineData(403, "application/hal+json", false)]
    [InlineData(302, "application/hal+json", false)]
    [InlineData(503, "application/hal+json", false)]
    public async Task ProbeChecksResponseAndSendsOnlySessionCookie(int status, string media, bool expected)
    {
      var handler = new ResponseHandler(request =>
      {
        Assert.Equal(CdeSessionPolicy.CurrentUser, request.RequestUri.AbsoluteUri);
        Assert.Null(request.Headers.Authorization);
        Assert.Equal("session=fixture", string.Join("", request.Headers.GetValues("Cookie")));
        return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(User, System.Text.Encoding.UTF8, media) };
      });
      using var client = new HttpClient(handler);
      var observation = await new CdeSessionProbe().CheckAsync(client, "session=fixture", CancellationToken.None);
      Assert.Equal(expected ? 42L : 0L, observation.UserId);
      Assert.Equal(expected ? CdeSessionStatus.Authenticated : status == 401 ? CdeSessionStatus.SignedOut : CdeSessionStatus.Unavailable, observation.Status);
    }

    [Fact]
    public async Task ProbeRejectsOversizedResponse()
    {
      using var client = new HttpClient(new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
      { Content = new StringContent(new string(' ', 65537) + User, System.Text.Encoding.UTF8, "application/json") }));
      Assert.Equal(CdeSessionStatus.Unavailable, (await new CdeSessionProbe().CheckAsync(client, "", CancellationToken.None)).Status);
    }
    [Fact]
    public async Task ProbeHonorsCancellation()
    {
      using var source = new CancellationTokenSource(); source.Cancel();
      using var client = new HttpClient(new ResponseHandler(_ => throw new Exception("Must not send")));
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CdeSessionProbe().CheckAsync(client, "", source.Token));
    }
    private sealed class ResponseHandler : HttpMessageHandler
    {
      private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;
      public ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> response) => _response = response;
      protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(_response(request)); }
    }
  }
}
