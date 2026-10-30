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
    // Optional capabilities of the CDE (sharedGeometry, gzip); older servers omit them.
    public string[] Features { get; set; }
    public bool Supports(string feature) => Array.IndexOf(Features ?? Array.Empty<string>(), feature) >= 0;
    // UI-8 logic 9: hash of the BIM catalog the CDE model has (null on older servers or before the first one).
    public string CatalogHash { get; set; }
    public string RevisionId { get; set; }
    public string ViewerRevision { get; set; }
    public string SourceId { get; set; }
    public string SessionId { get; set; }
    public long SessionStartedAt { get; set; }
    public int Count { get; set; }
    public long UserId { get; set; }
    // Name of the account this connection belongs to (older servers omit it).
    public string UserName { get; set; }
    public long ProjectId { get; set; }
    public long ModelId { get; set; }
    public long ChannelId { get; set; }
    public long ConnectionId { get; set; }
    public string PublisherRole { get; set; }
    public string PublishingUser { get; set; }
    public string ProjectName { get; set; }
    public string ModelName { get; set; }
    public string StateCode { get; set; }
    // Per-model group of this connection (feature "modelLevels"); older servers omit all three.
    public string AccessLevel { get; set; }
    public string[] AllowedActions { get; set; }
    public bool? ModelExists { get; set; }
    // Who published the current revision and when, and what the last replacement left to restore (older servers omit them).
    public string LastPublishedBy { get; set; }
    public DateTimeOffset? LastPublishedAt { get; set; }
    public RestorePointInfo RestorePoint { get; set; }
  }
  /// <summary>The revision a replacement of the model left behind; Administración can return to it.</summary>
  public sealed class RestorePointInfo
  {
    public string RevisionId { get; set; }
    public int ElementCount { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public string CreatedBy { get; set; }
  }
  /// <summary>Content hash of each element in the CDE revision, as its publisher computed it.</summary>
  public sealed class RemoteFingerprints
  {
    public string RevisionId { get; set; }
    public int ElementCount { get; set; }
    public Dictionary<string, string> Fingerprints { get; set; } = new();
    public bool Complete { get; set; }
    // UI-8 logic 8: ids removed from the model since its last complete capture (an older CDE sends none).
    public List<string> Removed { get; set; } = new();
  }
  public sealed class RemoteJob
  {
    public bool Committed { get; set; }
    public bool Waiting { get; set; }
    public string PublishingUser { get; set; }
    // Place in the CDE line counting the publication in progress (feature "queue"); 2 means next.
    public int? Position { get; set; }
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
    // The CDE no longer accepts this token: revoked, expired or unknown. Only a new connection helps.
    public bool Revoked => StatusCode == 401 || StatusCode == 403 && Message.Contains("revocada");
    /// <summary>UI-8 logic 4: the role lost «Ver modelos IFC»; not a revocation, a new token would not help.</summary>
    public bool NoPermission => StatusCode == 403 && Message.Contains(NoPermissionReason);
    public const string NoPermissionReason = "Tu rol ya no puede ver modelos IFC";
    public const string RevokedReason = "Conexión vencida, revocada o sin permiso";
  }
  public interface ISyncReceiver : IDisposable
  {
    Task<ReceiverStatus> StatusAsync(CancellationToken token);
    Task<RemoteJob> BeginAsync(SyncJob job, CancellationToken token);
    Task ChunkAsync(string jobId, int index, byte[] bytes, CancellationToken token);
    Task CommitAsync(string jobId, CancellationToken token);
  }
  public interface ISyncPresence { void SetPaused(bool paused); void Announce(string session); }
  /// <summary>UI-8 logic 5: whether the CDE already closed the real-time channel of this connection.</summary>
  public interface ISyncRejection { bool ChannelRejected { get; } }
  // Optional: a CDE announcing "merge" lists the huellas of its revision so only what differs is sent.
  public interface ISyncFingerprints { Task<RemoteFingerprints> FingerprintsAsync(CancellationToken token); }
  // Optional: returns the model to the revision its last replacement left as restore point (Administración only).
  public interface ISyncRestore { Task RestoreAsync(CancellationToken token); }
  public sealed class SyncReceiver : ISyncReceiver, ISyncPresence, ISyncFingerprints, ISyncRestore, ISyncRejection
  {
    private readonly HttpClient _http;
    private readonly Func<Task> _ensureService;
    private readonly CdeDestination _destination;
    private string _publisherSession;
    private string _presenceSession;
    private PublisherFeed _feed;
    public bool ChannelRejected => _feed?.Rejected == true;
    private bool _paused;
    private volatile bool _gzip;
    public SyncReceiver(Func<Task> ensureService = null, Uri baseAddress = null, CdeDestination destination = null)
    {
      _destination = destination;
      _ensureService = ensureService;
      _http = new HttpClient(new HttpClientHandler { UseProxy = destination != null, AllowAutoRedirect = false })
        { BaseAddress = destination?.Address ?? baseAddress ?? new Uri(LocalViewerEndpoint.Url), Timeout = TimeSpan.FromMinutes(30) };
      if (destination != null) _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", destination.Token);
    }
    public async Task<ReceiverStatus> StatusAsync(CancellationToken token)
    {
      EnsureAccount();
      if (_destination != null && _publisherSession != null)
      {
        _feed ??= new PublisherFeed(_destination);
        _feed.Configure(_publisherSession, _paused);
        var live = await _feed.ReadAsync(token).ConfigureAwait(false);
        _gzip = _destination != null && live.Supports("gzip");
        return live;
      }
      if (_ensureService != null) await _ensureService().ConfigureAwait(false);
      using var request = new HttpRequestMessage(HttpMethod.Get, "api/status");
      if (_destination != null && _publisherSession != null) request.Headers.Add("X-Publisher-Session", _publisherSession);
      using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
      await Check(response).ConfigureAwait(false);
      var value = JsonConvert.DeserializeObject<ReceiverStatus>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
      if (value?.Application != "cmcoordinator-cde" || Array.IndexOf(value.Protocols ?? Array.Empty<int>(), 3) < 0)
        throw new ReceiverException(426);
      _gzip = _destination != null && value.Supports("gzip");
      return value;
    }
    public async Task<RemoteFingerprints> FingerprintsAsync(CancellationToken token)
    {
      EnsureAccount();
      using var response = await _http.GetAsync("api/v3/fingerprints", token).ConfigureAwait(false);
      await Check(response).ConfigureAwait(false);
      return JsonConvert.DeserializeObject<RemoteFingerprints>(await response.Content.ReadAsStringAsync().ConfigureAwait(false))
        ?? new RemoteFingerprints();
    }
    public async Task RestoreAsync(CancellationToken token)
    {
      EnsureAccount();
      using var content = new StringContent("{}", Encoding.UTF8, "application/json");
      using var response = await _http.PostAsync("api/v3/restore", content, token).ConfigureAwait(false);
      await Check(response).ConfigureAwait(false);
    }
    public async Task<RemoteJob> BeginAsync(SyncJob job, CancellationToken token)
    {
      EnsureAccount();
      using var content = new StringContent(SyncEncoding.Json(job), Encoding.UTF8, "application/json");
      using var response = await _http.PostAsync("api/v3/jobs", content, token).ConfigureAwait(false);
      await Check(response).ConfigureAwait(false);
      _publisherSession = job.SessionId;
      _feed ??= _destination == null ? null : new PublisherFeed(_destination);
      _feed?.Configure(_publisherSession, _paused);
      return JsonConvert.DeserializeObject<RemoteJob>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }
    public async Task ChunkAsync(string jobId, int index, byte[] bytes, CancellationToken token)
    {
      EnsureAccount();
      // The hash always describes the uncompressed part, which is what the CDE stores.
      using var content = new ByteArrayContent(_gzip ? SyncEncoding.Gzip(bytes) : bytes);
      content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
      if (_gzip) content.Headers.ContentEncoding.Add("gzip");
      content.Headers.Add("X-Chunk-Sha256", SyncEncoding.Hash(bytes));
      using var response = await _http.PostAsync($"api/v3/jobs/{jobId}/chunks/{index}", content, token).ConfigureAwait(false);
      await Check(response).ConfigureAwait(false);
    }
    public async Task CommitAsync(string jobId, CancellationToken token)
    {
      EnsureAccount();
      using var content = new StringContent("{}", Encoding.UTF8, "application/json");
      using var response = await _http.PostAsync($"api/v3/jobs/{jobId}/commit", content, token).ConfigureAwait(false);
      await Check(response).ConfigureAwait(false);
      _feed?.ExpectRevision(jobId);
    }
    private void EnsureAccount()
    {
      if (_destination?.UserId > 0 &&
          !Entry.CdeAccessController.MatchesDestination(_destination.Address, _destination.UserId))
        throw new ReceiverException(403);
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
    public void SetPaused(bool paused) { _paused = paused; _feed?.Configure(_publisherSession ?? _presenceSession, paused); }
    // Opens the ActionCable control socket on Conectar so the CDE shows the model live before any publication.
    // Its 20 s pulse keeps presence; no HTTP polling is involved.
    public void Announce(string session)
    {
      if (_destination == null || string.IsNullOrEmpty(session)) return;
      _presenceSession = session;
      _feed ??= new PublisherFeed(_destination);
      _feed.Configure(_publisherSession ?? session, _paused);
    }
    public void Dispose() { _feed?.Dispose(); _http.Dispose(); }
  }
}
