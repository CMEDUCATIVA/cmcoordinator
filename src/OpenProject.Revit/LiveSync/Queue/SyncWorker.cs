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
    // Capability the CDE announces when it can store and show 2D drawings.
    public const string DrawingsFeature = "drawings2d";
    // Capability of a CDE that merges publications from several people and sessions instead of replacing them.
    public const string MergeFeature = "merge";
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly ISyncReceiver _receiver;
    private readonly ISyncTelemetrySink _log;
    private readonly Task _worker;
    private readonly string _directory;
    public string DirectoryPath => _directory;
    private readonly Task _predecessor;
    private LiveSnapshot _latest;
    private long _generation, _persisted;
    private bool _reconcile;
    private volatile bool _captureHold, _inFlight;
    private volatile bool _preparing;
    private volatile bool _publishRequested;
    private volatile bool _awaitSnapshot;
    private long _awaitGeneration;
    private volatile bool _paused, _validated, _attention, _waiting;
    // «Reemplazar modelo…»: the next publication is a complete capture that replaces the CDE content.
    private volatile bool _replace;
    private volatile bool _mergeCapable;
    private volatile SyncProgress _progress = new();
    private volatile CdeModelAccess _access = CdeModelAccess.Unknown;
    public const string ReadOnlyDetail = "Tu grupo en este modelo es Lectura: ves el modelo y su estado, pero no envías cambios. Pide al administrador del proyecto que te pase a Editor.";
    // UI-8 logics 4 and 5: the same treatment from every branch (sending, waiting or paused); logged once.
    private void MarkRevoked(SqliteOutbox outbox)
    {
      var first = !_revoked;
      _attention = _revoked = true; _publishRequested = false;
      Report(outbox, "Conexión revocada", RevokedDetail, job: outbox.Active, done: _progress.Done);
      if (first) _log.Emit("transport", "CONNECTION_REVOKED", "El CDE ya no acepta esta conexión; cola conservada");
    }
    private void MarkNoPermission(SqliteOutbox outbox)
    {
      var first = !_noPermission;
      _noPermission = true; _publishRequested = false;
      Report(outbox, "Sin permiso", NoPermissionDetail, job: outbox.Active, done: _progress.Done);
      if (first) _log.Emit("transport", "NO_PERMISSION", "El rol ya no tiene «Ver modelos IFC»; cola conservada");
    }
    private void PermissionBack()
    {
      _noPermission = false;
      _log.Emit("transport", "PERMISSION_BACK", "Se recuperó «Ver modelos IFC»; la sincronización sigue");
    }
    public const string CatalogReuseFeature = "catalogReuse";
    public const string NoPermissionDetail = "Tu rol ya no puede ver modelos IFC en este proyecto. Pídeselo a quien administre el proyecto; cuando lo tengas, seguirá sola. Tus cambios quedan guardados en este equipo.";
    public const string RevokedDetail = "El CDE ya no acepta esta conexión: Administración la revocó o venció su token. Lo publicado se conserva en el CDE y tus cambios quedan guardados en este equipo.";
    private volatile bool _revoked;
    private volatile bool _noPermission;
    // A «Sincronizar» pressed while a job is being sent (its capture is not in the queue yet) is kept for after it.
    private int _retryAtJob;
    private volatile int _queuePosition;
    private volatile Transport.RestorePointInfo _restorePoint;
    // UI-8 #24, #26 and #29: the comparison read when connecting, whether this connection already stored a revision,
    // and a first merge that would send nearly the whole model and waits for confirmation.
    private volatile ReplacePreview _comparison;
    private volatile bool _compared, _joined, _needsConfirmation, _fullMergeConfirmed;
    public const string ConfirmStage = "Confirmar sincronización";
    // Questions from the panel that need the queue or the CDE; the worker answers them between steps.
    private TaskCompletionSource<ReplacePreview> _preview;
    private TaskCompletionSource<bool> _restore;
    private int _retry;
    private int _disposed;
    private string _session = Guid.NewGuid().ToString("N");
    private long _started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private string _historyStage;
    public SyncProgress Progress => _progress;
    public CdeModelAccess Access => _access;
    public Task Completion => _worker;
    public bool IsPaused => _paused;
    public bool IsPreparing => _preparing;
    public bool IsPublishRequested => _publishRequested;
    public bool IsAwaitingSnapshot => _awaitSnapshot;
    // Live mode: any change persisted in the queue is published without a new request, including
    // edits captured while the previous revision was still being sent.
    public bool Live { get => _live; set => _live = value; }
    private volatile bool _live;

    public SyncWorker(string directory, ISyncReceiver receiver, ISyncTelemetrySink log, Task predecessor = null)
    { _directory = directory; _receiver = receiver; _log = log; _predecessor = predecessor; _worker = Task.Run(RunAsync); }

    public void Submit(LiveSnapshot snapshot, bool reconcile = false)
    {
      lock (_gate) { _latest = snapshot; _generation++; _reconcile |= reconcile; if (reconcile) _captureHold = false; }
    }
    public void Pause(bool value)
    {
      if (value) { _publishRequested = false; _awaitSnapshot = false; }
      _paused = value;
      var previous = _progress;
      _progress = new SyncProgress { Stage = value ? _inFlight ? "Pausando" : "En pausa" : "Continuando",
        Detail = value ? _inFlight ? "Esperando confirmación del lote en vuelo" : "La captura continúa; no salen nuevos lotes" : "Retomando desde el último bloque confirmado",
        Done = previous.Done, Total = previous.Total, JobId = previous.JobId, Paused = value, Attention = _attention, Revoked = _revoked, NoPermission = _noPermission, RestorePoint = _restorePoint,
        ReceiverRevision = previous.ReceiverRevision, ViewerRevision = previous.ViewerRevision, Jobs = previous.Jobs,
        HasPendingChanges = previous.HasPendingChanges, Drawings = previous.Drawings, DrawingsPending = previous.DrawingsPending, DrawingsSupported = previous.DrawingsSupported, PendingElements = previous.PendingElements,
        BytesDone = previous.BytesDone, BytesTotal = previous.BytesTotal, Access = _access,
        Comparison = _comparison, Joined = _joined, NeedsConfirmation = _needsConfirmation };
      _log.Emit("queue", value ? "PAUSE" : "RESUME", value ? "Pausa solicitada" : "Continuación solicitada");
    }
    public void HoldForReconciliation()
    {
      _captureHold = true; _validated = false; _paused = true;
      _log.Emit("queue", "RECONCILE_HOLD", "Publicación en espera de una captura completa");
    }
    public void RequestPublish()
    {
      _attention = false; _revoked = false; _paused = false; _publishRequested = true;
      Interlocked.Increment(ref _retry);
      var previous = _progress;
      _progress = new SyncProgress { Stage = "Solicitud de sincronización", Detail = "Esperando la captura local más reciente",
        Done = 0, Total = 0, Paused = false, Attention = false,
        ReceiverRevision = previous.ReceiverRevision, ViewerRevision = previous.ViewerRevision, Jobs = previous.Jobs,
        HasPendingChanges = previous.HasPendingChanges, Drawings = previous.Drawings, DrawingsPending = previous.DrawingsPending, DrawingsSupported = previous.DrawingsSupported, PendingElements = previous.PendingElements,
        Access = _access, RestorePoint = _restorePoint, Comparison = _comparison, Joined = _joined, NeedsConfirmation = _needsConfirmation };
      _log.Emit("queue", "PUBLISH_REQUESTED", "Sincronización solicitada por el usuario");
    }
    /// <summary>The person accepts sending a first merge in which nearly everything differs from the CDE (UI-8 #29).</summary>
    public void ConfirmFullMerge()
    {
      _fullMergeConfirmed = true; _needsConfirmation = false;
      _log.Emit("queue", "FULL_MERGE_CONFIRMED", "Confirmada la fusión de una captura que difiere del CDE en casi todo");
      RequestPublish();
    }
    /// <summary>Compares the last local capture with the huellas of the CDE before «Reemplazar modelo…» is confirmed.</summary>
    public Task<ReplacePreview> PreviewReplaceAsync()
    {
      var source = new TaskCompletionSource<ReplacePreview>(TaskCreationOptions.RunContinuationsAsynchronously);
      Interlocked.Exchange(ref _preview, source)?.TrySetCanceled();
      return source.Task;
    }
    /// <summary>Returns the CDE model to the revision its last replacement left as restore point (Administración).</summary>
    public Task RestoreAsync()
    {
      var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
      Interlocked.Exchange(ref _restore, source)?.TrySetCanceled();
      return source.Task;
    }
    public void Retry() => RequestPublish();
    public void RequestReplace() { _replace = true; _needsConfirmation = false; RequestPublish(); _log.Emit("queue", "REPLACE_REQUESTED", "Reemplazo del modelo solicitado"); }
    // Publication waits for the capture that the same click started; nothing older is sent first.
    public void AwaitSnapshot()
    { lock (_gate) { _awaitGeneration = _generation; _awaitSnapshot = true; } }
    public void CancelPublish()
    { _publishRequested = false; _awaitSnapshot = false; }

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
      DateTime nextProbe = DateTime.MinValue, nextAttempt = DateTime.MinValue, nextAccessProbe = DateTime.MinValue;
      if (_receiver is ISyncPresence announcer) announcer.Announce(_session);
      while (!_cancel.IsCancellationRequested)
      {
        SaveLatest(outbox);
        if (await AnswerPanelAsync(outbox).ConfigureAwait(false)) { remote = null; nextProbe = DateTime.MinValue; }
        if (_live && !_publishRequested && !_paused && !_attention && !_captureHold && !_needsConfirmation && !_access.ReadOnly && outbox.HasPending) _publishRequested = true;
        if (_receiver is ISyncPresence presence) presence.SetPaused(_paused || _captureHold);
        if (retrySeen != _retry) {
          if (_waiting) { outbox.Abandon("Publicación solicitada por el usuario; se conserva la captura más reciente"); _waiting = false; }
          retrySeen = _retry; nextAttempt = nextProbe = DateTime.MinValue; remote = null; }
        if (_attention || !_validated || _paused || _captureHold || _awaitSnapshot || _noPermission)
        {
          // The panel shows the group from the moment of connecting, before any publication. UI-8 logic 5: a channel the
          // CDE just closed is checked at once, so «Revocada» shows within one pulse (10 s) also while paused.
          if (!_revoked && _receiver is ISyncRejection rejection && rejection.ChannelRejected) nextAccessProbe = DateTime.MinValue;
          if (!_revoked && DateTime.UtcNow >= nextAccessProbe)
          {
            nextAccessProbe = DateTime.UtcNow.AddSeconds(_noPermission ? 10 : 15);
            try
            {
              remote = await _receiver.StatusAsync(_cancel.Token).ConfigureAwait(false); nextProbe = DateTime.UtcNow.AddSeconds(2);
              if (_noPermission) PermissionBack();
              if (!_compared && remote.ModelExists != false && remote.Count > 0) await CompareOnConnectAsync(outbox, remote).ConfigureAwait(false);
            }
            catch (ReceiverException error) when (error.Revoked) { MarkRevoked(outbox); }
            catch (ReceiverException error) when (error.NoPermission) { MarkNoPermission(outbox); }
            catch (Exception error) when (error is not OperationCanceledException || !_cancel.IsCancellationRequested)
            { /* Informative only: publication reads the status again and reports its own failure. */ }
          }
          if (_noPermission && !_revoked) { Report(outbox, "Sin permiso", NoPermissionDetail, remote, outbox.Active, _progress.Done); await Delay(200).ConfigureAwait(false); continue; }
          Report(outbox, _attention ? (_waiting ? "En espera de publicación" : _revoked ? "Conexión revocada" : "Requiere atención") : _paused ? "En pausa" : "Esperando captura",
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
            if (_noPermission) PermissionBack();
            outbox.SetDrawingsSupported(remote.Supports(DrawingsFeature));
            if (remote.SourceId == outbox.Get("source") && remote.ViewerRevision == outbox.Get("revision") && remote.ViewerRevision != null && viewerSeen != remote.ViewerRevision)
            {
              outbox.History(remote.ViewerRevision, "Revisión aplicada", "El destino aplicó esta revisión");
              viewerSeen = remote.ViewerRevision;
            }
          }
          var job = outbox.Active;
          if (job != null && remote.RevisionId == job.JobId)
          { outbox.Complete(job); job = null; }
          // Lectura never sends: the CDE would reject it, and the queue is kept for when the group changes.
          if (_publishRequested && CdeModelAccess.From(remote).ReadOnly)
          {
            _publishRequested = false;
            _log.Emit("transport", "READ_ONLY", "Grupo Lectura en el modelo: no se envían cambios; cola conservada");
          }
          if (!_publishRequested)
          {
            if (CdeModelAccess.From(remote).ReadOnly) Report(outbox, "Solo lectura", ReadOnlyDetail, remote);
            else if (_needsConfirmation) Report(outbox, ConfirmStage, ConfirmDetail(_comparison), remote);
            else Report(outbox, outbox.HasPending ? "Listo para sincronizar" : "Al día",
              outbox.HasPending ? "Captura guardada localmente · pulsa Sincronizar para publicarla" : "Sin cambios pendientes de publicación",
              remote);
            await Delay(250).ConfigureAwait(false); continue;
          }
          var merging = _mergeCapable = remote.Supports(MergeFeature);
          var ownRevision = remote.RevisionId != null && remote.RevisionId == outbox.Get("revision") && outbox.Get("forceReset") != "1";
          // Merging, another person's revision is no reason to publish: only own changes, a lost job, a first
          // publication or a replacement are. Publishing otherwise would send stale copies of their elements.
          var due = merging
            ? outbox.HasPending || _replace || outbox.Get("forceReset") == "1" || outbox.Get("revision") == null
            : outbox.HasPending || remote.RevisionId != outbox.Get("revision") || remote.SessionId != _session;
          if (job == null && due)
          {
            if (remote.SessionStartedAt > _started) throw new ReceiverException(403);
            // Replace only on request or to fill an empty model; otherwise continue on the revision of the CDE.
            var reset = merging ? _replace || remote.RevisionId == null
              : remote.RevisionId != outbox.Get("revision") || remote.SourceId != outbox.Get("source") || remote.SessionId != _session;
            IReadOnlyDictionary<string, string> prints = null;
            IReadOnlyCollection<string> gone = null;
            if (merging && !reset && !ownRevision && _receiver is ISyncFingerprints fingerprints)
            {
              Report(outbox, "Comparando con el CDE", "Leyendo las huellas del modelo para enviar solo lo que cambió", remote);
              var remotePrints = await fingerprints.FingerprintsAsync(_cancel.Token).ConfigureAwait(false);
              prints = remotePrints.Fingerprints ?? new Dictionary<string, string>();
              gone = remotePrints.Removed;
              // A queue that never published sends what differs from the CDE: when that is nearly everything, ask first.
              // With a starting point nothing old is sent, so there is nothing to confirm (UI-8 logic 8).
              if (!_fullMergeConfirmed && outbox.Get("revision") == null && !outbox.HasBaseline)
              {
                var check = Compare(outbox, remote, remotePrints);
                _comparison = check;
                if (check.MostlyDifferent)
                {
                  _needsConfirmation = true; _publishRequested = false;
                  Report(outbox, ConfirmStage, ConfirmDetail(check), remote);
                  _log.Emit("queue", "FULL_MERGE_HELD", $"Fusión en espera de confirmación: {check.Added + check.Changed:N0} de {check.Local:N0} elementos difieren del CDE");
                  continue;
                }
              }
            }
            Report(outbox, "Preparando paquetes", "Agrupando elementos en paquetes reanudables", remote);
            _retryAtJob = _retry;
            _preparing = true;
            try { job = outbox.Prepare(_session, _started, remote.RevisionId, reset, ReportPreparation, compact: remote.Supports("sharedGeometry"), drawings: remote.Supports(DrawingsFeature),
              merge: merging && !reset, remote: prints, gone: gone, remoteCatalog: remote.Supports(CatalogReuseFeature) ? remote.CatalogHash : null); }
            finally { _preparing = false; }
            if (reset) _replace = false;
            _log.Emit("queue", "JOB_PREPARED", job.Reset ? "Trabajo preparado · captura completa" : job.Merge == true ? "Trabajo preparado · fusionar" : "Trabajo preparado", job.JobId, bytes: job.ByteLength);
          }
          if (job == null)
          {
            _publishRequested = false;
            Report(outbox, "Al día", "La revisión vigente está guardada en el CDE", remote);
            await Delay(250).ConfigureAwait(false); continue;
          }
          Report(outbox, "Sincronizando", "Consultando bloques confirmados", remote, job);
          _inFlight = true;
          RemoteJob saved;
          try { saved = await _receiver.BeginAsync(job, _cancel.Token).ConfigureAwait(false); }
          finally { _inFlight = false; }
          if (saved.Waiting)
          {
            if (merging) { WaitInLine(outbox, saved.PublishingUser, saved.Position, remote, job); nextAttempt = DateTime.UtcNow.AddSeconds(3 + NetCompat.NextJitter()); remote = null; continue; }
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
              Report(outbox, "Enviando", $"Paquete {index + 1} de {job.ChunkCount} · {FormatBytes(outbox.AcknowledgedBytes)} de {FormatBytes(job.ByteLength)}", remote, job, received.Count);
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
          _joined = true;
          _publishRequested = outbox.HasPending || _retry != _retryAtJob;
          _log.Emit("transport", "REVISION_STORED", "Revisión guardada en destino; presentación independiente", job.JobId);
          remote = null; failures = 0; nextAttempt = DateTime.MinValue;
        }
        catch (ReceiverException error) when (error.StatusCode == 423 && _mergeCapable)
        {
          WaitInLine(outbox, null, null, null, outbox.Active);
          remote = null; nextAttempt = DateTime.UtcNow.AddSeconds(3 + NetCompat.NextJitter());
        }
        catch (ReceiverException error) when (error.StatusCode == 423)
        {
          WaitForPublication(outbox, "Otra conexión tiene el permiso de publicación. Pulsa Sincronizar para volver a intentarlo.", remote, outbox.Active);
        }
        // Revoked, expired or unknown token: nothing succeeds with it until a new connection; the queue is kept.
        catch (ReceiverException error) when (error.Revoked) { MarkRevoked(outbox); remote = null; }
        catch (ReceiverException error) when (error.NoPermission) { MarkNoPermission(outbox); remote = null; }
        // Moved to Lectura while sending: the CDE refuses the job with this reason; it is a group, not a failure.
        catch (ReceiverException error) when (error.StatusCode == 403 && error.Message.Contains("Solo lectura"))
        {
          _publishRequested = false; remote = null; nextProbe = DateTime.MinValue;
          Report(outbox, "Solo lectura", ReadOnlyDetail, job: outbox.Active, done: _progress.Done);
          _log.Emit("transport", "READ_ONLY", "El CDE rechazó el envío: grupo Lectura; cola conservada");
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
          if (_attention) _publishRequested = false;
          // Resending a job whose content the CDE rejected fails the same way; the next Synchronize prepares it again.
          if (error is ReceiverException { StatusCode: 422 } && outbox.Active != null)
            outbox.Abandon("El CDE rechazó el contenido de los paquetes; se volverán a preparar al sincronizar");
          var delay = Math.Min(30, Math.Pow(2, Math.Min(failures++, 5))) + NetCompat.NextJitter();
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

    // The CDE governs arrival: one publication at a time, the next one leaves as soon as the turn is free.
    private void WaitInLine(SqliteOutbox box, string publishingUser, int? position, ReceiverStatus remote, SyncJob job)
    {
      _queuePosition = position ?? 0;
      var ahead = publishingUser != null || position == null ? "Está publicando " + (publishingUser ?? "otra conexión") + ". " : "Hay envíos que llegaron antes que el tuyo. ";
      Report(box, "En cola del CDE", (position > 0 ? $"Posición {position} en la cola del CDE. " : "") + ahead +
        "Tu envío sale en cuanto le toque; no hace falta pulsar nada.", remote, job);
      _log.Emit("transport", "PUBLISHER_QUEUED", "Turno ocupado en el CDE; reintento automático");
    }
    private async Task<bool> AnswerPanelAsync(SqliteOutbox outbox)
    {
      var changed = false;
      var preview = Interlocked.Exchange(ref _preview, null);
      if (preview != null)
      {
        try
        {
          var status = await _receiver.StatusAsync(_cancel.Token).ConfigureAwait(false);
          var prints = _receiver is ISyncFingerprints source ? await source.FingerprintsAsync(_cancel.Token).ConfigureAwait(false) : null;
          preview.TrySetResult(Compare(outbox, status, prints));
        }
        catch (Exception error) { preview.TrySetException(error); }
      }
      var restore = Interlocked.Exchange(ref _restore, null);
      if (restore != null)
      {
        try
        {
          if (_receiver is not ISyncRestore restorer) throw new ReceiverException(404, 0, "Este CDE no guarda puntos de restauración");
          await restorer.RestoreAsync(_cancel.Token).ConfigureAwait(false);
          _restorePoint = null; changed = true;
          _log.Emit("transport", "MODEL_RESTORED", "Modelo devuelto al punto de restauración del último reemplazo");
          restore.TrySetResult(true);
        }
        catch (Exception error) { restore.TrySetException(error); }
      }
      return changed;
    }
    /// <summary>What the last local capture is against the CDE revision (huellas); without a capture only the size is known.</summary>
    private static ReplacePreview Compare(SqliteOutbox outbox, ReceiverStatus status, RemoteFingerprints prints)
    {
      var (local, added, modified, removed) = prints == null ? (0, 0, 0, 0) : outbox.CompareWithRemote(prints.Fingerprints ?? new Dictionary<string, string>());
      return new ReplacePreview { RemoteCount = status.Count, LastPublishedBy = status.LastPublishedBy, LastPublishedAt = status.LastPublishedAt,
        Compared = local > 0 && prints != null, Exact = prints?.Complete == true, Local = local, Added = added, Changed = modified, Removed = removed };
    }
    // UI-8 #24: once per connection, before anything is sent, the panel learns whether this Revit matches the CDE model.
    private async Task CompareOnConnectAsync(SqliteOutbox outbox, ReceiverStatus remote)
    {
      if (_receiver is not ISyncFingerprints source || !remote.Supports(MergeFeature)) { _compared = true; return; }
      var compared = Compare(outbox, remote, await source.FingerprintsAsync(_cancel.Token).ConfigureAwait(false));
      _comparison = compared; _compared = true;
      _log.Emit("transport", "MODEL_COMPARED", compared.Compared
        ? $"Comparado con el CDE al conectar: {compared.Unchanged:N0} iguales · +{compared.Added:N0} · {compared.Changed:N0} cambiados · −{compared.Removed:N0}"
        : $"Modelo del CDE con {compared.RemoteCount:N0} elementos; aún no hay captura local con la que comparar");
    }
    private void RecordBaseline(SqliteOutbox outbox)
    {
      if (outbox.RecordBaseline())
        _log.Emit("queue", "BASELINE_RECORDED", "Punto de partida guardado: solo se enviará lo que cambies desde ahora");
    }
    public static string ConfirmDetail(ReplacePreview check) => check == null ? "Confirma la sincronización para continuar."
      : $"{check.Added + check.Changed:N0} de {check.Local:N0} elementos de tu Revit son nuevos o distintos en el CDE: se enviaría casi todo el modelo. " +
        "Es una fusión: no quita nada de otros. Pulsa Sincronizar para confirmarlo.";
    private void WaitForPublication(SqliteOutbox box, string detail, ReceiverStatus remote, SyncJob job)
    {
      _attention = _waiting = true; _publishRequested = false;
      Report(box, "En espera de publicación", detail, remote, job);
      _log.Emit("transport", "PUBLISHER_WAITING", "Cola conservada; se necesita solicitud manual de publicación");
    }

    private void SaveLatest(SqliteOutbox outbox, bool duringJob = false)
    {
      LiveSnapshot latest; long generation; bool reconcile;
      lock (_gate) { latest = _latest; generation = _generation; reconcile = _reconcile; }
      if (latest == null || generation == _persisted) return;
      var watch = Stopwatch.StartNew();
      var saving = _publishRequested && !duringJob;
      if (saving) _preparing = true;
      bool changed;
      try { changed = outbox.Save(latest); }
      finally { if (saving) _preparing = false; }
      // UI-8 logic 8: the first capture of a queue that never published is this copy as it was when connecting, the
      // starting point. A first publication to an empty model is a complete capture, which sends everything anyway.
      if (changed && outbox.Get("revision") == null) RecordBaseline(outbox);
      lock (_gate) { if (_awaitSnapshot && generation > _awaitGeneration) _awaitSnapshot = false; }
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
      if (remote != null) { _access = CdeModelAccess.From(remote); _restorePoint = remote.RestorePoint; }
      if (job != null && box != null && _historyStage != job.JobId + stage)
      { box.History(job.JobId, stage, detail); _historyStage = job.JobId + stage; }
      if (box != null && remote?.SourceId != box.Get("source")) remote = null;
      _progress = new SyncProgress { Stage = stage, Detail = detail, JobId = job?.JobId, Done = done, Total = job?.ChunkCount ?? 0,
        BytesDone = box?.AcknowledgedBytes ?? _progress.BytesDone, BytesTotal = job?.ByteLength ?? _progress.BytesTotal,
        Paused = _paused, Attention = _attention, ReceiverRevision = remote?.RevisionId ?? _progress.ReceiverRevision,
        ViewerRevision = remote?.ViewerRevision ?? _progress.ViewerRevision, Jobs = box?.History() ?? _progress.Jobs,
        PendingElements = box?.PendingElements ?? _progress.PendingElements, HasPendingChanges = box?.HasPending ?? _progress.HasPendingChanges,
        Drawings = box?.DrawingCount ?? _progress.Drawings, DrawingsPending = box?.HasPendingDrawings ?? _progress.DrawingsPending,
        DrawingsSupported = box == null ? _progress.DrawingsSupported : box.Get("drawingsSupported") == "1", Access = _access,
        QueuePosition = stage == "En cola del CDE" ? _queuePosition : 0, Revoked = _revoked, NoPermission = _noPermission, RestorePoint = _restorePoint,
        Comparison = _comparison, Joined = _joined, NeedsConfirmation = _needsConfirmation };
    }
    private void ReportPreparation(int done, int total, long bytes)
    {
      var previous = _progress;
      var percent = total == 0 ? 100 : (int)(100L * done / total);
      _progress = new SyncProgress
      {
        Stage = "Preparando paquetes",
        Detail = $"Procesando elementos para los paquetes · {done:N0} de {total:N0} · {percent} %",
        Done = done, Total = total, BytesDone = bytes, BytesTotal = 0,
        Paused = _paused, Attention = _attention,
        ReceiverRevision = previous.ReceiverRevision, ViewerRevision = previous.ViewerRevision,
        Jobs = previous.Jobs, PendingElements = previous.PendingElements, HasPendingChanges = previous.HasPendingChanges, Drawings = previous.Drawings, DrawingsPending = previous.DrawingsPending, DrawingsSupported = previous.DrawingsSupported,
        Access = previous.Access, Revoked = previous.Revoked, NoPermission = previous.NoPermission, RestorePoint = previous.RestorePoint,
        Comparison = previous.Comparison, Joined = previous.Joined, NeedsConfirmation = previous.NeedsConfirmation
      };
    }
    private Task Delay(int milliseconds) => Task.Delay(milliseconds, _cancel.Token);
    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024
      ? $"{bytes / (1024d * 1024 * 1024):0.00} GiB" : $"{bytes / (1024d * 1024):0.00} MiB";
    public void Dispose()
    { if (Interlocked.Exchange(ref _disposed, 1) != 0) return; _cancel.Cancel(); _ = _worker.ContinueWith(_ => _cancel.Dispose(), TaskScheduler.Default); }
  }
}
