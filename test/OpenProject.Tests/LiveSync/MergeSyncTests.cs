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
using OpenProject.Revit.LiveSync.Presentation;
using Xunit;

namespace OpenProject.Tests.LiveSync
{
  /// <summary>Several people publishing to one CDE model: merge, huellas, turn and replacement (CP4).</summary>
  public class MergeSyncTests
  {
    private static LiveSnapshot Snapshot(params (string id, string name)[] elements) => new("model", "Model", "3D",
      elements.ToDictionary(e => e.id, e => new LiveElement { Id = e.id, ElementId = e.id, Name = e.name, Category = "Demo", Meshes = new() }));
    private static async Task Until(Func<bool> condition, int seconds = 8)
    {
      for (var i = 0; i < seconds * 20; i++) { if (condition()) return; await Task.Delay(50); }
      Assert.True(condition(), "No se alcanzó el estado esperado");
    }
    private static async Task<SyncWorker> Publish(OutboxTests.TestFolder folder, MergeCde cde, string user, LiveSnapshot snapshot, RecordingLog log = null)
    {
      var worker = new SyncWorker(folder.Path, cde.For(user), log ?? new RecordingLog());
      var before = cde.Committed.Count;
      worker.Submit(snapshot); worker.RequestPublish();
      await Until(() => cde.Committed.Count > before && !worker.Progress.HasPendingChanges);
      return worker;
    }
    private static async Task Stop(SyncWorker worker) { worker.Dispose(); await worker.Completion; }
    // Connecting with this copy of the model: its first capture is the starting point (UI-8 logic 8).
    private static async Task<SyncWorker> Join(OutboxTests.TestFolder folder, MergeCde cde, string user, LiveSnapshot copy, RecordingLog log)
    {
      var worker = new SyncWorker(folder.Path, cde.For(user), log);
      worker.Submit(copy);
      await Until(() => log.Codes.Contains("BASELINE_RECORDED"));
      return worker;
    }

    [Fact]
    public async Task AnotherPersonsRevisionIsMergedWithoutRevertingTheirWork()
    {
      using var francisFolder = new OutboxTests.TestFolder();
      using var jenniferFolder = new OutboxTests.TestFolder();
      var cde = new MergeCde();
      var francis = await Publish(francisFolder, cde, "francis", Snapshot(("a", "muro"), ("b", "losa")));
      Assert.True(cde.LastJob.Reset); // The empty model is created with a complete capture.
      try
      {
        // jennifer starts from the same file and moves «b» after connecting: only that element travels, nothing is replaced.
        var jennifer = await Join(jenniferFolder, cde, "jennifer", Snapshot(("a", "muro"), ("b", "losa")), new RecordingLog());
        jennifer.Submit(Snapshot(("a", "muro"), ("b", "losa movida"))); jennifer.RequestPublish();
        await Until(() => cde.Committed.Count == 2 && !jennifer.Progress.HasPendingChanges);
        Assert.False(cde.LastJob.Reset);
        Assert.True(cde.LastJob.Merge);
        Assert.Equal(new[] { "b" }, cde.LastSent);
        await Stop(jennifer);
        // francis adds an element; his stale copy of «b» must not undo jennifer's move.
        francis.Submit(Snapshot(("a", "muro"), ("b", "losa"), ("c", "pilar"))); francis.RequestPublish();
        await Until(() => cde.Committed.Count == 3);
        Assert.Equal(new[] { "c" }, cde.LastSent);
        Assert.Contains("losa movida", cde.Scene["b"]);
        Assert.Equal(new[] { "a", "b", "c" }, cde.Scene.Keys.OrderBy(k => k));
      }
      finally { await Stop(francis); }
    }

    [Fact]
    public async Task OnlyElementsDeletedByThisPersonAreRemoved()
    {
      using var francisFolder = new OutboxTests.TestFolder();
      using var jenniferFolder = new OutboxTests.TestFolder();
      var cde = new MergeCde();
      await Stop(await Publish(francisFolder, cde, "francis", Snapshot(("a", "muro"), ("b", "losa"))));
      var jennifer = await Publish(jenniferFolder, cde, "jennifer", Snapshot(("a", "muro"), ("b", "losa"), ("j", "puerta")));
      try
      {
        // jennifer deletes her door and never had «z», which francis adds meanwhile.
        cde.PublishAs("francis", "z", "ventana");
        jennifer.Submit(Snapshot(("a", "muro"), ("b", "losa"))); jennifer.RequestPublish();
        await Until(() => cde.Committed.Count == 4);
        Assert.Equal(new[] { "j" }, cde.LastRemoved);
        Assert.True(cde.Scene.ContainsKey("z"));
      }
      finally { await Stop(jennifer); }
    }

    [Fact]
    public async Task SomeoneElsePublishingIsNoReasonToPublish()
    {
      using var folder = new OutboxTests.TestFolder();
      var cde = new MergeCde();
      var francis = await Publish(folder, cde, "francis", Snapshot(("a", "muro")));
      try
      {
        francis.Live = true;
        var begins = cde.Begins;
        cde.PublishAs("jennifer", "j", "puerta");
        await Task.Delay(800);
        Assert.Equal(begins, cde.Begins);
        Assert.Equal("Al día", francis.Progress.Stage);
      }
      finally { await Stop(francis); }
    }

    [Fact]
    public async Task ABusyTurnWaitsInLineAndLeavesWithoutAnotherClick()
    {
      using var francisFolder = new OutboxTests.TestFolder();
      using var jenniferFolder = new OutboxTests.TestFolder();
      var cde = new MergeCde();
      await Stop(await Publish(francisFolder, cde, "francis", Snapshot(("a", "muro"))));
      cde.TurnHeldBy = "Francis Vin";
      var log = new RecordingLog();
      var jennifer = new SyncWorker(jenniferFolder.Path, cde.For("jennifer"), log);
      try
      {
        jennifer.Submit(Snapshot(("a", "muro"), ("j", "puerta"))); jennifer.RequestPublish();
        await Until(() => jennifer.Progress.Stage == "En cola del CDE");
        Assert.False(jennifer.Progress.Attention);
        Assert.Contains("Francis Vin", jennifer.Progress.Detail);
        Assert.Equal(2, jennifer.Progress.QueuePosition);
        Assert.StartsWith("Posición 2 en la cola del CDE", jennifer.Progress.Detail);
        cde.TurnHeldBy = null;
        await Until(() => cde.Scene.ContainsKey("j"), 12);
        Assert.Contains("PUBLISHER_QUEUED", log.Codes);
      }
      finally { await Stop(jennifer); }
    }

    [Fact]
    public async Task ReplacingSendsTheCompleteCaptureAndIsRefusedToAnEditor()
    {
      using var francisFolder = new OutboxTests.TestFolder();
      using var jenniferFolder = new OutboxTests.TestFolder();
      var cde = new MergeCde();
      var francis = await Publish(francisFolder, cde, "francis", Snapshot(("a", "muro"), ("b", "losa")));
      await Stop(await Publish(jenniferFolder, cde, "jennifer", Snapshot(("a", "muro"), ("b", "losa"), ("j", "puerta"))));
      try
      {
        cde.Levels["francis"] = "admin";
        francis.Submit(Snapshot(("a", "muro"))); francis.RequestReplace();
        await Until(() => cde.Committed.Count == 3);
        Assert.True(cde.LastJob.Reset);
        Assert.Equal(new[] { "a" }, cde.Scene.Keys);
      }
      finally { await Stop(francis); }
      var editor = new SyncWorker(jenniferFolder.Path, cde.For("jennifer"), new RecordingLog());
      try
      {
        editor.Submit(Snapshot(("a", "muro"))); editor.RequestReplace();
        await Until(() => editor.Progress.Attention);
        Assert.Equal(3, cde.Committed.Count);
      }
      finally { await Stop(editor); }
    }

    [Fact]
    public async Task ARevokedConnectionKeepsItsQueueAndAsksForANewOne()
    {
      using var folder = new OutboxTests.TestFolder();
      var cde = new MergeCde();
      var log = new RecordingLog();
      var francis = await Publish(folder, cde, "francis", Snapshot(("a", "muro")), log);
      try
      {
        cde.Revoked = true;
        francis.Submit(Snapshot(("a", "muro"), ("b", "losa"))); francis.RequestPublish();
        await Until(() => francis.Progress.Revoked);
        Assert.Equal("Conexión revocada", francis.Progress.Stage);
        Assert.True(francis.Progress.Attention);
        Assert.True(francis.Progress.HasPendingChanges);
        Assert.Contains("CONNECTION_REVOKED", log.Codes);
        await Task.Delay(600);
        Assert.Equal("Conexión revocada", francis.Progress.Stage); // It stays: nothing is retried with a revoked token.
      }
      finally { await Stop(francis); }
    }

    // UI-8 logic 5: revoking while the synchronization is paused shows «Revocada» at once (the closed channel tells it).
    [Fact]
    public async Task ARevocationIsShownWhilePaused()
    {
      using var folder = new OutboxTests.TestFolder();
      var cde = new MergeCde();
      var log = new RecordingLog();
      var francis = await Publish(folder, cde, "francis", Snapshot(("a", "muro")), log);
      try
      {
        francis.Pause(true);
        await Until(() => francis.Progress.Paused);
        await Task.Delay(300);
        cde.Revoked = true;
        await Until(() => francis.Progress.Revoked, 3);
        Assert.Equal("Conexión revocada", francis.Progress.Stage);
        Assert.Contains("CONNECTION_REVOKED", log.Codes);
        Assert.Equal(CdeConnectionDisplayState.Revoked, SyncPanelViewModel.ResolveConnectionState(true, francis.Progress, false, true));
      }
      finally { await Stop(francis); }
    }

    // UI-8 logic 4: losing «Ver modelos IFC» is not a revocation: no new token is asked for and it resumes by itself.
    [Fact]
    public async Task LosingTheViewPermissionWaitsAndResumesByItself()
    {
      using var folder = new OutboxTests.TestFolder();
      var cde = new MergeCde();
      var log = new RecordingLog();
      var francis = await Publish(folder, cde, "francis", Snapshot(("a", "muro")), log);
      try
      {
        cde.NoPermission = true;
        francis.Submit(Snapshot(("a", "muro"), ("b", "losa"))); francis.RequestPublish();
        await Until(() => francis.Progress.NoPermission);
        Assert.False(francis.Progress.Revoked);
        Assert.Equal("Sin permiso", francis.Progress.Stage);
        Assert.Contains("NO_PERMISSION", log.Codes);
        cde.NoPermission = false;
        await Until(() => !francis.Progress.NoPermission, 14);
        Assert.Contains("PERMISSION_BACK", log.Codes);
        francis.RequestPublish();
        await Until(() => cde.Scene.ContainsKey("b"));
      }
      finally { await Stop(francis); }
    }

    // UI-8 logic 9: the BIM catalog travels with the first publication and again only when it changes.
    [Fact]
    public async Task TheCatalogTravelsOnlyWhenItChanges()
    {
      using var folder = new OutboxTests.TestFolder();
      var cde = new MergeCde { CatalogReuse = true };
      var francis = await Publish(folder, cde, "francis", Snapshot(("a", "muro")));
      try
      {
        Assert.Contains("catalog", cde.LastKinds);
        Assert.NotNull(cde.CatalogHash);
        cde.PublishAs("jennifer", "j", "puerta");
        francis.Submit(Snapshot(("a", "muro movido"))); francis.RequestPublish();
        await Until(() => cde.Committed.Count == 3 && !francis.Progress.HasPendingChanges);
        Assert.Equal(new[] { "a" }, cde.LastSent);
        Assert.DoesNotContain("catalog", cde.LastKinds);
        Assert.Equal(cde.CatalogHash, cde.LastJob.CatalogHash);
      }
      finally { await Stop(francis); }
    }

    [Fact]
    public async Task ThePreviewOfAReplacementCountsWhatItWouldChange()
    {
      using var francisFolder = new OutboxTests.TestFolder();
      using var jenniferFolder = new OutboxTests.TestFolder();
      var cde = new MergeCde();
      await Stop(await Publish(francisFolder, cde, "francis", Snapshot(("a", "muro"), ("b", "losa"), ("c", "pilar"))));
      cde.Levels["jennifer"] = "admin";
      var jennifer = new SyncWorker(jenniferFolder.Path, cde.For("jennifer"), new RecordingLog());
      try
      {
        // Her capture: «a» unchanged, «b» moved, «c» missing, «d» new.
        jennifer.Submit(Snapshot(("a", "muro"), ("b", "losa movida"), ("d", "puerta")));
        await Until(() => jennifer.Progress.Stage != "Preparando cola local");
        var preview = await jennifer.PreviewReplaceAsync().WaitAsync(TimeSpan.FromSeconds(8));
        Assert.True(preview.Compared && preview.Exact, $"Compared={preview.Compared} Exact={preview.Exact} remote={preview.RemoteCount} +{preview.Added} ~{preview.Changed} -{preview.Removed}");
        Assert.Equal(3, preview.RemoteCount);
        Assert.Equal((1, 1, 1), (preview.Added, preview.Changed, preview.Removed));
        Assert.Equal(1, cde.Committed.Count); // Previewing sends nothing.
      }
      finally { await Stop(jennifer); }
    }

    [Fact]
    public async Task ConnectingComparesWithTheCdeBeforeSendingAnything()
    {
      // UI-8 #24: the connection reads the huellas and compares them with the capture this Revit already has, sending nothing.
      using var francisFolder = new OutboxTests.TestFolder();
      using var jenniferFolder = new OutboxTests.TestFolder();
      using var kimFolder = new OutboxTests.TestFolder();
      var cde = new MergeCde();
      await Stop(await Publish(francisFolder, cde, "francis", Snapshot(("a", "muro"), ("b", "losa"), ("c", "pilar"))));
      async Task Capture(OutboxTests.TestFolder folder, string user, LiveSnapshot snapshot)
      {
        var earlier = new SyncWorker(folder.Path, cde.For(user), new RecordingLog());
        earlier.Submit(snapshot);
        await Until(() => earlier.Progress.Stage is "Listo para sincronizar" or "Al día");
        await Stop(earlier);
      }
      await Capture(jenniferFolder, "jennifer", Snapshot(("a", "muro"), ("b", "losa movida"), ("d", "puerta")));
      await Capture(kimFolder, "kim", Snapshot(("a", "muro"), ("b", "losa"), ("c", "pilar")));
      var log = new RecordingLog();
      var jennifer = new SyncWorker(jenniferFolder.Path, cde.For("jennifer"), log);
      var kim = new SyncWorker(kimFolder.Path, cde.For("kim"), new RecordingLog());
      try
      {
        await Until(() => jennifer.Progress.Comparison?.Compared == true && kim.Progress.Comparison?.Compared == true);
        var differs = jennifer.Progress.Comparison;
        Assert.Equal((3, 1, 1, 1, 1), (differs.Local, differs.Unchanged, differs.Added, differs.Changed, differs.Removed));
        Assert.False(differs.Matches);
        Assert.True(kim.Progress.Comparison.Matches);
        Assert.Equal(3, kim.Progress.Comparison.Unchanged);
        Assert.Contains("MODEL_COMPARED", log.Codes);
        Assert.Single(cde.Committed); // Connecting sends nothing.
        Assert.False(jennifer.Progress.Joined);
      }
      finally { await Stop(jennifer); await Stop(kim); }
    }

    // UI-8 logic 8 (replaces #29): an old copy that differs from the CDE in nearly everything sends nothing old and asks
    // nothing; what its person changes after connecting is sent.
    [Fact]
    public async Task AnOldCopySendsNothingOldAndAsksNothing()
    {
      using var francisFolder = new OutboxTests.TestFolder();
      using var jenniferFolder = new OutboxTests.TestFolder();
      var cde = new MergeCde();
      var many = Enumerable.Range(0, 60).Select(i => ("e" + i, "muro " + i)).ToArray();
      await Stop(await Publish(francisFolder, cde, "francis", Snapshot(many)));
      var log = new RecordingLog();
      var old = many.Select(e => (e.Item1, e.Item2 + " (otra versión)")).ToArray();
      var jennifer = await Join(jenniferFolder, cde, "jennifer", Snapshot(old), log);
      try
      {
        jennifer.Live = true;
        jennifer.RequestPublish();
        await Until(() => cde.Committed.Count == 2 && jennifer.Progress.Joined);
        Assert.False(jennifer.Progress.NeedsConfirmation);
        Assert.DoesNotContain("FULL_MERGE_HELD", log.Codes);
        Assert.Empty(cde.LastSent);
        Assert.All(cde.Scene, pair => Assert.DoesNotContain("otra versión", pair.Value));
        old[1] = ("e1", "muro 1 movido por jennifer");
        jennifer.Submit(Snapshot(old));
        await Until(() => cde.Committed.Count == 3);
        Assert.Equal(new[] { "e1" }, cde.LastSent);
        Assert.Equal("muro 1 movido por jennifer", cde.Scene["e1"]);
        Assert.Equal("muro 2", cde.Scene["e2"]);
      }
      finally { await Stop(jennifer); }
    }

    // UI-8 logic 8, the case of 2026-10-29: francis changes a wall and removes a column; jennifer connects with an old copy.
    [Fact]
    public async Task AnOldCopyNeitherRevertsNorBringsBackWhatOthersDid()
    {
      using var francisFolder = new OutboxTests.TestFolder();
      using var jenniferFolder = new OutboxTests.TestFolder();
      var cde = new MergeCde();
      await Stop(await Publish(francisFolder, cde, "francis", Snapshot(("a", "muro"), ("b", "losa"), ("c", "pilar"))));
      cde.PublishAs("francis", "a", "muro de francis");
      cde.RemoveAs("francis", "c");
      var log = new RecordingLog();
      var jennifer = await Join(jenniferFolder, cde, "jennifer", Snapshot(("a", "muro"), ("b", "losa"), ("c", "pilar"), ("n", "ventana nueva")), log);
      try
      {
        jennifer.RequestPublish();
        await Until(() => cde.Committed.Count == 4 && !jennifer.Progress.HasPendingChanges);
        Assert.Equal(new[] { "n" }, cde.LastSent);            // her new window enters
        Assert.Equal("muro de francis", cde.Scene["a"]);        // francis's wall is not reverted
        Assert.False(cde.Scene.ContainsKey("c"));               // the column he removed does not come back
        jennifer.Submit(Snapshot(("a", "muro"), ("b", "losa movida"), ("c", "pilar"), ("n", "ventana nueva"))); jennifer.RequestPublish();
        await Until(() => cde.Committed.Count == 5 && !jennifer.Progress.HasPendingChanges);
        Assert.Equal(new[] { "b" }, cde.LastSent);             // what she changes after connecting enters
        jennifer.Submit(Snapshot(("a", "muro"), ("b", "losa movida"), ("c", "pilar movido"), ("n", "ventana nueva"))); jennifer.RequestPublish();
        await Until(() => cde.Committed.Count == 6 && !jennifer.Progress.HasPendingChanges);
        Assert.Equal(new[] { "c" }, cde.LastSent);             // changing the removed column brings it back, on purpose
        Assert.Equal("muro de francis", cde.Scene["a"]);
      }
      finally { await Stop(jennifer); }
    }

    private sealed class RecordingLog : ISyncTelemetrySink
    {
      public ConcurrentQueue<string> Codes { get; } = new();
      public void Emit(string stage, string code, string message, string jobId = null, long durationMs = 0, long bytes = 0, int elements = 0) => Codes.Enqueue(code);
    }

    /// <summary>In-memory CDE with the receiver rules of CP4; each person talks to it through their own connection.</summary>
    private sealed class MergeCde
    {
      private readonly object _gate = new();
      private readonly Dictionary<string, SyncJob> _jobs = new();
      private readonly Dictionary<string, SortedDictionary<int, byte[]>> _parts = new();
      private readonly Dictionary<string, string> _prints = new();
      public ConcurrentDictionary<string, string> Scene { get; } = new();
      public ConcurrentQueue<string> Committed { get; } = new();
      public Dictionary<string, string> Levels { get; } = new();
      public string Revision, TurnHeldBy;
      public bool Revoked, NoPermission, CatalogReuse;
      public string CatalogHash;
      public string[] LastKinds = Array.Empty<string>();
      public int Begins;
      public SyncJob LastJob;
      public string[] LastSent = Array.Empty<string>(), LastRemoved = Array.Empty<string>();
      public Connection For(string user) => new(this, user);
      // Another person's publication arriving through their own Cmcoordinator.
      public void PublishAs(string user, string id, string name)
      {
        lock (_gate)
        {
          Scene[id] = name; _prints[id] = "otra:" + name; _gone.Remove(id);
          Revision = Guid.NewGuid().ToString("N"); Committed.Enqueue(Revision);
        }
      }
      // Another person deleting an element: the CDE keeps it among the removed ids (UI-8 logic 8).
      public void RemoveAs(string user, string id)
      {
        lock (_gate)
        {
          Scene.TryRemove(id, out _); _prints.Remove(id); _gone.Add(id);
          Revision = Guid.NewGuid().ToString("N"); Committed.Enqueue(Revision);
        }
      }
      private readonly HashSet<string> _gone = new();
      public sealed class Connection : ISyncReceiver, ISyncFingerprints, ISyncRejection
      {
        // The CDE closes the real-time channel of a revoked connection (PublisherChannel#deny).
        public bool ChannelRejected => _cde.Revoked;
        private readonly MergeCde _cde; private readonly string _user;
        public Connection(MergeCde cde, string user) { _cde = cde; _user = user; }
        private string Level => _cde.Levels.TryGetValue(_user, out var level) ? level : "editor";
        public Task<ReceiverStatus> StatusAsync(CancellationToken token)
        {
          lock (_cde._gate)
          {
            if (_cde.Revoked) throw new ReceiverException(403, 0, "Conexión sin permiso, revocada o vencida");
            if (_cde.NoPermission) throw new ReceiverException(403, 0, ReceiverException.NoPermissionReason + ": pídeselo a quien administre el proyecto");
            return Task.FromResult(new ReceiverStatus { Application = "cmcoordinator-cde", Protocols = new[] { 3 }, Count = _cde.Scene.Count,
              Features = _cde.CatalogReuse ? new[] { SyncWorker.MergeFeature, SyncWorker.CatalogReuseFeature } : new[] { SyncWorker.MergeFeature },
              CatalogHash = _cde.CatalogHash, RevisionId = _cde.Revision, AccessLevel = Level,
              AllowedActions = _cde.Revision == null ? new[] { "create" } : Level == "admin" ? new[] { "sync", "replace" } : new[] { "sync" },
              ModelExists = _cde.Revision != null });
          }
        }
        public Task<RemoteFingerprints> FingerprintsAsync(CancellationToken token)
        {
          lock (_cde._gate)
            return Task.FromResult(new RemoteFingerprints { RevisionId = _cde.Revision, Fingerprints = new(_cde._prints), ElementCount = _cde.Scene.Count,
              Complete = _cde.Scene.Keys.All(_cde._prints.ContainsKey), Removed = _cde._gone.ToList() });
        }
        public Task<RemoteJob> BeginAsync(SyncJob job, CancellationToken token)
        {
          lock (_cde._gate)
          {
            _cde.Begins++;
            if (_cde.Revoked) throw new ReceiverException(403, 0, "Conexión sin permiso, revocada o vencida");
            if (_cde.NoPermission) throw new ReceiverException(403, 0, ReceiverException.NoPermissionReason + ": pídeselo a quien administre el proyecto");
            if (_cde.TurnHeldBy != null) return Task.FromResult(new RemoteJob { Waiting = true, PublishingUser = _cde.TurnHeldBy, Position = 2 });
            if (job.Reset && _cde.Revision != null && Level != "admin")
              throw new ReceiverException(403, 0, "Solo Administración del modelo puede reemplazarlo. Sincroniza para fusionar tus cambios.");
            if (job.Merge != true && !job.Reset && job.BaseRevision != _cde.Revision) throw new ReceiverException(409);
            _cde._jobs[job.JobId] = job;
            if (!_cde._parts.ContainsKey(job.JobId)) _cde._parts[job.JobId] = new();
            return Task.FromResult(new RemoteJob { Received = _cde._parts[job.JobId].Keys.ToList() });
          }
        }
        public Task ChunkAsync(string job, int index, byte[] bytes, CancellationToken token)
        { lock (_cde._gate) _cde._parts[job][index] = bytes; return Task.CompletedTask; }
        public Task CommitAsync(string job, CancellationToken token)
        {
          lock (_cde._gate)
          {
            var header = _cde._jobs[job];
            if (header.Reset) { _cde.Scene.Clear(); _cde._prints.Clear(); _cde._gone.Clear(); }
            var sent = new List<string>(); var removed = new List<string>(); var kinds = new List<string>();
            foreach (var bytes in _cde._parts[job].Values)
            {
              var part = JObject.Parse(Encoding.UTF8.GetString(bytes));
              kinds.Add((string)part["kind"]);
              if ((string)part["kind"] == "catalog") _cde.CatalogHash = header.CatalogHash;
              foreach (var id in part["removed"] ?? new JArray()) { _cde.Scene.TryRemove(id.ToString(), out _); _cde._prints.Remove(id.ToString()); _cde._gone.Add(id.ToString()); removed.Add(id.ToString()); }
              foreach (var element in part["updated"] ?? new JArray())
              {
                var id = element["id"].ToString(); sent.Add(id); _cde._gone.Remove(id);
                _cde.Scene[id] = (string)element["name"];
                var print = (string)part["fingerprints"]?[id];
                if (print != null) _cde._prints[id] = print; else _cde._prints.Remove(id);
              }
            }
            _cde.LastKinds = kinds.ToArray();
            _cde.LastJob = header; _cde.LastSent = sent.OrderBy(i => i).ToArray(); _cde.LastRemoved = removed.OrderBy(i => i).ToArray();
            _cde.Revision = job; _cde.Committed.Enqueue(job);
          }
          return Task.CompletedTask;
        }
        public void Dispose() { }
      }
    }
  }
}
