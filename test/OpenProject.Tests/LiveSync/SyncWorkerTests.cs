using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using OpenProject.Revit.LiveSync;
using OpenProject.Revit.LiveSync.Queue;
using OpenProject.Revit.LiveSync.Telemetry;
using OpenProject.Revit.LiveSync.Transport;
using Xunit;

namespace OpenProject.Tests.LiveSync
{
  public class SyncWorkerTests
  {
    private static LiveSnapshot Snapshot(string id, bool large = false) => new("model", "Model", "3D",
      new Dictionary<string, LiveElement> { [id] = new LiveElement { Id = id, ElementId = id,
        Name = id, Category = "Demo", Meshes = large ? new List<LiveMesh> { new() {
          Positions = new double[300000], Indices = new[] { 0, 1, 2 } } } : new() } });
    private static async Task Until(Func<bool> condition)
    {
      for (var i = 0; i < 120; i++) { if (condition()) return; await Task.Delay(50); }
      Assert.True(condition(), "No se alcanzó el estado esperado");
    }
    [Fact]
    public async Task PauseDuringFlightPersistsNewChangesAndRestartResumesOnlyMissingChunks()
    {
      using var folder = new OutboxTests.TestFolder();
      var receiver = new FakeReceiver { BlockFirst = true }; var log = new RecordingLog();
      var worker = new SyncWorker(folder.Path, receiver, log);
      try
      {
        worker.Submit(Snapshot("old", true)); worker.RequestPublish();
        await receiver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        worker.Pause(true); worker.Submit(Snapshot("latest")); receiver.Release.TrySetResult(true);
        await Until(() => worker.Progress.Paused && log.Codes.Count(code => code == "LOCAL_SAVED") >= 2);
        Assert.Single(receiver.Uploads); Assert.Empty(receiver.Committed);
      }
      finally { worker.Dispose(); await worker.Completion; }
      receiver.BlockFirst = false;
      worker = new SyncWorker(folder.Path, receiver, log);
      try
      {
        worker.Submit(Snapshot("latest")); worker.RequestPublish();
        await Until(() => receiver.Scene.ContainsKey("latest"));
        Assert.False(receiver.Scene.ContainsKey("old"));
        Assert.Single(receiver.Uploads.Where(entry => entry.EndsWith(":0") && entry.StartsWith(receiver.FirstJob)));
        Assert.Collection(receiver.Committed, id => Assert.Equal(receiver.FirstJob, id), id => Assert.NotEqual(receiver.FirstJob, id));
      }
      finally { worker.Dispose(); await worker.Completion; }
    }
    [Fact]
    public async Task ContentRejectedByTheCdeIsPreparedAgainOnTheNextSynchronize()
    {
      using var folder = new OutboxTests.TestFolder();
      var receiver = new FakeReceiver { RejectContent = 1 }; var log = new RecordingLog();
      var worker = new SyncWorker(folder.Path, receiver, log);
      try
      {
        worker.Submit(Snapshot("model")); worker.RequestPublish();
        await Until(() => log.Codes.Contains("SYNC_REJECTED"));
        var rejected = receiver.FirstJob;
        worker.RequestPublish();
        await Until(() => receiver.Committed.Count == 1);
        Assert.NotEqual(rejected, receiver.Committed.Single());
        Assert.True(receiver.Scene.ContainsKey("model"));
      }
      finally { worker.Dispose(); await worker.Completion; }
    }
    [Fact]
    public async Task StopAndReconcileDoNotCommitObsoleteInflightTarget()
    {
      using var folder = new OutboxTests.TestFolder();
      var receiver = new FakeReceiver { BlockFirst = true };
      var worker = new SyncWorker(folder.Path, receiver, new RecordingLog());
      try
      {
        worker.Submit(Snapshot("old", true)); worker.RequestPublish(); await receiver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        worker.HoldForReconciliation(); worker.Submit(Snapshot("current"), reconcile: true);
        worker.Pause(false); receiver.Release.TrySetResult(true);
        await Until(() => receiver.Scene.ContainsKey("current"));
        Assert.DoesNotContain(receiver.FirstJob, receiver.Committed);
        Assert.Single(receiver.Scene);
      }
      finally { worker.Dispose(); await worker.Completion; }
    }
    [Fact]
    public async Task ReopeningUnchangedModelClaimsANewPublisherSession()
    {
      using var folder = new OutboxTests.TestFolder();
      var receiver = new FakeReceiver();
      var first = new SyncWorker(folder.Path, receiver, new RecordingLog());
      first.Submit(Snapshot("unchanged")); first.RequestPublish();
      try { await Until(() => receiver.Committed.Count == 1); }
      finally { first.Dispose(); await first.Completion; }
      var previous = (await receiver.StatusAsync(CancellationToken.None)).SessionId;
      var second = new SyncWorker(folder.Path, receiver, new RecordingLog());
      second.Submit(Snapshot("unchanged")); second.RequestPublish();
      try
      {
        await Until(() => receiver.Committed.Count == 2);
        Assert.NotEqual(previous, (await receiver.StatusAsync(CancellationToken.None)).SessionId);
        Assert.Single(receiver.Scene);
      }
      finally { second.Dispose(); await second.Completion; }
    }
    [Fact]
    public async Task PermanentErrorWaitsForManualRetryInsteadOfSpinning()
    {
      using var folder = new OutboxTests.TestFolder();
      var receiver = new FakeReceiver { Reject = true };
      var worker = new SyncWorker(folder.Path, receiver, new RecordingLog());
      try
      {
        worker.Submit(Snapshot("one")); worker.RequestPublish(); await Until(() => worker.Progress.Attention);
        var attempts = receiver.Begins; await Task.Delay(400); Assert.Equal(attempts, receiver.Begins);
        receiver.Reject = false; worker.Retry(); await Until(() => receiver.Scene.ContainsKey("one"));
      }
      finally { worker.Dispose(); await worker.Completion; }
    }
    [Fact]
    public async Task WaitingPublisherKeepsLatestCaptureUntilExplicitRetry()
    {
      using var folder = new OutboxTests.TestFolder();
      var receiver = new FakeReceiver { Waiting = true };
      var worker = new SyncWorker(folder.Path, receiver, new RecordingLog());
      try
      {
        worker.Submit(Snapshot("old")); worker.RequestPublish();
        await Until(() => worker.Progress.Stage == "En espera de publicación");
        var begins = receiver.Begins;
        worker.Submit(Snapshot("latest")); receiver.Waiting = false;
        await Task.Delay(500);
        Assert.Equal(begins, receiver.Begins); Assert.Empty(receiver.Uploads); Assert.Empty(receiver.Committed);
        worker.Retry(); await Until(() => receiver.Scene.ContainsKey("latest"));
        Assert.False(receiver.Scene.ContainsKey("old")); Assert.Single(receiver.Committed);
      }
      finally { worker.Dispose(); await worker.Completion; }
    }
    [Fact]
    public async Task ReceivedRevisionCompletesWithoutViewerAndAllowsNextChange()
    {
      using var folder = new OutboxTests.TestFolder();
      var receiver = new FakeReceiver { DelayPresentation = true };
      var worker = new SyncWorker(folder.Path, receiver, new RecordingLog());
      try
      {
        worker.Submit(Snapshot("one")); worker.RequestPublish();
        await Until(() => worker.Progress.Stage == "Al día" && worker.Progress.ReceiverRevision != null);
        Assert.NotNull(worker.Progress.ReceiverRevision);
        Assert.Null(worker.Progress.ViewerRevision);
        worker.Submit(Snapshot("two")); worker.RequestPublish();
        await Until(() => worker.Progress.Stage == "Al día" && receiver.Scene.ContainsKey("two") && receiver.Committed.Count == 2);
        Assert.Null(worker.Progress.ViewerRevision);
        receiver.ApplyLatest();
        await Until(() => worker.Progress.Stage == "Al día" &&
          worker.Progress.ReceiverRevision == worker.Progress.ViewerRevision);
        Assert.Contains(worker.Progress.Jobs, job => job.State == "Revisión aplicada");
      }
      finally { worker.Dispose(); await worker.Completion; }
    }
    [Fact]
    public async Task ConnectingAndCapturingDoNotPublishUntilUserRequestsSynchronization()
    {
      using var folder = new OutboxTests.TestFolder();
      var receiver = new FakeReceiver();
      var worker = new SyncWorker(folder.Path, receiver, new RecordingLog());
      try
      {
        worker.Submit(Snapshot("local"));
        await Until(() => worker.Progress.HasPendingChanges && worker.Progress.Stage == "Listo para sincronizar");
        await Task.Delay(300);
        Assert.Equal(0, receiver.Begins);
        Assert.Empty(receiver.Uploads);
        Assert.Empty(receiver.Committed);
        Assert.False(worker.IsPublishRequested);
        worker.RequestPublish();
        await Until(() => receiver.Scene.ContainsKey("local"));
        // The receiver stores the scene before the worker completes the job, so wait for the release.
        await Until(() => !worker.IsPublishRequested);
      }
      finally { worker.Dispose(); await worker.Completion; }
    }
    [Fact]
    public async Task SynchronizeWaitsForTheCaptureItStartedBeforePublishing()
    {
      using var folder = new OutboxTests.TestFolder();
      var receiver = new FakeReceiver();
      var worker = new SyncWorker(folder.Path, receiver, new RecordingLog());
      try
      {
        worker.Submit(Snapshot("stale"));
        await Until(() => worker.Progress.HasPendingChanges);
        worker.RequestPublish(); worker.AwaitSnapshot();
        await Task.Delay(400);
        Assert.Equal(0, receiver.Begins);
        Assert.True(worker.IsAwaitingSnapshot);
        worker.Submit(Snapshot("fresh"));
        await Until(() => receiver.Scene.ContainsKey("fresh"));
        Assert.False(receiver.Scene.ContainsKey("stale"));
        Assert.False(worker.IsAwaitingSnapshot);
      }
      finally { worker.Dispose(); await worker.Completion; }
    }
    [Fact]
    public async Task FailedCaptureReleasesThePublicationRequest()
    {
      using var folder = new OutboxTests.TestFolder();
      var receiver = new FakeReceiver();
      var worker = new SyncWorker(folder.Path, receiver, new RecordingLog());
      try
      {
        worker.Submit(Snapshot("local"));
        await Until(() => worker.Progress.HasPendingChanges);
        worker.RequestPublish(); worker.AwaitSnapshot();
        worker.CancelPublish();
        Assert.False(worker.IsPublishRequested);
        Assert.False(worker.IsAwaitingSnapshot);
        await Task.Delay(300);
        Assert.Equal(0, receiver.Begins);
      }
      finally { worker.Dispose(); await worker.Completion; }
    }
    [Fact]
    public async Task LiveModePublishesAnEditCapturedWhileThePreviousOneWasSending()
    {
      using var folder = new OutboxTests.TestFolder();
      var receiver = new FakeReceiver { BlockFirst = true };
      var worker = new SyncWorker(folder.Path, receiver, new RecordingLog()) { Live = true };
      try
      {
        worker.Submit(Snapshot("first", true)); worker.RequestPublish();
        await receiver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        worker.Submit(Snapshot("edit"));
        receiver.Release.TrySetResult(true);
        // No RequestPublish: live mode alone must send the pending edit.
        await Until(() => receiver.Scene.ContainsKey("edit"));
        Assert.Equal(2, receiver.Committed.Count);
      }
      finally { worker.Dispose(); await worker.Completion; }
    }
    [Fact]
    public async Task ConnectingAnnouncesPresenceWithoutPublishing()
    {
      using var folder = new OutboxTests.TestFolder();
      var receiver = new PresenceReceiver();
      var worker = new SyncWorker(folder.Path, receiver, new RecordingLog());
      try
      {
        await Until(() => receiver.Announced != null);
        Assert.False(receiver.Paused);
        await Task.Delay(300);
        Assert.Equal(0, receiver.Begins);
      }
      finally { worker.Dispose(); await worker.Completion; }
    }
    private sealed class PresenceReceiver : ISyncReceiver, ISyncPresence
    {
      public volatile string Announced;
      public volatile bool Paused;
      public int Begins;
      public void Announce(string session) => Announced = session;
      public void SetPaused(bool paused) => Paused = paused;
      public Task<ReceiverStatus> StatusAsync(CancellationToken token) => Task.FromResult(new ReceiverStatus());
      public Task<RemoteJob> BeginAsync(SyncJob job, CancellationToken token) { Begins++; return Task.FromResult(new RemoteJob()); }
      public Task ChunkAsync(string job, int index, byte[] bytes, CancellationToken token) => Task.CompletedTask;
      public Task CommitAsync(string job, CancellationToken token) => Task.CompletedTask;
      public void Dispose() { }
    }
    private sealed class RecordingLog : ISyncTelemetrySink
    {
      public ConcurrentQueue<string> Codes { get; } = new();
      public void Emit(string stage, string code, string message, string jobId = null, long durationMs = 0, long bytes = 0, int elements = 0) => Codes.Enqueue(code);
    }
    private sealed class FakeReceiver : ISyncReceiver
    {
      private readonly Dictionary<string, SyncJob> _jobs = new();
      private readonly Dictionary<string, SortedDictionary<int, byte[]>> _parts = new();
      private ReceiverStatus _status = new();
      public ConcurrentDictionary<string, string> Scene { get; } = new();
      public ConcurrentQueue<string> Uploads { get; } = new();
      public ConcurrentQueue<string> Committed { get; } = new();
      public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
      public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
      public bool BlockFirst, Reject, Waiting, DelayPresentation;
      public int Begins, RejectContent;
      public string FirstJob;
      public Task<ReceiverStatus> StatusAsync(CancellationToken token) => Task.FromResult(_status);
      public Task<RemoteJob> BeginAsync(SyncJob job, CancellationToken token)
      {
        Begins++; if (Waiting) return Task.FromResult(new RemoteJob { Waiting = true, PublishingUser = "Otro usuario" });
        if (Reject) throw new ReceiverException(413);
        FirstJob ??= job.JobId;
        _jobs[job.JobId] = job;
        if (!_parts.ContainsKey(job.JobId)) _parts[job.JobId] = new();
        return Task.FromResult(new RemoteJob { Received = _parts[job.JobId].Keys.ToList(), Committed = Committed.Contains(job.JobId) });
      }
      public async Task ChunkAsync(string job, int index, byte[] bytes, CancellationToken token)
      {
        _parts[job][index] = bytes; Uploads.Enqueue(job + ":" + index);
        if (BlockFirst && Uploads.Count == 1) { Entered.TrySetResult(true); await Release.Task.WaitAsync(token); }
      }
      public Task CommitAsync(string job, CancellationToken token)
      {
        if (RejectContent > 0) { RejectContent--; throw new ReceiverException(422); }
        var header = _jobs[job]; Committed.Enqueue(job);
        if (header.Reset) Scene.Clear();
        foreach (var bytes in _parts[job].Values)
        {
          var part = JObject.Parse(Encoding.UTF8.GetString(bytes));
          foreach (var id in part["removed"] ?? new JArray()) Scene.TryRemove(id.ToString(), out _);
          foreach (var element in part["updated"] ?? new JArray()) Scene[element["id"].ToString()] = element.ToString();
        }
        _status = new ReceiverStatus { RevisionId = job, ViewerRevision = DelayPresentation ? null : job, SourceId = header.SourceId,
          SessionId = header.SessionId, SessionStartedAt = header.SessionStartedAt };
        return Task.CompletedTask;
      }
      public void ApplyLatest()
      {
        var previous = _status;
        _status = new ReceiverStatus { RevisionId = previous.RevisionId, ViewerRevision = previous.RevisionId,
          SourceId = previous.SourceId, SessionId = previous.SessionId, SessionStartedAt = previous.SessionStartedAt };
      }
      public void Dispose() { }
    }
  }
}
