using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenProject.Revit.LiveSync;
using OpenProject.Revit.LiveSync.Bim;
using OpenProject.Revit.LiveSync.Capture;
using OpenProject.Revit.LiveSync.Queue;
using OpenProject.Revit.LiveSync.Telemetry;
using OpenProject.Revit.LiveSync.Transport;
using Xunit;

namespace OpenProject.Tests.LiveSync
{
  /// <summary>Two people publishing to one real CDE model through the production transport (CP4).</summary>
  /// <remarks>Runs only when CMCOORDINATOR_LIVE_ADMIN_LINK/TOKEN and CMCOORDINATOR_LIVE_EDITOR_LINK/TOKEN point to two
  /// connections of an empty test model (Administración and Editor); the audit creates and removes them.</remarks>
  public class LiveMergeTests
  {
    private static LiveSnapshot Snapshot(params (string id, string name)[] elements)
    {
      var map = elements.ToDictionary(e => e.id, e => new LiveElement { Id = e.id, ElementId = e.id, Name = e.name, Category = "Muros",
        Bim = new BimElement { DocumentId = "cp4-doc", IfcClass = "IfcWall", Parameters = new List<BimParameter> {
          new() { Id = "p", Name = "Comentarios", Storage = "String", Value = e.name, HasValue = true } } } });
      return new LiveSnapshot(DocumentIdentityKey.Create("cp4-doc"), "Prueba CP4", "3D", map,
        new BimContext { ScopeId = "cp4-view", Documents = new List<BimDocument> { new() { Id = "cp4-doc", Title = "Prueba CP4" } } }, "cp4-doc");
    }
    private static Func<string> _state = () => "";
    private static async Task Until(Func<Task<bool>> condition, string what, int seconds = 90)
    {
      for (var i = 0; i < seconds * 2; i++) { if (await condition()) return; await Task.Delay(500); }
      Assert.True(await condition(), "No se alcanzó: " + what + " · " + _state());
    }
    private static string Describe(string who, SyncWorker worker, Log log) =>
      $"{who}: {worker.Progress.Stage} — {worker.Progress.Detail} [{string.Join(",", log.Codes)}] ";
    private sealed class Log : ISyncTelemetrySink
    {
      public ConcurrentQueue<string> Codes { get; } = new();
      public void Emit(string stage, string code, string message, string jobId = null, long durationMs = 0, long bytes = 0, int elements = 0) => Codes.Enqueue(code);
    }

    [Fact]
    public async Task TwoPeopleMergeOnTheRealCde()
    {
      string Env(string name) => Environment.GetEnvironmentVariable("CMCOORDINATOR_LIVE_" + name);
      if (string.IsNullOrEmpty(Env("ADMIN_TOKEN")) || string.IsNullOrEmpty(Env("EDITOR_TOKEN"))) return;
      // Like the panel: a pasted link is verified against the CDE before it is used.
      async Task<CdeDestination> Verified(string who)
      {
        var pasted = new CdeDestination(Env(who + "_LINK"), Env(who + "_TOKEN"));
        using var receiver = new SyncReceiver(destination: pasted);
        return pasted.Verified(await receiver.StatusAsync(CancellationToken.None));
      }
      var adminDestination = await Verified("ADMIN");
      var editorDestination = await Verified("EDITOR");
      // Two people, as two Revit sessions each signed in to the CDE with their own account.
      OpenProject.Revit.Entry.CdeAccessController.TestAccounts = (_, user) => user == adminDestination.UserId || user == editorDestination.UserId;
      using var probe = new SyncReceiver(destination: adminDestination);
      async Task<RemoteFingerprints> Prints() => await probe.FingerprintsAsync(CancellationToken.None);
      using var adminFolder = new OutboxTests.TestFolder();
      using var editorFolder = new OutboxTests.TestFolder();
      var adminLog = new Log(); var editorLog = new Log();
      var admin = new SyncWorker(adminFolder.Path, new SyncReceiver(destination: adminDestination), adminLog);
      _state = () => Describe("admin", admin, adminLog);
      try
      {
        // 1 · Administración creates the empty model with a complete capture.
        admin.Submit(Snapshot(("w1", "muro 1"), ("w2", "muro 2"))); admin.RequestPublish();
        await Until(async () => (await Prints()).ElementCount == 2 && !admin.Progress.HasPendingChanges, "creación del modelo");
        var created = await Prints();
        Assert.True(created.Complete);
        admin.Live = true; // Its Revit stays connected and live: that alone must not hold the turn.

        // 2 · Editor, another person and session, moves «w2» on the same file: merged, no complete capture, no waiting.
        RemoteFingerprints both = null;
        var editor = new SyncWorker(editorFolder.Path, new SyncReceiver(destination: editorDestination), editorLog);
        _state = () => Describe("admin", admin, adminLog) + Describe("editor", editor, editorLog);
        try
        {
          editor.Submit(Snapshot(("w1", "muro 1"), ("w2", "muro 2 movido"))); editor.RequestPublish();
          await Until(async () => (await Prints()).Fingerprints["w2"] != created.Fingerprints["w2"] && !editor.Progress.HasPendingChanges, "fusión del Editor");
          var merged = await Prints();
          Assert.Equal(2, merged.ElementCount);
          Assert.Equal(created.Fingerprints["w1"], merged.Fingerprints["w1"]);
          Assert.DoesNotContain("PUBLISHER_QUEUED", editorLog.Codes);
          Assert.False(editor.Progress.Attention);

          // 3 · Administración adds «w3»; its unchanged copy of «w2» does not undo the Editor's move.
          admin.Submit(Snapshot(("w1", "muro 1"), ("w2", "muro 2"), ("w3", "muro 3")));
          await Until(async () => (await Prints()).ElementCount == 3, "alta de Administración");
          Assert.Equal(merged.Fingerprints["w2"], (await Prints()).Fingerprints["w2"]);

          // 3b · Both send at once (UI-8 #10): the CDE applies them in order; nobody waits for a click or replaces.
          editor.Submit(Snapshot(("w1", "muro 1"), ("w2", "muro 2 movido"), ("e4", "muro del Editor"))); editor.RequestPublish();
          admin.Submit(Snapshot(("w1", "muro 1"), ("w2", "muro 2"), ("w3", "muro 3"), ("a4", "muro de Administración")));
          await Until(async () => (await Prints()).ElementCount == 5 && !admin.Progress.HasPendingChanges && !editor.Progress.HasPendingChanges, "envíos simultáneos");
          both = await Prints();
          Assert.Equal(merged.Fingerprints["w2"], both.Fingerprints["w2"]);
          Assert.True(both.Fingerprints.ContainsKey("w3") && both.Fingerprints.ContainsKey("a4") && both.Fingerprints.ContainsKey("e4"));
          Assert.False(admin.Progress.Attention || editor.Progress.Attention);

          // 4 · An Editor cannot replace the model.
          editor.Submit(Snapshot(("w1", "muro 1"))); editor.RequestReplace();
          await Until(() => Task.FromResult(editor.Progress.Attention), "rechazo del reemplazo");
          Assert.Equal(5, (await Prints()).ElementCount);
        }
        finally { editor.Dispose(); await editor.Completion; }

        // 5 · Administración replaces: only its capture remains.
        admin.Live = false;
        admin.Submit(Snapshot(("w1", "muro 1"))); admin.RequestReplace();
        await Until(async () => (await Prints()).ElementCount == 1, "reemplazo de Administración");

        // 6 · The replacement left a restore point (UI-8 #9); restoring brings back the 5 elements, huellas included.
        var replaced = await probe.StatusAsync(CancellationToken.None);
        if (replaced.Supports("restorePoint"))
        {
          Assert.Equal(5, replaced.RestorePoint?.ElementCount);
          Assert.Contains("restore", replaced.AllowedActions);
          await admin.RestoreAsync().WaitAsync(TimeSpan.FromSeconds(60));
          var restored = await Prints();
          Assert.Equal(5, restored.ElementCount);
          Assert.Equal(both.Fingerprints["w2"], restored.Fingerprints["w2"]);
          Assert.Null((await probe.StatusAsync(CancellationToken.None)).RestorePoint);
        }
      }
      finally { admin.Dispose(); await admin.Completion; OpenProject.Revit.Entry.CdeAccessController.TestAccounts = null; }
    }

    /// <summary>UI-8 #26–#28: a connection joins the model and stays live so the CDE audit can see «En vivo».</summary>
    /// <remarks>Runs only with CMCOORDINATOR_LIVE_HOLD_SECONDS; writes CMCOORDINATOR_LIVE_HOLD_MARKER once joined.</remarks>
    [Fact]
    public async Task AConnectionStaysLiveForTheCdeAudit()
    {
      string Env(string name) => Environment.GetEnvironmentVariable("CMCOORDINATOR_LIVE_" + name);
      if (!int.TryParse(Env("HOLD_SECONDS"), out var hold) || string.IsNullOrEmpty(Env("ADMIN_TOKEN"))) return;
      var pasted = new CdeDestination(Env("ADMIN_LINK"), Env("ADMIN_TOKEN"));
      ReceiverStatus status;
      using (var check = new SyncReceiver(destination: pasted)) status = await check.StatusAsync(CancellationToken.None);
      Assert.False(string.IsNullOrWhiteSpace(status.UserName), "El CDE debe enviar el nombre de la cuenta (UI-8 #27)");
      Assert.Equal(status.UserName, CdeModelAccess.From(status).UserName);
      var destination = pasted.Verified(status);
      OpenProject.Revit.Entry.CdeAccessController.TestAccounts = (_, user) => user == destination.UserId;
      using var folder = new OutboxTests.TestFolder();
      var log = new Log();
      var worker = new SyncWorker(folder.Path, new SyncReceiver(destination: destination), log);
      _state = () => Describe("vivo", worker, log);
      try
      {
        worker.Live = true;
        worker.Submit(Snapshot(("w1", "muro 1"), ("w2", "muro 2"))); worker.RequestPublish();
        await Until(() => Task.FromResult(worker.Progress.Joined && !worker.Progress.HasPendingChanges), "unirse al modelo");
        var marker = Env("HOLD_MARKER");
        if (!string.IsNullOrEmpty(marker)) System.IO.File.WriteAllText(marker, status.UserName);
        await Task.Delay(TimeSpan.FromSeconds(hold));
        Assert.False(worker.Progress.Attention, worker.Progress.Detail);
      }
      finally { worker.Dispose(); await worker.Completion; OpenProject.Revit.Entry.CdeAccessController.TestAccounts = null; }
    }

    /// <summary>UI-8 #7 and #11: the Editor is moved to Lectura, or its connection is revoked, by Administración.</summary>
    /// <remarks>CMCOORDINATOR_LIVE_PHASE = «lectura» or «revocada»; the audit changes the group or revokes before running it.</remarks>
    [Fact]
    public async Task TheEditorLosesItsGroupOrItsConnection()
    {
      string Env(string name) => Environment.GetEnvironmentVariable("CMCOORDINATOR_LIVE_" + name);
      var phase = Env("PHASE");
      if (string.IsNullOrEmpty(phase) || string.IsNullOrEmpty(Env("EDITOR_TOKEN"))) return;
      using var probe = new SyncReceiver(destination: new CdeDestination(Env("ADMIN_LINK"), Env("ADMIN_TOKEN")));
      var before = await probe.FingerprintsAsync(CancellationToken.None);
      var pasted = new CdeDestination(Env("EDITOR_LINK"), Env("EDITOR_TOKEN"));
      var destination = pasted;
      if (phase == "lectura") { using var check = new SyncReceiver(destination: pasted); destination = pasted.Verified(await check.StatusAsync(CancellationToken.None)); }
      OpenProject.Revit.Entry.CdeAccessController.TestAccounts = (_, user) => user == destination.UserId;
      using var folder = new OutboxTests.TestFolder();
      var log = new Log();
      var editor = new SyncWorker(folder.Path, new SyncReceiver(destination: destination), log);
      _state = () => Describe("editor", editor, log);
      try
      {
        editor.Submit(Snapshot(("w1", "muro 1 cambiado en Lectura"))); editor.RequestPublish();
        if (phase == "lectura")
        {
          await Until(() => Task.FromResult(editor.Progress.Stage == "Solo lectura"), "Solo lectura", 30);
          Assert.False(editor.Progress.Attention);
          Assert.True(editor.Progress.HasPendingChanges); // Kept on this computer for when the group changes.
          Assert.Equal(SyncWorker.ReadOnlyDetail, editor.Progress.Detail);
        }
        else
        {
          await Until(() => Task.FromResult(editor.Progress.Attention), "rechazo por revocación", 30);
          // The CDE answers 403 «Conexión sin permiso, revocada o vencida»; the panel then offers «Volver a conectar».
          Assert.True(editor.Progress.Revoked, editor.Progress.Detail);
          Assert.Equal(SyncWorker.RevokedDetail, editor.Progress.Detail);
        }
        await Task.Delay(1000);
        var after = await probe.FingerprintsAsync(CancellationToken.None);
        Assert.Equal(before.RevisionId, after.RevisionId); // What was published is kept.
      }
      finally { editor.Dispose(); await editor.Completion; OpenProject.Revit.Entry.CdeAccessController.TestAccounts = null; }
    }
  }
}
