using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using OpenProject.Revit.LiveSync.Queue;

namespace OpenProject.Revit.LiveSync.Transport
{
  public sealed class ReceiverStatus
  {
    public string Application { get; set; }
    public int[] Protocols { get; set; }
    public string RevisionId { get; set; }
    public string ViewerRevision { get; set; }
    public string SourceId { get; set; }
    public string SessionId { get; set; }
    public long SessionStartedAt { get; set; }
    public int Count { get; set; }
    public long UserId { get; set; }
    public long ProjectId { get; set; }
    public long ModelId { get; set; }
    public long ChannelId { get; set; }
    public long ConnectionId { get; set; }
    public string PublisherRole { get; set; }
    public string PublishingUser { get; set; }
    public string ProjectName { get; set; }
    public string ModelName { get; set; }
    public string StateCode { get; set; }
  }
  public sealed class RemoteJob
  {
    public bool Committed { get; set; }
    public bool Waiting { get; set; }
    public string PublishingUser { get; set; }
    public List<int> Received { get; set; } = new();
  }
  public sealed class ReceiverException : Exception
  {
    public int StatusCode { get; }
    public double RetryAfterSeconds { get; }
    public ReceiverException(int code, double retryAfter = 0, string reason = null)
      : base("El receptor devolvió HTTP " + code + (string.IsNullOrWhiteSpace(reason) ? "" : ": " + reason))
    { StatusCode = code; RetryAfterSeconds = retryAfter; }
    public bool Permanent => (StatusCode >= 300 && StatusCode < 400) || StatusCode is 400 or 401 or 403 or 404 or 413 or 415 or 422 or 423 or 426 or 507;
  }
  public interface ISyncReceiver : IDisposable
  {
    Task<ReceiverStatus> StatusAsync(CancellationToken token);
    Task<RemoteJob> BeginAsync(SyncJob job, CancellationToken token);
    Task ChunkAsync(string jobId, int index, byte[] bytes, CancellationToken token);
    Task CommitAsync(string jobId, CancellationToken token);
  }
  public interface ISyncPresence { void SetPaused(bool paused); }
  public sealed class SyncReceiver : ISyncReceiver, ISyncPresence
  {
    private readonly HttpClient _http;
    private readonly Func<Task> _ensureService;
    private readonly CdeDestination _destination;
    private string _publisherSession;
    private PublisherFeed _feed;
    private bool _paused;
    public SyncReceiver(Func<Task> ensureService = null, Uri baseAddress = null, CdeDestination destination = null)
    {
      _destination = destination;
      _ensureService = ensureService;
      _http = new HttpClient(new HttpClientHandler { UseProxy = destination != null, AllowAutoRedirect = false })
        { BaseAddress = destination?.Address ?? baseAddress ?? new Uri(LocalViewerEndpoint.Url), Timeout = TimeSpan.FromSeconds(30) };
      if (destination != null) _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", destination.Token);
    }
    public async Task<ReceiverStatus> StatusAsync(CancellationToken token)
    {
      EnsureAccount();
      if (_destination != null && _publisherSession != null)
      {
        _feed ??= new PublisherFeed(_destination);
        _feed.Configure(_publisherSession, _paused);
        return await _feed.ReadAsync(token).ConfigureAwait(false);
      }
      if (_ensureService != null) await _ensureService().ConfigureAwait(false);
      using var request = new HttpRequestMessage(HttpMethod.Get, "api/status");
      if (_destination != null && _publisherSession != null) request.Headers.Add("X-Publisher-Session", _publisherSession);
      using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
      await Check(response).ConfigureAwait(false);
      var value = JsonConvert.DeserializeObject<ReceiverStatus>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
      if (value?.Application != (_destination == null ? "cmcoordinator-local-live-viewer" : "cmcoordinator-cde") || Array.IndexOf(value.Protocols ?? Array.Empty<int>(), 2) < 0)
        throw new ReceiverException(426);
      return value;
    }
    public async Task<RemoteJob> BeginAsync(SyncJob job, CancellationToken token)
    {
      EnsureAccount();
      using var content = new StringContent(SyncEncoding.Json(job), Encoding.UTF8, "application/json");
      using var response = await _http.PostAsync("api/v2/jobs", content, token).ConfigureAwait(false);
      await Check(response).ConfigureAwait(false);
      _publisherSession = job.SessionId;
      _feed ??= _destination == null ? null : new PublisherFeed(_destination);
      _feed?.Configure(_publisherSession, _paused);
      return JsonConvert.DeserializeObject<RemoteJob>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }
    public async Task ChunkAsync(string jobId, int index, byte[] bytes, CancellationToken token)
    {
      EnsureAccount();
      using var content = new ByteArrayContent(bytes);
      content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
      content.Headers.Add("X-Chunk-Sha256", SyncEncoding.Hash(bytes));
      using var response = await _http.PostAsync($"api/v2/jobs/{jobId}/chunks/{index}", content, token).ConfigureAwait(false);
      await Check(response).ConfigureAwait(false);
    }
    public async Task CommitAsync(string jobId, CancellationToken token)
    {
      EnsureAccount();
      using var content = new StringContent("{}", Encoding.UTF8, "application/json");
      using var response = await _http.PostAsync($"api/v2/jobs/{jobId}/commit", content, token).ConfigureAwait(false);
      await Check(response).ConfigureAwait(false);
      _feed?.ExpectRevision(jobId);
    }
    private void EnsureAccount()
    {
      if (_destination?.UserId > 0 && (!Entry.CdeAccessController.CanUseLocalSync ||
          Entry.CdeAccessController.SignedInUserId != _destination.UserId)) throw new ReceiverException(403);
    }
    private static async Task Check(HttpResponseMessage response)
    {
      if (!response.IsSuccessStatusCode)
      {
        var retry = response.Headers.RetryAfter;
        string reason = null;
        if (response.Content.Headers.ContentType?.MediaType == "application/json" && response.Content.Headers.ContentLength is > 0 and <= 4096)
        {
          try
          {
            var error = Newtonsoft.Json.Linq.JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var text = error["error"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)error["error"] : null;
            if (text?.Length <= 300 && !text.Contains('\r') && !text.Contains('\n')) reason = text;
          }
          catch (JsonException) { /* Keep a stable HTTP diagnostic for non-protocol error bodies. */ }
        }
        throw new ReceiverException((int)response.StatusCode,
          retry?.Delta?.TotalSeconds ?? Math.Max(0, (retry?.Date - DateTimeOffset.UtcNow)?.TotalSeconds ?? 0), reason);
      }
    }
    public void SetPaused(bool paused) { _paused = paused; _feed?.Configure(_publisherSession, paused); }
    public void Dispose() { _feed?.Dispose(); _http.Dispose(); }
  }
}
