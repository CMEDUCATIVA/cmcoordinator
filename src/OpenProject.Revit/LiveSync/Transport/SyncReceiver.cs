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
  }
  public sealed class RemoteJob
  {
    public bool Committed { get; set; }
    public List<int> Received { get; set; } = new();
  }
  public sealed class ReceiverException : Exception
  {
    public int StatusCode { get; }
    public double RetryAfterSeconds { get; }
    public ReceiverException(int code, double retryAfter = 0) : base("El receptor devolvió HTTP " + code) { StatusCode = code; RetryAfterSeconds = retryAfter; }
    public bool Permanent => StatusCode is 400 or 401 or 403 or 404 or 413 or 415 or 422 or 426 or 507;
  }
  public interface ISyncReceiver : IDisposable
  {
    Task<ReceiverStatus> StatusAsync(CancellationToken token);
    Task<RemoteJob> BeginAsync(SyncJob job, CancellationToken token);
    Task ChunkAsync(string jobId, int index, byte[] bytes, CancellationToken token);
    Task CommitAsync(string jobId, CancellationToken token);
  }
  public sealed class SyncReceiver : ISyncReceiver
  {
    private readonly HttpClient _http;
    private readonly Func<Task> _ensureService;
    public SyncReceiver(Func<Task> ensureService = null, Uri baseAddress = null)
    {
      _ensureService = ensureService;
      _http = new HttpClient(new HttpClientHandler { UseProxy = false })
        { BaseAddress = baseAddress ?? new Uri(LocalViewerEndpoint.Url), Timeout = TimeSpan.FromSeconds(30) };
    }
    public async Task<ReceiverStatus> StatusAsync(CancellationToken token)
    {
      if (_ensureService != null) await _ensureService().ConfigureAwait(false);
      using var response = await _http.GetAsync("api/status", token).ConfigureAwait(false);
      await Check(response).ConfigureAwait(false);
      var value = JsonConvert.DeserializeObject<ReceiverStatus>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
      if (value?.Application != "cmcoordinator-local-live-viewer" || Array.IndexOf(value.Protocols ?? Array.Empty<int>(), 2) < 0)
        throw new ReceiverException(426);
      return value;
    }
    public async Task<RemoteJob> BeginAsync(SyncJob job, CancellationToken token)
    {
      using var content = new StringContent(SyncEncoding.Json(job), Encoding.UTF8, "application/json");
      using var response = await _http.PostAsync("api/v2/jobs", content, token).ConfigureAwait(false);
      await Check(response).ConfigureAwait(false);
      return JsonConvert.DeserializeObject<RemoteJob>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }
    public async Task ChunkAsync(string jobId, int index, byte[] bytes, CancellationToken token)
    {
      using var content = new ByteArrayContent(bytes);
      content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
      content.Headers.Add("X-Chunk-Sha256", SyncEncoding.Hash(bytes));
      using var response = await _http.PostAsync($"api/v2/jobs/{jobId}/chunks/{index}", content, token).ConfigureAwait(false);
      await Check(response).ConfigureAwait(false);
    }
    public async Task CommitAsync(string jobId, CancellationToken token)
    {
      using var content = new StringContent("{}", Encoding.UTF8, "application/json");
      using var response = await _http.PostAsync($"api/v2/jobs/{jobId}/commit", content, token).ConfigureAwait(false);
      await Check(response).ConfigureAwait(false);
    }
    private static Task Check(HttpResponseMessage response)
    {
      if (!response.IsSuccessStatusCode)
      {
        var retry = response.Headers.RetryAfter;
        throw new ReceiverException((int)response.StatusCode,
          retry?.Delta?.TotalSeconds ?? Math.Max(0, (retry?.Date - DateTimeOffset.UtcNow)?.TotalSeconds ?? 0));
      }
      return Task.CompletedTask;
    }
    public void Dispose() => _http.Dispose();
  }
}
