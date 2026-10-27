using System;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OpenProject.Revit.LiveSync.Transport
{
  /// <summary>Scoped ActionCable control transport. Geometry stays in the durable HTTP queue.</summary>
  public sealed class PublisherFeed : IDisposable
  {
    private readonly CdeDestination _destination;
    private readonly long _projectId, _channelId;
    private readonly CancellationTokenSource _cancel = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly object _gate = new();
    private ClientWebSocket _socket;
    private Task _run;
    private ReceiverStatus _status;
    private string _expectedRevision;
    private string _session = "";
    private bool _paused, _connected;
    private int _rejected, _version;
    private long _lastFrame;
    private int _disposed;

    public PublisherFeed(CdeDestination destination)
    {
      _destination = destination;
      var parts = destination.Address.AbsolutePath.Split('/');
      _projectId = long.Parse(parts[2]); _channelId = long.Parse(parts[6]);
    }

    public void Configure(string session, bool paused)
    {
      lock (_gate)
      {
        _paused = paused;
        if (_session == (session ?? "")) return;
        _session = session ?? ""; _version++; _status = null; _expectedRevision = null; _connected = false;
        _socket?.Abort();
        _run ??= Task.Run(RunAsync);
      }
    }

    public async Task<ReceiverStatus> ReadAsync(CancellationToken token)
    {
      lock (_gate) _run ??= Task.Run(RunAsync);
      var deadline = DateTime.UtcNow.AddSeconds(10);
      while (DateTime.UtcNow < deadline)
      {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
          if (_rejected != 0) throw new ReceiverException(_rejected);
          if (_connected && _status != null && Fresh() && (_expectedRevision == null || _status.RevisionId == _expectedRevision))
          { _expectedRevision = null; return _status; }
        }
        await Task.Delay(100, token).ConfigureAwait(false);
      }
      throw new HttpRequestException("No se pudo confirmar el canal en tiempo real del CDE");
    }

    // The pulse below answers with a status frame every 10 s, so silence of 35 s means a lost channel
    // (the CDE marks a publisher offline after 45 s). Freshness never depends on viewers being open.
    private const int PulseSeconds = 10;
    private bool Fresh() => DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastFrame) < TimeSpan.FromSeconds(35).Ticks;
    public void ExpectRevision(string revision)
    {
      lock (_gate) _expectedRevision = _status?.RevisionId == revision ? null : revision;
    }

    private async Task RunAsync()
    {
      var failures = 0;
      while (!_cancel.IsCancellationRequested)
      {
        int version;
        string session;
        using var socket = new ClientWebSocket();
        lock (_gate) { version = _version; session = _session; _socket = socket; }
        try
        {
          var address = new UriBuilder(_destination.Address) { Scheme = "wss", Path = "/cable/revit", Query = "" };
          socket.Options.AddSubProtocol("actioncable-v1-json");
          socket.Options.SetRequestHeader("Origin", _destination.Address.GetLeftPart(UriPartial.Authority));
          socket.Options.SetRequestHeader("Authorization", "Bearer " + _destination.Token);
          socket.Options.SetRequestHeader("X-Cmcoordinator-Project", _projectId.ToString());
          socket.Options.SetRequestHeader("X-Cmcoordinator-Channel", _channelId.ToString());
          using var connect = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
          connect.CancelAfter(TimeSpan.FromSeconds(12));
          await socket.ConnectAsync(address.Uri, connect.Token).ConfigureAwait(false);
          Interlocked.Exchange(ref _lastFrame, DateTime.UtcNow.Ticks);
          var identifier = JsonConvert.SerializeObject(new { channel = "Bim::RevitLive::PublisherChannel", session_id = session });
          using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
          var reader = ReceiveAsync(socket, identifier, version, lifetime.Token);
          var heartbeat = HeartbeatAsync(socket, identifier, lifetime.Token);
          await Task.WhenAny(reader, heartbeat).ConfigureAwait(false);
          lifetime.Cancel(); socket.Abort();
          try { await Task.WhenAll(reader, heartbeat).ConfigureAwait(false); }
          catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
          failures = 0;
        }
        catch (Exception error) when (error is WebSocketException or HttpRequestException or OperationCanceledException or JsonException or IOException)
        { /* Retain the durable outbox; never log tokens, headers or frames. */ }
        finally { lock (_gate) { _connected = false; _status = null; if (_socket == socket) _socket = null; } }
        lock (_gate) { if (_rejected != 0) return; }
        if (_cancel.IsCancellationRequested) return;
        try
        {
          if (version == _version)
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(failures++, 5))) + NetCompat.NextJitter()), _cancel.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
      }
    }

    private async Task ReceiveAsync(ClientWebSocket socket, string identifier, int version, CancellationToken token)
    {
      var buffer = new byte[8192];
      while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
      {
        using var message = new MemoryStream();
        WebSocketReceiveResult frame;
        do
        {
          frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
          if (frame.MessageType == WebSocketMessageType.Close) return;
          if (frame.MessageType != WebSocketMessageType.Text || message.Length + frame.Count > 65536)
            throw new HttpRequestException("Respuesta de control inválida");
          message.Write(buffer, 0, frame.Count);
        } while (!frame.EndOfMessage);
        lock (_gate) { if (_version != version) return; }
        Interlocked.Exchange(ref _lastFrame, DateTime.UtcNow.Ticks);
        var value = JObject.Parse(Encoding.UTF8.GetString(message.ToArray()));
        var type = (string)value["type"];
        if (type == "welcome") await SendAsync(socket, new { command = "subscribe", identifier }, token).ConfigureAwait(false);
        if (type == "confirm_subscription") { lock (_gate) { if (_version == version) _connected = true; } }
        if (type == "reject_subscription" || (type == "disconnect" && (bool?)value["reconnect"] == false))
        { lock (_gate) _rejected = 403; return; }
        if ((string)value["identifier"] != identifier || value["message"] is not JObject payload) continue;
        if ((string)payload["type"] == "publisher_error") { lock (_gate) _rejected = (int?)payload["status"] ?? 403; return; }
        if ((string)payload["type"] == "publisher_status")
        {
          var status = payload["status"]?.ToObject<ReceiverStatus>();
          if (status?.Application != "cmcoordinator-cde" || status.ChannelId != _channelId ||
              status.ProjectId != _projectId || (_destination.ConnectionId > 0 && status.ConnectionId != _destination.ConnectionId) || status.ModelId <= 0 || status.UserId != _destination.UserId ||
              Array.IndexOf(status.Protocols ?? Array.Empty<int>(), 2) < 0) throw new HttpRequestException("El canal no corresponde al destino autorizado");
          lock (_gate) { if (_version == version) _status = status; }
        }
      }
    }

    private async Task HeartbeatAsync(ClientWebSocket socket, string identifier, CancellationToken token)
    {
      var next = DateTime.MinValue;
      bool? previous = null;
      while (!token.IsCancellationRequested)
      {
        bool connected, paused;
        lock (_gate) { connected = _connected; paused = _paused; }
        if (!Fresh()) throw new HttpRequestException("Se perdió la señal del CDE");
        if (connected && (DateTime.UtcNow >= next || paused != previous))
        {
          await SendAsync(socket, new { command = "message", identifier, data = JsonConvert.SerializeObject(new { action = "pulse", paused }) }, token).ConfigureAwait(false);
          previous = paused; next = DateTime.UtcNow.AddSeconds(PulseSeconds);
        }
        await Task.Delay(1000, token).ConfigureAwait(false);
      }
    }

    private async Task SendAsync(ClientWebSocket socket, object payload, CancellationToken token)
    {
      await _send.WaitAsync(token).ConfigureAwait(false);
      try { await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload))), WebSocketMessageType.Text, true, token).ConfigureAwait(false); }
      finally { _send.Release(); }
    }
    public void Dispose()
    {
      if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
      _cancel.Cancel(); lock (_gate) _socket?.Abort();
    }
  }
}
