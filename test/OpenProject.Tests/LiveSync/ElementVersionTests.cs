using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using OpenProject.Revit.LiveSync;
using OpenProject.Revit.LiveSync.Queue;
using OpenProject.Revit.LiveSync.Transport;
using Xunit;

namespace OpenProject.Tests.LiveSync
{
  /// <summary>UI-8 logic 8, second part: per-element versions in the queue (starting version, aspects, stale, forced).</summary>
  public class ElementVersionTests
  {
    private static LiveElement Element(string id, double x = 0, string name = "Muro") => new()
    {
      Id = id, ElementId = id, Name = name, Category = "Muros",
      Meshes = new() { new LiveMesh { Positions = new[] { x, 0, 0, x + 1, 0, 0, x, 1, 0 }, Indices = new[] { 0, 1, 2 } } }
    };
    private static LiveSnapshot Snapshot(params LiveElement[] elements) => new("model", "Model", "3D", elements.ToDictionary(e => e.Id, e => e));
    private static string Print(LiveElement element) => SyncEncoding.Hash(SyncEncoding.Json(element)).Substring(0, SqliteOutbox.FingerprintLength);
    private static RemoteFingerprints Remote(Dictionary<string, (string Print, long G, long P, long? By)> elements) => new()
    {
      Fingerprints = elements.ToDictionary(e => e.Key, e => e.Value.Print),
      Versions = elements.ToDictionary(e => e.Key, e => new long?[] { e.Value.G, e.Value.P, e.Value.By }),
      Authors = new Dictionary<string, string> { ["7"] = "Juan" }
    };
    private static SqliteOutbox.VersionPlan Plan(RemoteFingerprints remote, bool admin = false) =>
      new() { Remote = remote.Versions, Authors = remote.Authors, CanForce = admin };
    private static SyncJob Prepare(SqliteOutbox outbox, RemoteFingerprints remote, bool admin = false) =>
      outbox.Prepare("s", 1, "rev", false, merge: true, remote: remote.Fingerprints, gone: new List<string>(), versions: Plan(remote, admin));
    private static List<JObject> Parts(SqliteOutbox outbox, SyncJob job) =>
      Enumerable.Range(0, job.ChunkCount).Select(i => JObject.Parse(Encoding.UTF8.GetString(outbox.Chunk(i)))).ToList();
    private static JObject ElementsPart(SqliteOutbox outbox, SyncJob job) => Parts(outbox, job).FirstOrDefault(p => (string)p["kind"] == "elements");

    // A10: Revit 2027 triangulates the same element differently, renames a parameter, adds another and adds decimals.
    private static string Captured(string typeName, string extra, double[] positions, int[] indices, string decimals) =>
      "{\"id\":\"W\",\"name\":\"Muro\",\"category\":\"Muros\",\"properties\":{\"Largo\":\"" + decimals + " m\"," + typeName + ":\"3.7021 W\"" + extra +
      "},\"meshes\":[{\"positions\":" + Newtonsoft.Json.JsonConvert.SerializeObject(positions) + ",\"indices\":" + Newtonsoft.Json.JsonConvert.SerializeObject(indices) + ",\"color\":\"#fff\",\"opacity\":1.0}]}";

    [Fact]
    public void TheSameElementInAnotherRevitYearHasTheSameHuella()
    {
      var y2026 = Captured("\"Tipo · U\"", "", new double[] { 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0 }, new[] { 0, 1, 2, 0, 2, 3 }, "1763.5639755188622");
      var y2027 = Captured("\"Tipo · Transmitancia\"", ",\"Alojado\":\"No\"", new double[] { 1, 1, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0 }, new[] { 2, 3, 0, 2, 0, 1 }, "1763.5639755188654");
      Assert.Equal(SqliteOutbox.SemanticPrint(y2026), SqliteOutbox.SemanticPrint(y2027));
      Assert.NotEqual(SqliteOutbox.ElementHash(y2026), SqliteOutbox.ElementHash(y2027));
    }

    [Fact]
    public void TheHuellaSeesAMoveACommentAndAColour()
    {
      var original = Captured("\"U\"", "", new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, new[] { 0, 1, 2 }, "1");
      Assert.NotEqual(SqliteOutbox.SemanticPrint(original), SqliteOutbox.SemanticPrint(Captured("\"U\"", "", new double[] { 0.002, 0, 0, 1.002, 0, 0, 0.002, 1, 0 }, new[] { 0, 1, 2 }, "1")));
      Assert.NotEqual(SqliteOutbox.SemanticPrint(original), SqliteOutbox.SemanticPrint(Captured("\"U\"", ",\"Comentarios\":\"Carla\"", new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, new[] { 0, 1, 2 }, "1")));
      Assert.NotEqual(SqliteOutbox.SemanticPrint(original), SqliteOutbox.SemanticPrint(original.Replace("#fff", "#000")));
    }

    [Fact]
    public void AnOldCopyStartsFromTheCdeVersionOnceItMatches()
    {
      using var folder = new OutboxTests.TestFolder();
      using var outbox = new SqliteOutbox(folder.Path);
      var w1 = Element("W1");
      outbox.Save(Snapshot(w1));
      outbox.RecordBaseline();
      Assert.Equal(1, outbox.ResolveKnown(Remote(new() { ["W1"] = (Print(Element("W1", x: 9)), 5, 5, 7) })));
      // Still different: nothing new is known.
      Assert.Equal(0, outbox.ResolveKnown(Remote(new() { ["W1"] = (Print(Element("W1", x: 8)), 6, 6, 7) })));
      Assert.Equal(1, outbox.ResolveKnown(Remote(new() { ["W1"] = (Print(w1), 7, 7, 7) })));
    }

    [Fact]
    public void EachElementCarriesTheAspectItChangedAndTheVersionItStartedFrom()
    {
      using var folder = new OutboxTests.TestFolder();
      using var outbox = new SqliteOutbox(folder.Path);
      var (w1, w2) = (Element("W1"), Element("W2", name: "Losa"));
      outbox.Save(Snapshot(w1, w2));
      Assert.True(outbox.RecordBaseline());
      var remote = Remote(new() { ["W1"] = (Print(w1), 3, 4, 7), ["W2"] = (Print(w2), 3, 4, 7) });
      Assert.Equal(2, outbox.ResolveKnown(remote));
      // Moved W1 (geometry) and renamed W2 (parameters) after connecting.
      outbox.Save(Snapshot(Element("W1", x: 5), Element("W2", name: "Losa B")));
      var part = ElementsPart(outbox, Prepare(outbox, remote));
      Assert.Equal("g", (string)part["versions"]["W1"]["a"]);
      Assert.Equal("p", (string)part["versions"]["W2"]["a"]);
      Assert.Equal(3, (long)part["versions"]["W1"]["bg"]);
      Assert.Equal(4, (long)part["versions"]["W2"]["bp"]);
      Assert.Null(part["force"]);
    }

    [Fact]
    public void AChangeOnAnOldCopyStaysOutAndIsListedWithWhoChangedIt()
    {
      using var folder = new OutboxTests.TestFolder();
      using var outbox = new SqliteOutbox(folder.Path);
      var (w1, w2) = (Element("W1"), Element("W2"));
      outbox.Save(Snapshot(w1, w2));
      outbox.RecordBaseline();
      // Juan already moved W1 in the CDE: this copy differs from it when connecting.
      var remote = Remote(new() { ["W1"] = (Print(Element("W1", x: 9)), 5, 5, 7), ["W2"] = (Print(w2), 2, 2, 7) });
      outbox.ResolveKnown(remote);
      outbox.Save(Snapshot(Element("W1", x: 3), Element("W2", x: 4)));
      var job = Prepare(outbox, remote);
      var part = ElementsPart(outbox, job);
      Assert.Equal(new[] { "W2" }, part["updated"].Select(e => (string)e["id"]).ToArray());
      var differing = Assert.Single(outbox.Differs);
      Assert.Equal(("W1", "stale", "Juan"), (differing.Id, differing.Reason, differing.By));
    }

    [Fact]
    public void NothingLeftToSendStillMakesAValidJob()
    {
      using var folder = new OutboxTests.TestFolder();
      using var outbox = new SqliteOutbox(folder.Path);
      outbox.Save(Snapshot(Element("W1")));
      outbox.RecordBaseline();
      var remote = Remote(new() { ["W1"] = (Print(Element("W1", x: 9)), 5, 5, 7) });
      outbox.ResolveKnown(remote);
      outbox.Save(Snapshot(Element("W1", x: 3)));
      var job = Prepare(outbox, remote);
      Assert.True(job.ChunkCount >= 1);
      Assert.Null(ElementsPart(outbox, job));
    }

    [Fact]
    public void TheCdeAnswerSetsNewVersionsAndListsWhatStayedOut()
    {
      using var folder = new OutboxTests.TestFolder();
      using var outbox = new SqliteOutbox(folder.Path);
      var (w1, w2) = (Element("W1"), Element("W2"));
      outbox.Save(Snapshot(w1, w2));
      outbox.RecordBaseline();
      var remote = Remote(new() { ["W1"] = (Print(w1), 1, 1, 7), ["W2"] = (Print(w2), 1, 1, 7) });
      outbox.ResolveKnown(remote);
      outbox.Save(Snapshot(Element("W1", x: 2), Element("W2", x: 2)));
      var job = Prepare(outbox, remote);
      outbox.Complete(job);
      outbox.ApplyCommit(new CommitResult { Versions = new() { ["W1"] = new long[] { 9, 1 } },
        Rejected = new() { new DifferingElement { Id = "W2", Name = "Puerta", Reason = "host", By = "Juan" } }, RejectedCount = 1 });
      Assert.Equal("W2", Assert.Single(outbox.Differs).Id);
      // The next change of W1 starts from the version the CDE gave it.
      outbox.Save(Snapshot(Element("W1", x: 3), Element("W2", x: 2)));
      var next = ElementsPart(outbox, Prepare(outbox, Remote(new() { ["W1"] = ("0000000000000000", 9, 1, 7), ["W2"] = ("0000000000000000", 5, 5, 7) })));
      Assert.Equal(9, (long)next["versions"]["W1"]["bg"]);
    }

    [Fact]
    public void OnlyAdministracionForcesItsVersionOfDifferingElements()
    {
      using var folder = new OutboxTests.TestFolder();
      using var outbox = new SqliteOutbox(folder.Path);
      var w1 = Element("W1");
      outbox.Save(Snapshot(w1));
      outbox.RecordBaseline();
      var remote = Remote(new() { ["W1"] = (Print(Element("W1", x: 9)), 5, 5, 7) });
      outbox.ResolveKnown(remote);
      outbox.Save(Snapshot(Element("W1", x: 3)));
      outbox.Complete(Prepare(outbox, remote));
      Assert.Single(outbox.Differs);
      Assert.Equal(1, outbox.Force(outbox.Differs.Select(d => d.Id)));
      Assert.Null(ElementsPart(outbox, Prepare(outbox, remote, admin: false)));
      var forced = ElementsPart(outbox, Prepare(outbox, remote, admin: true));
      Assert.Equal(new[] { "W1" }, forced["force"].Select(t => (string)t).ToArray());
      Assert.Equal(new[] { "W1" }, forced["updated"].Select(e => (string)e["id"]).ToArray());
    }

    [Fact]
    public void AModelFromBeforeVersionsStartsAtVersionZeroAndItsChangesTravel()
    {
      using var folder = new OutboxTests.TestFolder();
      using var outbox = new SqliteOutbox(folder.Path);
      var (w1, w2) = (Element("W1"), Element("W2"));
      outbox.Save(Snapshot(w1, w2));
      outbox.RecordBaseline();
      // The CDE has huellas but no versions yet (models published before element versions).
      var remote = new RemoteFingerprints { Fingerprints = new() { ["W1"] = Print(w1), ["W2"] = Print(Element("W2", x: 8)) },
        Versions = new Dictionary<string, long?[]>() };
      Assert.Equal(2, outbox.ResolveKnown(remote));
      outbox.Save(Snapshot(Element("W1", x: 4), Element("W2", x: 4)));
      var part = ElementsPart(outbox, outbox.Prepare("s", 1, "rev", false, merge: true, remote: remote.Fingerprints, gone: new List<string>(),
        versions: new SqliteOutbox.VersionPlan { Remote = remote.Versions }));
      Assert.Equal(0, (long)part["versions"]["W1"]["bg"]);   // same as the CDE: starts from 0, enters
      Assert.Equal(-1, (long)part["versions"]["W2"]["bg"]);  // already different: an old copy
    }

    [Fact]
    public void AnElementThatMatchesTheCdeAgainStopsDiffering()
    {
      using var folder = new OutboxTests.TestFolder();
      using var outbox = new SqliteOutbox(folder.Path);
      outbox.Save(Snapshot(Element("W1")));
      outbox.RecordBaseline();
      var remote = Remote(new() { ["W1"] = (Print(Element("W1", x: 9)), 5, 5, 7) });
      outbox.ResolveKnown(remote);
      var moved = Element("W1", x: 3);
      outbox.Save(Snapshot(moved));
      outbox.Complete(Prepare(outbox, remote));
      Assert.Single(outbox.Differs);
      // The CDE now has exactly this Revit's element (for instance, forced by Administración).
      outbox.ResolveKnown(Remote(new() { ["W1"] = (Print(moved), 6, 6, 7) }));
      Assert.Empty(outbox.Differs);
    }

    [Fact]
    public void ARemovalOfSomethingChangedMeanwhileStaysOut()
    {
      using var folder = new OutboxTests.TestFolder();
      using var outbox = new SqliteOutbox(folder.Path);
      var (w1, w2) = (Element("W1"), Element("W2"));
      outbox.Save(Snapshot(w1, w2));
      outbox.RecordBaseline();
      var remote = Remote(new() { ["W1"] = (Print(w1), 1, 1, 7), ["W2"] = (Print(w2), 1, 1, 7) });
      outbox.ResolveKnown(remote);
      outbox.Save(Snapshot(Element("W1", x: 1), w2));
      var first = Prepare(outbox, remote);
      outbox.Complete(first);
      outbox.ApplyCommit(new CommitResult { Versions = new() { ["W1"] = new long[] { 2, 1 } } });
      // Juan moved W2 (now v3); this Revit deletes it from its version 1.
      outbox.Save(Snapshot(Element("W1", x: 1)));
      var job = Prepare(outbox, Remote(new() { ["W1"] = (Print(Element("W1", x: 1)), 2, 1, 7), ["W2"] = ("0000000000000000", 3, 3, 7) }));
      Assert.DoesNotContain(Parts(outbox, job), p => (string)p["kind"] == "removed");
      Assert.Equal("W2", Assert.Single(outbox.Differs).Id);
    }
  }
}
