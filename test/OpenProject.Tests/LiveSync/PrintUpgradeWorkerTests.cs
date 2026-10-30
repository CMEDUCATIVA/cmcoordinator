using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using OpenProject.Revit.LiveSync;
using OpenProject.Revit.LiveSync.Presentation;
using OpenProject.Revit.LiveSync.Queue;
using OpenProject.Revit.LiveSync.Telemetry;
using OpenProject.Revit.LiveSync.Transport;
using Xunit;

namespace OpenProject.Tests.LiveSync
{
  /// <summary>Contracts 1 and 4 through the worker: huellas upgraded on the way, and no job when there is nothing to send.</summary>
  public class PrintUpgradeWorkerTests
  {
    private static LiveSnapshot Snapshot(params LiveElement[] elements) => new("model", "Model", "3D", elements.ToDictionary(e => e.Id, e => e));
    private static LiveElement Wall(string id, double x = 0) => new()
    {
      Id = id, ElementId = id, Name = "Muro", Category = "Muros",
      Meshes = new() { new LiveMesh { Positions = new[] { x, 0, 0, x + 1, 0, 0, x, 1, 0 }, Indices = new[] { 0, 1, 2 } } }
    };
    // The same wall as another Revit year captures it.
    private static LiveElement OtherYear(string id) => new()
    {
      Id = id, ElementId = id, Name = "Muro", Category = "Muros",
      Meshes = new() { new LiveMesh { Positions = new double[] { 0, 1, 0, 0, 0, 0, 1, 0, 0 }, Indices = new[] { 1, 2, 0 } } }
    };
    private static string Exact(LiveElement element) => SyncEncoding.Hash(SyncEncoding.Json(element)).Substring(0, SqliteOutbox.FingerprintLength);
    private static async Task Until(Func<bool> condition, string what = "No se alcanzó el estado esperado")
    {
      for (var i = 0; i < 200; i++) { if (condition()) return; await Task.Delay(50); }
      Assert.True(condition(), what);
    }

    private sealed class Log : ISyncTelemetrySink
    {
      public readonly System.Collections.Concurrent.ConcurrentQueue<string> Codes = new();
      public void Emit(string area, string code, string message, string jobId = null, long durationMs = 0, long bytes = 0, int elements = 0) => Codes.Enqueue(code);
    }

    /// <summary>A CDE with versions, whose huellas live in memory. Each connection publishes through it.</summary>
    private sealed class Cde : ISyncReceiver, ISyncFingerprints, ISyncPrintUpgrade
    {
      public readonly Dictionary<string, string> Prints = new();
      public readonly Dictionary<string, long?[]> Versions = new();
      public readonly List<PrintUpgrade> Upgrades = new();
      public readonly List<SyncJob> Jobs = new();
      public readonly Dictionary<string, List<JObject>> Parts = new();
      public string Revision = "cde-1", Catalog = SyncEncoding.Hash("null");
      public bool Announce = true; public Exception UpgradeError; public int Begins;
      public Task<ReceiverStatus> StatusAsync(CancellationToken token) => Task.FromResult(new ReceiverStatus
      {
        Application = "cmcoordinator-cde", Protocols = new[] { 3 }, Count = Prints.Count, RevisionId = Revision, SourceId = "model", AccessLevel = "editor", AllowedActions = new[] { "sync" }, ModelExists = true, CatalogHash = Catalog,
        Features = Announce ? new[] { SyncWorker.MergeFeature, SyncWorker.CatalogReuseFeature, SyncWorker.ElementVersionsFeature, SyncWorker.PrintUpgradeFeature }
          : new[] { SyncWorker.MergeFeature, SyncWorker.CatalogReuseFeature, SyncWorker.ElementVersionsFeature }
      });
      public Task<RemoteFingerprints> FingerprintsAsync(CancellationToken token) => Task.FromResult(new RemoteFingerprints
      { Fingerprints = new(Prints), Versions = new(Versions), Complete = true, Authors = new() { ["7"] = "Juan" } });
      public Task<int> UpgradePrintsAsync(IReadOnlyDictionary<string, PrintUpgrade> upgrades, CancellationToken token)
      {
        if (UpgradeError != null) throw UpgradeError;
        foreach (var pair in upgrades) { Upgrades.Add(pair.Value); if (Prints[pair.Key] == pair.Value.Old) Prints[pair.Key] = pair.Value.New; }
        return Task.FromResult(upgrades.Count);
      }
      public Task<RemoteJob> BeginAsync(SyncJob job, CancellationToken token)
      { Begins++; Jobs.Add(job); Parts[job.JobId] = new(); return Task.FromResult(new RemoteJob()); }
      public Task ChunkAsync(string jobId, int index, byte[] bytes, CancellationToken token)
      { Parts[jobId].Add(JObject.Parse(Encoding.UTF8.GetString(bytes))); return Task.CompletedTask; }
      public Task CommitAsync(string jobId, CancellationToken token) { Revision = jobId; return Task.CompletedTask; }
      public void Dispose() { }
    }

    [Fact]
    public async Task AnAnnouncedCdeGetsTheOldHuellasAndNothingIsSent()
    {
      using var folder = new OutboxTests.TestFolder();
      var wall = Wall("W1");
      var cde = new Cde();
      cde.Prints["W1"] = Exact(wall); cde.Versions["W1"] = new long?[] { 3, 3, 7 };
      var log = new Log();
      var worker = new SyncWorker(folder.Path, cde, log);
      try
      {
        worker.Submit(Snapshot(wall));
        worker.RequestPublish();
        await Until(() => cde.Upgrades.Count > 0 && !worker.IsPublishRequested);
        Assert.Equal(new[] { Exact(wall) }, cde.Upgrades.Select(u => u.Old).Distinct());
        Assert.Equal(SqliteOutbox.SemanticPrint(SyncEncoding.Json(wall)), cde.Upgrades.First().New);
        Assert.Equal(SqliteOutbox.SemanticPrint(SyncEncoding.Json(wall)), cde.Prints["W1"]);
        Assert.Contains("PRINTS_UPGRADED", log.Codes);
      }
      finally { worker.Dispose(); await worker.Completion; }
    }

    [Fact]
    public async Task WithoutTheFeatureNothingIsAskedOfTheCde()
    {
      using var folder = new OutboxTests.TestFolder();
      var wall = Wall("W1");
      var cde = new Cde { Announce = false };
      cde.Prints["W1"] = Exact(wall); cde.Versions["W1"] = new long?[] { 3, 3, 7 };
      var log = new Log();
      var worker = new SyncWorker(folder.Path, cde, log);
      try
      {
        worker.Submit(Snapshot(wall));
        worker.RequestPublish();
        await Until(() => log.Codes.Contains("NOTHING_TO_SEND"));
        Assert.Empty(cde.Upgrades);
        Assert.Equal(Exact(wall), cde.Prints["W1"]);
        Assert.DoesNotContain("PRINT_UPGRADE_FAILED", log.Codes);
      }
      finally { worker.Dispose(); await worker.Completion; }
    }

    [Fact]
    public async Task AFailedUpgradeDoesNotStopTheSynchronization()
    {
      using var folder = new OutboxTests.TestFolder();
      var (wall, moved) = (Wall("W1"), Wall("W2"));
      var cde = new Cde { UpgradeError = new ReceiverException(500) };
      cde.Prints["W1"] = Exact(wall); cde.Versions["W1"] = new long?[] { 3, 3, 7 };
      cde.Prints["W2"] = Exact(moved); cde.Versions["W2"] = new long?[] { 3, 3, 7 };
      var log = new Log();
      var worker = new SyncWorker(folder.Path, cde, log);
      try
      {
        worker.Submit(Snapshot(wall, moved));
        await Until(() => log.Codes.Contains("BASELINE_RECORDED"));
        worker.Submit(Snapshot(wall, Wall("W2", x: 4)));
        worker.RequestPublish();
        await Until(() => cde.Revision != "cde-1");
        Assert.Contains("PRINT_UPGRADE_FAILED", log.Codes);
        Assert.False(worker.Progress.Attention);
        var updated = cde.Parts[cde.Revision].Single(p => (string)p["kind"] == "elements")["updated"];
        Assert.Equal("W2", (string)updated.Single()["id"]);
        Assert.Equal(3, (long)cde.Parts[cde.Revision].Single(p => (string)p["kind"] == "elements")["versions"]["W2"]["bg"]);
      }
      finally { worker.Dispose(); await worker.Completion; }
    }

    [Fact]
    public async Task OtherRevitYearStartsFromTheCdeVersionOnceTheHuellaIsUpgraded()
    {
      using var folder2026 = new OutboxTests.TestFolder();
      using var folder2027 = new OutboxTests.TestFolder();
      var (wall, sameWall) = (Wall("W1"), OtherYear("W1"));
      var cde = new Cde();
      cde.Prints["W1"] = Exact(wall); cde.Versions["W1"] = new long?[] { 35, 15, 7 };
      // Revit 2027 connects first: its copy does not match the old huella and the change would not enter.
      var log2027 = new Log();
      var y2027 = new SyncWorker(folder2027.Path, cde, log2027);
      try
      {
        y2027.Submit(Snapshot(sameWall));
        await Until(() => log2027.Codes.Contains("LOCAL_SAVED"));
        y2027.RequestPublish();
        await Until(() => log2027.Codes.Contains("NOTHING_TO_SEND"));
      }
      finally { y2027.Dispose(); await y2027.Completion; }
      using (var box = new SqliteOutbox(folder2027.Path)) Assert.True(box.HasOldCopies);
      // Revit 2026 recognizes it and upgrades it.
      var y2026 = new SyncWorker(folder2026.Path, cde, new Log());
      try
      {
        y2026.Submit(Snapshot(wall)); y2026.RequestPublish();
        await Until(() => cde.Upgrades.Count > 0);
      }
      finally { y2026.Dispose(); await y2026.Completion; }
      // Revit 2027 comes back and moves the wall: the change starts from the CDE's version and enters.
      log2027 = new Log();
      y2027 = new SyncWorker(folder2027.Path, cde, log2027);
      try
      {
        y2027.Submit(Snapshot(Wall("W1", x: 7)));
        y2027.RequestPublish();
        await Until(() => cde.Begins > 0);
        var part = cde.Parts[cde.Jobs.Last().JobId];
        await Until(() => part.Any(p => (string)p["kind"] == "elements"));
        Assert.Equal(35, (long)part.Single(p => (string)p["kind"] == "elements")["versions"]["W1"]["bg"]);
      }
      finally { y2027.Dispose(); await y2027.Completion; }
    }

    [Fact]
    public async Task NothingToSendLeavesThePersonInTheModelAndWithoutWaiting()
    {
      using var folder = new OutboxTests.TestFolder();
      var wall = Wall("W1");
      var cde = new Cde();
      cde.Prints["W1"] = SqliteOutbox.SemanticPrint(SyncEncoding.Json(wall)); cde.Versions["W1"] = new long?[] { 3, 3, 7 };
      var log = new Log();
      var worker = new SyncWorker(folder.Path, cde, log);
      try
      {
        worker.Submit(Snapshot(wall));
        worker.RequestPublish();
        await Until(() => log.Codes.Contains("NOTHING_TO_SEND") && !worker.IsPublishRequested && worker.Progress.Stage == "Al día");
        Assert.Equal(0, cde.Begins);
        Assert.True(worker.Progress.Joined);
        Assert.False(worker.Progress.HasPendingChanges);
        Assert.Equal(0, worker.Progress.PendingElements);
        // In live mode it does not ask again and again.
        worker.Live = true;
        await Task.Delay(500);
        Assert.Equal(0, cde.Begins);
        Assert.Single(log.Codes, code => code == "NOTHING_TO_SEND");
      }
      finally { worker.Dispose(); await worker.Completion; }
    }

    [Fact]
    public async Task AnEditThatWouldNotEnterIsListedWithoutAJobAndLeavesWhenUndone()
    {
      using var folder = new OutboxTests.TestFolder();
      var wall = Wall("W1");
      var cde = new Cde();
      cde.Prints["W1"] = Exact(Wall("W1", x: 9)); cde.Versions["W1"] = new long?[] { 5, 5, 7, null, null, 4 };
      var log = new Log();
      var worker = new SyncWorker(folder.Path, cde, log);
      try
      {
        worker.Submit(Snapshot(wall));
        await Until(() => log.Codes.Contains("BASELINE_RECORDED"));
        worker.Submit(Snapshot(Wall("W1", x: 3)));
        worker.RequestPublish();
        await Until(() => log.Codes.Contains("NOTHING_TO_SEND") && worker.Progress.Stage == "Al día" && worker.Progress.Differs.Count == 1);
        Assert.Equal(0, cde.Begins);
        Assert.Equal("Al día · 1 fuera", DifferDetail.QueueState(worker.Progress));
        Assert.Equal("Juan", worker.Progress.Differs[0].By);
        Assert.Equal(4, worker.Progress.Differs[0].Theirs);
        worker.Submit(Snapshot(Wall("W1")));
        await Until(() => worker.Progress.Differs.Count == 0);
        Assert.Equal("Al día", DifferDetail.QueueState(new SyncProgress { Differs = worker.Progress.Differs }));
      }
      finally { worker.Dispose(); await worker.Completion; }
    }

    [Fact]
    public async Task ANewCatalogStillMakesAJob()
    {
      using var folder = new OutboxTests.TestFolder();
      var wall = Wall("W1");
      var cde = new Cde { Catalog = "otro" };
      cde.Prints["W1"] = SqliteOutbox.SemanticPrint(SyncEncoding.Json(wall)); cde.Versions["W1"] = new long?[] { 3, 3, 7 };
      var worker = new SyncWorker(folder.Path, cde, new Log());
      try
      {
        worker.Submit(Snapshot(wall));
        worker.RequestPublish();
        await Until(() => cde.Revision != "cde-1");
        Assert.Equal("catalog", (string)cde.Parts[cde.Revision].Single()["kind"]);
      }
      finally { worker.Dispose(); await worker.Completion; }
    }

  }
}
