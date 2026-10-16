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
        worker.Submit(Snapshot("old", true));
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
        worker.Submit(Snapshot("latest"));
        await Until(() => receiver.Scene.ContainsKey("latest"));
        Assert.False(receiver.Scene.ContainsKey("old"));
        Assert.Single(receiver.Uploads.Where(entry => entry.EndsWith(":0") && entry.StartsWith(receiver.FirstJob)));
        Assert.Collection(receiver.Committed, id => Assert.Equal(receiver.FirstJob, id), id => Assert.NotEqual(receiver.FirstJob, id));
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
        worker.Submit(Snapshot("old", true)); await receiver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        worker.HoldForReconciliation(); worker.Submit(Snapshot("current"), reconcile: true);
        worker.Pause(false); receiver.Release.TrySetResult(true);
        await Until(() => receiver.Scene.ContainsKey("current"));
        Assert.DoesNotContain(receiver.FirstJob, receiver.Committed);
        Assert.Single(receiver.Scene);
      }
      finally { worker.Dispose(); await worker.Completion; }
    }
    [Fact]
    public async Task PermanentErrorWaitsForManualRetryInsteadOfSpinning()
    {
      using var folder = new OutboxTests.TestFolder();
      var receiver = new FakeReceiver { Reject = true };
      var worker = new SyncWorker(folder.Path, receiver, new RecordingLog());
      try
      {
        worker.Submit(Snapshot("one")); await Until(() => worker.Progress.Attention);
        var attempts = receiver.Begins; await Task.Delay(400); Assert.Equal(attempts, receiver.Begins);
        receiver.Reject = false; worker.Retry(); await Until(() => receiver.Scene.ContainsKey("one"));
      }
      finally { worker.Dispose(); await worker.Completion; }
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
      public bool BlockFirst, Reject;
      public int Begins;
      public string FirstJob;
      public Task<ReceiverStatus> StatusAsync(CancellationToken token) => Task.FromResult(_status);
      public Task<RemoteJob> BeginAsync(SyncJob job, CancellationToken token)
      {
        Begins++; if (Reject) throw new ReceiverException(413);
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
        var packet = JObject.Parse(Encoding.UTF8.GetString(_parts[job].Values.SelectMany(part => part).ToArray()));
        if (packet["reset"].Value<bool>()) Scene.Clear();
        foreach (var id in packet["removed"]) Scene.TryRemove(id.ToString(), out _);
        foreach (var element in packet["updated"]) Scene[element["id"].ToString()] = element.ToString();
        var header = _jobs[job]; Committed.Enqueue(job);
        _status = new ReceiverStatus { RevisionId = job, ViewerRevision = job, SourceId = header.SourceId,
          SessionId = header.SessionId, SessionStartedAt = header.SessionStartedAt };
        return Task.CompletedTask;
      }
      public void Dispose() { }
    }
  }
}
