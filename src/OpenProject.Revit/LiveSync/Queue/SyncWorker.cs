using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Microsoft.Data.Sqlite;
using OpenProject.Revit.LiveSync.Telemetry;
using OpenProject.Revit.LiveSync.Transport;

namespace OpenProject.Revit.LiveSync.Queue
{
  public sealed class SyncWorker : IDisposable
  {
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly ISyncReceiver _receiver;
    private readonly ISyncTelemetrySink _log;
    private readonly Task _worker;
    private readonly string _directory;
    private readonly Task _predecessor;
    private LiveSnapshot _latest;
    private long _generation, _persisted;
    private bool _reconcile;
    private volatile bool _captureHold, _inFlight;
    private volatile bool _paused, _validated, _attention, _waiting;
    private volatile SyncProgress _progress = new();
    private int _retry;
    private int _disposed;
    private string _session = Guid.NewGuid().ToString("N");
    private long _started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private string _historyStage;
    public SyncProgress Progress => _progress;
    public Task Completion => _worker;
    public bool IsPaused => _paused;

    public SyncWorker(string directory, ISyncReceiver receiver, ISyncTelemetrySink log, Task predecessor = null)
    { _directory = directory; _receiver = receiver; _log = log; _predecessor = predecessor; _worker = Task.Run(RunAsync); }

    public void Submit(LiveSnapshot snapshot, bool reconcile = false)
    {
      lock (_gate) { _latest = snapshot; _generation++; _reconcile |= reconcile; if (reconcile) _captureHold = false; }
    }
    public void Pause(bool value)
    {
      _paused = value;
      var previous = _progress;
      _progress = new SyncProgress { Stage = value ? _inFlight ? "Pausando" : "En pausa" : "Continuando",
        Detail = value ? _inFlight ? "Esperando confirmación del lote en vuelo" : "La captura continúa; no salen nuevos lotes" : "Retomando desde el último bloque confirmado",
        Done = previous.Done, Total = previous.Total, JobId = previous.JobId, Paused = value, Attention = _attention,
        ReceiverRevision = previous.ReceiverRevision, ViewerRevision = previous.ViewerRevision, Jobs = previous.Jobs,
        HasPendingChanges = previous.HasPendingChanges, PendingElements = previous.PendingElements };
      _log.Emit("queue", value ? "PAUSE" : "RESUME", value ? "Pausa solicitada" : "Continuación solicitada");
    }
    public void HoldForReconciliation() { _captureHold = true; _validated = false; Pause(true); }
    public void Retry() { _attention = false; Interlocked.Increment(ref _retry); }

    private async Task RunAsync()
    {
      if (_predecessor != null)
      {
        try { await _predecessor.WaitAsync(_cancel.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { _receiver.Dispose(); return; }
        catch (Exception) { /* The previous worker has released its lease in its using scope. */ }
      }
      while (!_cancel.IsCancellationRequested)
      {
        try
        {
          using var outbox = new SqliteOutbox(_directory);
          _persisted = 0; _validated = false;
          await PumpAsync(outbox).ConfigureAwait(false);
          break;
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { break; }
        catch (Exception error)
        {
          _attention = true;
          Report(null, "Error de cola local", "No se pudo conservar la cola. Comprueba espacio, permisos u otra instancia. " + SyncFailure.Describe(error));
          _log.Emit("storage", "QUEUE_WRITE_FAILED", "Falló el almacenamiento local: " + SyncFailure.Describe(error));
          var retry = _retry;
          try { while (!_cancel.IsCancellationRequested && retry == _retry) await Delay(250).ConfigureAwait(false); }
          catch (OperationCanceledException) { break; }
        }
      }
      _receiver.Dispose();
    }

    private async Task PumpAsync(SqliteOutbox outbox)
    {
      ReceiverStatus remote = null;
      var failures = 0;
      var retrySeen = _retry;
      string viewerSeen = null;
      DateTime nextProbe = DateTime.MinValue, nextAttempt = DateTime.MinValue;
      while (!_cancel.IsCancellationRequested)
      {
        SaveLatest(outbox);
        if (_receiver is ISyncPresence presence) presence.SetPaused(_paused || _captureHold);
        if (retrySeen != _retry) {
          if (_waiting) { outbox.Abandon("Publicación solicitada por el usuario; se conserva la captura más reciente"); _waiting = false; }
          retrySeen = _retry; nextAttempt = nextProbe = DateTime.MinValue; remote = null; }
        if (_attention || !_validated || _paused || _captureHold)
        {
          Report(outbox, _attention ? (_waiting ? "En espera de publicación" : "Requiere atención") : _paused ? "En pausa" : "Esperando captura",
            _attention ? _progress.Detail : "La cola local se conserva; no salen nuevos lotes", remote, outbox.Active, _progress.Done);
          await Delay(200).ConfigureAwait(false); continue;
        }
        if (DateTime.UtcNow < nextAttempt) { await Delay(200).ConfigureAwait(false); continue; }
        try
        {
          if (remote == null || DateTime.UtcNow >= nextProbe)
          {
            remote = await _receiver.StatusAsync(_cancel.Token).ConfigureAwait(false);
            nextProbe = DateTime.UtcNow.AddSeconds(2);
            if (remote.SourceId == outbox.Get("source") && remote.ViewerRevision == outbox.Get("revision") && remote.ViewerRevision != null && viewerSeen != remote.ViewerRevision)
            {
              outbox.History(remote.ViewerRevision, "Confirmado en visor", "El visor aplicó esta revisión");
              viewerSeen = remote.ViewerRevision;
            }
          }
          var job = outbox.Active;
          if (job != null && remote.RevisionId == job.JobId)
          { outbox.Complete(job); job = null; }
          if (job == null && (outbox.HasPending || remote.RevisionId != outbox.Get("revision") || remote.SessionId != _session))
          {
            if (remote.SessionStartedAt > _started) throw new ReceiverException(403);
            var reset = remote.RevisionId != outbox.Get("revision") || remote.SourceId != outbox.Get("source") || remote.SessionId != _session;
            job = outbox.Prepare(_session, _started, remote.RevisionId, reset);
            _log.Emit("queue", "JOB_PREPARED", "Trabajo preparado", job.JobId, bytes: job.ByteLength);
          }
          if (job == null)
          {
            Report(outbox, remote.ViewerRevision == remote.RevisionId ? "Al día" : "Guardado · visor pendiente",
              "Todos los cambios preparados están guardados en el destino seleccionado", remote);
            await Delay(250).ConfigureAwait(false); continue;
          }
          Report(outbox, "Sincronizando", "Consultando bloques confirmados", remote, job);
          _inFlight = true;
          RemoteJob saved;
          try { saved = await _receiver.BeginAsync(job, _cancel.Token).ConfigureAwait(false); }
          finally { _inFlight = false; }
          if (saved.Waiting)
          {
            WaitForPublication(outbox, "Está publicando " + (saved.PublishingUser ?? "otra conexión") + ". Cuando termine, pulsa Sincronizar para publicar tu captura.", remote, job);
            continue;
          }
          if (!saved.Committed)
          {
            var received = new HashSet<int>(saved.Received);
            for (var index = 0; index < job.ChunkCount; index++)
            {
              SaveLatest(outbox, duringJob: true);
              if (_paused || !_validated || _captureHold || _cancel.IsCancellationRequested) break;
              if (received.Contains(index)) continue;
              var bytes = outbox.Chunk(index);
              var watch = Stopwatch.StartNew();
              Report(outbox, "Enviando", $"Lote {index + 1} de {job.ChunkCount}", remote, job, received.Count);
              _inFlight = true;
              try { await _receiver.ChunkAsync(job.JobId, index, bytes, _cancel.Token).ConfigureAwait(false); }
              finally { _inFlight = false; }
              outbox.Ack(index); received.Add(index);
              _log.Emit("transport", "CHUNK_ACK", "Lote conservado por el receptor", job.JobId, watch.ElapsedMilliseconds, bytes.Length);
            }
            if (_paused || !_validated || _captureHold) { Report(outbox, "En pausa", "Último lote confirmado; cola conservada", remote, job); continue; }
            _cancel.Token.ThrowIfCancellationRequested();
            Report(outbox, "Validando revisión", "El destino conserva la revisión anterior hasta completar esta carga", remote, job, job.ChunkCount);
            _inFlight = true;
            try { await _receiver.CommitAsync(job.JobId, _cancel.Token).ConfigureAwait(false); }
            finally { _inFlight = false; }
          }
          outbox.Complete(job);
          _log.Emit("transport", "REVISION_STORED", "Revisión guardada en destino; presentación independiente", job.JobId);
          remote = null; failures = 0; nextAttempt = DateTime.MinValue;
        }
        catch (ReceiverException error) when (error.StatusCode == 423)
        {
          WaitForPublication(outbox, "Otra conexión tiene el permiso de publicación. Pulsa Sincronizar para volver a intentarlo.", remote, outbox.Active);
        }
        catch (ReceiverException error) when (error.StatusCode == 409)
        {
          outbox.Abandon("La base del destino cambió; se necesita reconciliación");
          remote = null; nextAttempt = DateTime.UtcNow.AddSeconds(1);
          _log.Emit("transport", "BASE_CONFLICT", "Cambió la base del destino; se consulta antes de reintentar");
        }
        catch (Exception error) when (error is SqliteException or IOException) { throw; }
        catch (Exception error) when (error is not OperationCanceledException || !_cancel.IsCancellationRequested)
        {
          var permanent = error is ReceiverException receiver && receiver.Permanent;
          var localDataError = error is InvalidOperationException;
          _attention = permanent || localDataError;
          var delay = Math.Min(30, Math.Pow(2, Math.Min(failures++, 5))) + Random.Shared.NextDouble();
          if (error is ReceiverException limited) delay = Math.Max(delay, limited.RetryAfterSeconds);
          nextAttempt = DateTime.UtcNow.AddSeconds(delay); remote = null;
          var detail = _attention ? "Revisa alcance, permisos o versión del receptor antes de reintentar. " + error.Message
            : $"Destino no disponible. Reintento en {Math.Ceiling(delay)} s; cola conservada.";
          Report(outbox, _attention ? "Requiere atención" : "Esperando conexión", detail, job: outbox.Active, done: _progress.Done);
          _log.Emit("transport", _attention ? "SYNC_REJECTED" : "DESTINATION_UNAVAILABLE",
            _attention ? "El receptor requiere atención: " + SyncFailure.Describe(error) : "Destino no disponible; reintento con espera");
        }
      }
    }

    private void WaitForPublication(SqliteOutbox box, string detail, ReceiverStatus remote, SyncJob job)
    {
      _attention = _waiting = true;
      Report(box, "En espera de publicación", detail, remote, job);
      _log.Emit("transport", "PUBLISHER_WAITING", "Cola conservada; se necesita solicitud manual de publicación");
    }

    private void SaveLatest(SqliteOutbox outbox, bool duringJob = false)
    {
      LiveSnapshot latest; long generation; bool reconcile;
      lock (_gate) { latest = _latest; generation = _generation; reconcile = _reconcile; }
      if (latest == null || generation == _persisted) return;
      var watch = Stopwatch.StartNew();
      var changed = outbox.Save(latest);
      // A reconciled snapshot must not remove the chunks that the current loop still references.
      if (reconcile && duringJob) return;
      if (reconcile) { _session = Guid.NewGuid().ToString("N"); _started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
      if ((reconcile || (!_validated && changed)) && outbox.Active != null)
        outbox.Abandon("Modelo reconciliado con Revit");
      if (!_validated && !changed && outbox.Active is { } restored)
      { _session = restored.SessionId; _started = restored.SessionStartedAt; }
      lock (_gate) { if (generation == _generation) _reconcile = false; }
      _persisted = generation; _validated = !_captureHold;
      _log.Emit("storage", "LOCAL_SAVED", "Cambios preparados conservados en la cola local", durationMs: watch.ElapsedMilliseconds, elements: latest.Elements.Count);
    }

    private void Report(SqliteOutbox box, string stage, string detail, ReceiverStatus remote = null, SyncJob job = null, int done = 0)
    {
      if (job != null && box != null && _historyStage != job.JobId + stage)
      { box.History(job.JobId, stage, detail); _historyStage = job.JobId + stage; }
      if (box != null && remote?.SourceId != box.Get("source")) remote = null;
      _progress = new SyncProgress { Stage = stage, Detail = detail, JobId = job?.JobId, Done = done, Total = job?.ChunkCount ?? 0,
        Paused = _paused, Attention = _attention, ReceiverRevision = remote?.RevisionId ?? _progress.ReceiverRevision,
        ViewerRevision = remote?.ViewerRevision ?? _progress.ViewerRevision, Jobs = box?.History() ?? _progress.Jobs,
        PendingElements = box?.PendingElements ?? _progress.PendingElements, HasPendingChanges = box?.HasPending ?? _progress.HasPendingChanges };
    }
    private Task Delay(int milliseconds) => Task.Delay(milliseconds, _cancel.Token);
    public void Dispose()
    { if (Interlocked.Exchange(ref _disposed, 1) != 0) return; _cancel.Cancel(); _ = _worker.ContinueWith(_ => _cancel.Dispose(), TaskScheduler.Default); }
  }
}
