using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using OpenProject.Shared.Authentication;

namespace OpenProject.Browser.WebViewIntegration
{
  public sealed class CdeSessionProbe
  {
    public async Task<CdeSessionObservation> CheckAsync(HttpClient client, string cookies, CancellationToken cancel)
    {
      using var request = new HttpRequestMessage(HttpMethod.Get, CdeSessionPolicy.CurrentUser);
      request.Headers.Accept.ParseAdd("application/hal+json");
      request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
      if (!string.IsNullOrEmpty(cookies)) request.Headers.TryAddWithoutValidation("Cookie", cookies);
      using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
      if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) return new CdeSessionObservation(CdeSessionStatus.SignedOut);
      if (response.StatusCode != System.Net.HttpStatusCode.OK || response.Content.Headers.ContentLength > 65536)
        return new CdeSessionObservation(CdeSessionStatus.Unavailable);
      var media = response.Content.Headers.ContentType?.MediaType;
      if (media != "application/hal+json" && media != "application/json") return new CdeSessionObservation(CdeSessionStatus.Unavailable);
      using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
      using var memory = new System.IO.MemoryStream();
      var buffer = new byte[4096]; int count;
      while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, cancel).ConfigureAwait(false)) > 0)
      {
        if (memory.Length + count > 65536) return new CdeSessionObservation(CdeSessionStatus.Unavailable);
        memory.Write(buffer, 0, count);
      }
      var userId = CdeSessionPolicy.ReadUser(System.Text.Encoding.UTF8.GetString(memory.ToArray()));
      return userId > 0 ? new CdeSessionObservation(CdeSessionStatus.Authenticated, userId)
        : new CdeSessionObservation(CdeSessionStatus.Unavailable);
    }
  }
}
