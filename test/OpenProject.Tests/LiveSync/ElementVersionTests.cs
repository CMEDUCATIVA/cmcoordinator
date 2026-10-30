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
    public void AMoveDoesNotCountAsAChangeOfParameters()
    {
      string Wall(string volume, double raw, string comment, double x) =>
        "{\"id\":\"W\",\"name\":\"Muro\",\"properties\":{\"Volumen\":\"" + volume + "\",\"Comentarios\":\"" + comment + "\"},\"bim\":{\"ifcSets\":[{\"kind\":\"qto\",\"name\":\"" + volume + "\"}],\"parameters\":[" +
        "{\"name\":\"Volumen\",\"groupId\":\"autodesk.parameter.group:dimensions-1.0.0\",\"display\":\"" + volume + "\",\"value\":" + raw.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}," +
        "{\"name\":\"Marca\",\"groupId\":\"x\",\"display\":\"A\",\"value\":" + raw.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}," +
        "{\"name\":\"Comentarios\",\"groupId\":\"y\",\"display\":\"" + comment + "\"}]},\"meshes\":[{\"positions\":[" + x.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",0,0]}]}";
      var (g, p) = SqliteOutbox.Aspects(Wall("23.33 m3", 1.1, "", 0));
      var moved = SqliteOutbox.Aspects(Wall("23.40 m3", 1.2, "", 5));
      Assert.NotEqual(g, moved.Geometry);
      Assert.Equal(p, moved.Parameters);
      Assert.NotEqual(p, SqliteOutbox.Aspects(Wall("23.33 m3", 1.1, "Carla", 0)).Parameters);
    }

    [Fact]
    public void AnElementMadeOfTwoPeoplesChangesIsNotSentAgainAsMissing()
    {
      using var folder = new OutboxTests.TestFolder();
      using var outbox = new SqliteOutbox(folder.Path);
      var (w1, w2) = (Element("W1"), Element("W2"));
      outbox.Save(Snapshot(w1, w2));
      Assert.True(outbox.RecordBaseline());
      // W1 has a version in the CDE but no huella: the CDE composed it from two people's changes.
      var remote = Remote(new() { ["W2"] = (Print(w2), 1, 1, 7) });
      ((Dictionary<string, long?[]>)remote.Versions)["W1"] = new long?[] { 5, 6, 7 };
      outbox.ResolveKnown(remote);
      outbox.Save(Snapshot(w1, Element("W2", x: 3)));
      var part = ElementsPart(outbox, Prepare(outbox, remote));
      Assert.Equal(new[] { "W2" }, part["updated"].Select(e => (string)e["id"]).ToArray());
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
    public void ACdeWithoutCatalogReuseStillGetsTheCatalog()
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

    // ---- Contract 1: huellas of models published before the semantic huella.
    private static string Semantic(LiveElement element) => SqliteOutbox.SemanticPrint(SyncEncoding.Json(element));
    // The same wall as another Revit year captures it: other bytes, the same box, centre and values.
    private static LiveElement OtherYear(string id) => new()
    {
      Id = id, ElementId = id, Name = "Muro", Category = "Muros",
      Meshes = new() { new LiveMesh { Positions = new double[] { 0, 1, 0, 0, 0, 0, 1, 0, 0 }, Indices = new[] { 1, 2, 0 } } }
    };

    [Fact]
    public void AnOldHuellaIsUpgradedAndAnotherRevitYearThenStartsFromTheCdeVersion()
    {
      using var folderA = new OutboxTests.TestFolder();
      using var folderB = new OutboxTests.TestFolder();
      using var y2026 = new SqliteOutbox(folderA.Path);
      using var y2027 = new SqliteOutbox(folderB.Path);
      var (wall, sameWall) = (Element("W1"), OtherYear("W1"));
      Assert.Equal(Semantic(wall), Semantic(sameWall));
      Assert.NotEqual(Print(wall), Print(sameWall));
      y2026.Save(Snapshot(wall)); y2026.RecordBaseline();
      y2027.Save(Snapshot(sameWall)); y2027.RecordBaseline();
      // The CDE keeps the exact hash of the wall as Revit 2026 published it.
      var before = Remote(new() { ["W1"] = (Print(wall), 35, 15, 7) });
      y2027.ResolveKnown(before);
      Assert.True(y2027.HasOldCopies);
      Assert.Empty(y2027.PrintUpgrades(before.Fingerprints));
      y2027.Save(Snapshot(OtherYear("W1"), Element("W2")));
      var upgrade = Assert.Single(y2026.PrintUpgrades(before.Fingerprints));
      Assert.Equal(("W1", Print(wall), Semantic(wall)), (upgrade.Key, upgrade.Value.Old, upgrade.Value.New));
      // After the CDE replaces it, the other year matches by the existing mechanism and starts from version 35/15.
      var after = Remote(new() { ["W1"] = (upgrade.Value.New, 35, 15, 7) });
      Assert.Equal(1, y2027.ResolveKnown(after));
      Assert.False(y2027.HasOldCopies);
      Assert.Empty(y2026.PrintUpgrades(after.Fingerprints));
    }

    [Fact]
    public void AMissingOrDifferentHuellaIsNeverUpgraded()
    {
      using var folder = new OutboxTests.TestFolder();
      using var outbox = new SqliteOutbox(folder.Path);
      outbox.Save(Snapshot(Element("W1"), Element("W2")));
      Assert.Empty(outbox.PrintUpgrades(new Dictionary<string, string> { ["W1"] = "0000000000000000", ["W3"] = Print(Element("W3")) }));
      Assert.Empty(outbox.PrintUpgrades(new Dictionary<string, string> { ["W1"] = Semantic(Element("W1")) }));
      Assert.Empty(outbox.PrintUpgrades(null));
    }

    // ---- Contract 4: no empty jobs.
    private static SyncJob PrepareWithCatalog(SqliteOutbox outbox, RemoteFingerprints remote, string catalog) =>
      outbox.Prepare("s", 1, "rev", false, merge: true, remote: remote.Fingerprints, gone: new List<string>(), remoteCatalog: catalog, versions: Plan(remote));
    private static string CatalogHash(SqliteOutbox outbox) => SyncEncoding.Hash(outbox.Get("bim") ?? "null");

    [Fact]
    public void NothingToSendMakesNoJobAndLeavesTheQueueAtRest()
    {
      using var folder = new OutboxTests.TestFolder();
      using var outbox = new SqliteOutbox(folder.Path);
      outbox.Save(Snapshot(Element("W1")));
      outbox.RecordBaseline();
      var remote = Remote(new() { ["W1"] = (Print(Element("W1", x: 9)), 5, 5, 7) });
      outbox.ResolveKnown(remote);
      outbox.Save(Snapshot(Element("W1", x: 3)));
      Assert.True(outbox.HasPending);
      Assert.Null(PrepareWithCatalog(outbox, remote, CatalogHash(outbox)));
      Assert.False(outbox.HasPending);
      Assert.Null(outbox.Active);
      Assert.Equal("0", outbox.Get("dirty"));
      Assert.Equal(0, outbox.PendingElements);
      // What stayed out is still listed, and the history says no job left.
      Assert.Equal("W1", Assert.Single(outbox.Differs).Id);
      Assert.Equal(SqliteOutbox.NoJobState, outbox.History().First().State);
    }

    [Fact]
    public void WithoutChangesNothingIsLeftInTheHistory()
    {
      using var folder = new OutboxTests.TestFolder();
      using var outbox = new SqliteOutbox(folder.Path);
      var wall = Element("W1");
      outbox.Save(Snapshot(wall));
      outbox.RecordBaseline();
      var remote = Remote(new() { ["W1"] = (Print(wall), 1, 1, 7) });
      outbox.ResolveKnown(remote);
      Assert.Null(PrepareWithCatalog(outbox, remote, CatalogHash(outbox)));
      Assert.Empty(outbox.History());
    }

    [Fact]
    public void ANewCatalogIsSentEvenWithoutElements()
    {
      using var folder = new OutboxTests.TestFolder();
      using var outbox = new SqliteOutbox(folder.Path);
      var wall = Element("W1");
      outbox.Save(Snapshot(wall));
      outbox.RecordBaseline();
      var remote = Remote(new() { ["W1"] = (Print(wall), 1, 1, 7) });
      outbox.ResolveKnown(remote);
      var job = PrepareWithCatalog(outbox, remote, "otro-catalogo");
      Assert.Equal("catalog", (string)Assert.Single(Parts(outbox, job))["kind"]);
    }

    // ---- F3: what stayed out is not confirmed.
    [Fact]
    public void AChangePredictedOutIsNotConfirmedAndEntersWhenTheCopyMatchesTheCdeAgain()
    {
      using var folder = new OutboxTests.TestFolder();
      using var outbox = new SqliteOutbox(folder.Path);
      var original = Element("W1");
      var (other, edit) = (Element("W2"), Element("W1", x: 3));
      outbox.Save(Snapshot(original, other));
      outbox.RecordBaseline();
      var stale = Remote(new() { ["W1"] = (Print(Element("W1", x: 9)), 5, 5, 7), ["W2"] = (Print(other), 1, 1, 7) });
      outbox.ResolveKnown(stale);
      outbox.Save(Snapshot(edit, Element("W2", x: 2)));
      var first = Prepare(outbox, stale);
      Assert.Equal(new[] { "W2" }, ElementsPart(outbox, first).Value<JArray>("updated").Select(e => (string)e["id"]).ToArray());
      outbox.Complete(first);
      Assert.Equal("W1", Assert.Single(outbox.Differs).Id);
      // The CDE holds this Revit's original again (for instance, its old huella was upgraded): the edit can enter now.
      var same = Remote(new() { ["W1"] = (Print(original), 6, 6, 7), ["W2"] = (Print(Element("W2", x: 2)), 2, 2, 7) });
      Assert.Equal(1, outbox.ResolveKnown(same));
      Assert.Empty(outbox.Differs);
      Assert.True(outbox.HasPending);
      var part = ElementsPart(outbox, Prepare(outbox, same));
      Assert.Equal(new[] { "W1" }, part["updated"].Select(e => (string)e["id"]).ToArray());
      Assert.Equal(6, (long)part["versions"]["W1"]["bg"]);
    }

    [Fact]
    public void AChangeTheCdeRefusedIsNotConfirmedAndIsNotSentAgainUnchanged()
    {
      using var folder = new OutboxTests.TestFolder();
      using var outbox = new SqliteOutbox(folder.Path);
      var wall = Element("W1");
      outbox.Save(Snapshot(wall));
      outbox.RecordBaseline();
      var remote = Remote(new() { ["W1"] = (Print(wall), 1, 1, 7) });
      outbox.ResolveKnown(remote);
      outbox.Save(Snapshot(Element("W1", x: 3)));
      var job = Prepare(outbox, remote);
      outbox.Complete(job, new CommitResult { Rejected = new() { new DifferingElement { Id = "W1", Reason = "host", By = "Juan", Aspects = new[] { "g" } } }, RejectedCount = 1 });
      Assert.Equal("W1", Assert.Single(outbox.Differs).Id);
      Assert.Null(ElementsPart(outbox, Prepare(outbox, remote)));
      // A new edit is another change: it travels, and against what the CDE really received from here.
      outbox.Save(Snapshot(Element("W1", x: 4)));
      var part = ElementsPart(outbox, Prepare(outbox, remote));
      Assert.Equal("g", (string)part["versions"]["W1"]["a"]);
    }

    [Fact]
    public void ACommentThatStayedOutStillCountsWhenTheElementMovesLater()
    {
      using var folder = new OutboxTests.TestFolder();
      using var outbox = new SqliteOutbox(folder.Path);
      var wall = Element("W1");
      outbox.Save(Snapshot(wall));
      outbox.RecordBaseline();
      var remote = Remote(new() { ["W1"] = (Print(wall), 1, 1, 7) });
      outbox.ResolveKnown(remote);
      // Carla commented first (parameters at 5): this comment stays out.
      var later = Remote(new() { ["W1"] = (Print(Element("W1", name: "Carla")), 1, 5, 7) });
      outbox.Save(Snapshot(Element("W1", name: "Juan")));
      outbox.Complete(Prepare(outbox, later));
      outbox.Save(Snapshot(Element("W1", x: 2, name: "Juan")));
      var part = ElementsPart(outbox, Prepare(outbox, later));
      Assert.Equal("gp", (string)part["versions"]["W1"]["a"]);
    }

    // ---- The list empties when the edit is undone.
    [Fact]
    public void UndoingAnEditThatStayedOutEmptiesTheList()
    {
      using var folder = new OutboxTests.TestFolder();
      using var outbox = new SqliteOutbox(folder.Path);
      var wall = Element("W1");
      outbox.Save(Snapshot(wall));
      outbox.RecordBaseline();
      var remote = Remote(new() { ["W1"] = (Print(Element("W1", x: 9)), 5, 5, 7) });
      outbox.ResolveKnown(remote);
      outbox.Save(Snapshot(Element("W1", x: 3)));
      outbox.Complete(Prepare(outbox, remote));
      Assert.Single(outbox.Differs);
      outbox.Save(Snapshot(Element("W1")));
      Assert.Empty(outbox.Differs);
      // Back at the starting point: nothing to send.
      Assert.Null(PrepareWithCatalog(outbox, remote, CatalogHash(outbox)));
    }

    [Fact]
    public void ARemovalThatStayedOutEndsWhenItIsUndoneOrTheCdeRemovesItToo()
    {
      using var folder = new OutboxTests.TestFolder();
      using var outbox = new SqliteOutbox(folder.Path);
      var (w1, w2) = (Element("W1"), Element("W2"));
      outbox.Save(Snapshot(w1, w2));
      outbox.RecordBaseline();
      var remote = Remote(new() { ["W1"] = (Print(w1), 1, 1, 7), ["W2"] = (Print(w2), 1, 1, 7) });
      outbox.ResolveKnown(remote);
      outbox.Save(Snapshot(Element("W1", x: 1), w2));
      outbox.Complete(Prepare(outbox, remote));
      outbox.ApplyCommit(new CommitResult { Versions = new() { ["W1"] = new long[] { 2, 1 } } });
      outbox.Save(Snapshot(Element("W1", x: 1)));
      var changed = Remote(new() { ["W1"] = (Print(Element("W1", x: 1)), 2, 1, 7), ["W2"] = ("0000000000000000", 3, 3, 7) });
      outbox.Complete(Prepare(outbox, changed));
      var listed = Assert.Single(outbox.Differs);
      Assert.Contains(DifferingElement.RemovalAspect, listed.Aspects);
      outbox.ResolveKnown(Remote(new() { ["W1"] = (Print(Element("W1", x: 1)), 2, 1, 7) }));
      Assert.Empty(outbox.Differs);
    }

    // ---- Contract 2.
    [Fact]
    public void TheManifestSaysWhichRevitSends()
    {
      using var folder = new OutboxTests.TestFolder();
      using var outbox = new SqliteOutbox(folder.Path);
      outbox.Save(Snapshot(Element("W1")));
      var job = outbox.Prepare("s", 1, null, true, revitVersion: "2027");
      Assert.Equal("2027", (string)JObject.Parse(SyncEncoding.Json(job))["revitVersion"]);
      outbox.Save(Snapshot(Element("W1", x: 2)));
      Assert.Null(JObject.Parse(SyncEncoding.Json(outbox.Prepare("s", 1, null, true, revitVersion: "")))["revitVersion"]);
    }

    // ---- Contract 3.
    [Fact]
    public void TheVersionListToleratesServersWithAndWithoutNumbers()
    {
      var prints = Newtonsoft.Json.JsonConvert.DeserializeObject<RemoteFingerprints>(
        "{\"fingerprints\":{},\"versions\":{" +
        "\"old\":[3,4,7]," +
        "\"plain\":[3,4,7,\"2026-10-30T12:00:00Z\",null]," +
        "\"numbered\":[3,4,null,\"2026-10-30T12:00:00Z\",\"H\",9]}}");
      Assert.Equal(new long?[] { 3, 4, 7 }, prints.Versions["old"]);
      Assert.Equal(new long?[] { 3, 4, 7, null, null }, prints.Versions["plain"]);
      Assert.Equal(new long?[] { 3, 4, null, null, null, 9 }, prints.Versions["numbered"]);
      Assert.Contains("2026", prints.VersionTimes["numbered"]);
      Assert.Null(Newtonsoft.Json.JsonConvert.DeserializeObject<RemoteFingerprints>("{\"fingerprints\":{}}").Versions);
    }

    [Fact]
    public void TheNumberIsKeptFromTheCommitAndFromTheListAndShownInTheDetail()
    {
      using var folder = new OutboxTests.TestFolder();
      using var outbox = new SqliteOutbox(folder.Path);
      var (w1, w2) = (Element("W1"), Element("W2"));
      outbox.Save(Snapshot(w1, w2));
      outbox.RecordBaseline();
      var remote = new RemoteFingerprints { Fingerprints = new() { ["W1"] = Print(w1), ["W2"] = Print(w2) },
        Versions = new() { ["W1"] = new long?[] { 1, 1, 7, null, null, 2 }, ["W2"] = new long?[] { 1, 1, 7, null, null, 4 } },
        Authors = new() { ["7"] = "Juan" }, VersionTimes = new() { ["W1"] = "2026-10-30T12:00:00Z" } };
      outbox.ResolveKnown(remote);
      outbox.Save(Snapshot(Element("W1", x: 1), Element("W2", x: 1)));
      var job = Prepare(outbox, remote);
      outbox.Complete(job, new CommitResult { Versions = new() { ["W2"] = new long[] { 8, 1 } }, VersionNumbers = new() { ["W2"] = 5 } });
      // W2 enters (now v5); then Juan takes W1 to v3 and this Revit's next change of W1 stays out.
      outbox.Save(Snapshot(Element("W1", x: 2), Element("W2", x: 1)));
      var later = new RemoteFingerprints { Fingerprints = new() { ["W1"] = "0000000000000000", ["W2"] = Print(Element("W2", x: 1)) },
        Versions = new() { ["W1"] = new long?[] { 9, 9, 7, null, null, 3 }, ["W2"] = new long?[] { 8, 1, 7, null, null, 5 } },
        Authors = new() { ["7"] = "Juan" }, VersionTimes = new() { ["W1"] = "2026-10-30T12:00:00Z" } };
      outbox.ResolveKnown(later);
      var plan = new SqliteOutbox.VersionPlan { Remote = later.Versions, Authors = later.Authors, Times = later.VersionTimes };
      Assert.Null(outbox.Prepare("s", 1, "rev", false, merge: true, remote: later.Fingerprints, gone: new List<string>(), remoteCatalog: CatalogHash(outbox), versions: plan));
      var item = Assert.Single(outbox.Differs);
      Assert.Equal((2L, 3L), (item.Mine, item.Theirs));
      Assert.Equal("Tu copia v2 · CDE v3", OpenProject.Revit.LiveSync.Presentation.DifferDetail.Versions(item));
      var lines = OpenProject.Revit.LiveSync.Presentation.DifferDetail.Lines(item);
      Assert.Contains(lines, line => line == "Quién: Juan");
      Assert.Contains(lines, line => line.StartsWith("Cuándo: "));
      Assert.Contains(lines, line => line.StartsWith("Motivo: Juan cambió la geometría"));
    }

    [Fact]
    public void BothNamesOfARemovalReadAsYourDeletion()
    {
      foreach (var aspect in new[] { "removed", "d" })
        Assert.Equal("No entró: tu borrado", OpenProject.Revit.LiveSync.Presentation.DifferDetail.Versions(new DifferingElement { Id = "W", Aspects = new[] { aspect } }));
    }

    [Fact]
    public void WithoutNumbersTheDetailNamesTheAspect()
    {
      var item = new DifferingElement { Id = "W", Name = "Muro", Reason = "stale", By = "Juan", Aspects = new[] { "p" } };
      Assert.Equal("No entró: los parámetros", OpenProject.Revit.LiveSync.Presentation.DifferDetail.Versions(item));
      Assert.DoesNotContain(OpenProject.Revit.LiveSync.Presentation.DifferDetail.Lines(item), line => System.Text.RegularExpressions.Regex.IsMatch(line, @"vd"));
      Assert.Equal("Tu copia es anterior a la del CDE", OpenProject.Revit.LiveSync.Presentation.DifferDetail.Versions(new DifferingElement { Id = "W" }));
    }

    [Fact]
    public void TheQueueStateSaysHowManyStayedOut()
    {
      var outside = new[] { new DifferingElement { Id = "A" }, new DifferingElement { Id = "B" }, new DifferingElement { Id = "C" } };
      Assert.Equal("Al día", OpenProject.Revit.LiveSync.Presentation.DifferDetail.QueueState(new SyncProgress()));
      Assert.Equal("Al día · 3 fuera", OpenProject.Revit.LiveSync.Presentation.DifferDetail.QueueState(new SyncProgress { Differs = outside }));
      Assert.Equal("En espera", OpenProject.Revit.LiveSync.Presentation.DifferDetail.QueueState(new SyncProgress { Differs = outside, HasPendingChanges = true }));
      Assert.Equal("3 elementos no entraron", OpenProject.Revit.LiveSync.Presentation.DifferDetail.Title(3));
      Assert.Equal("1 elemento no entró", OpenProject.Revit.LiveSync.Presentation.DifferDetail.Title(1));
    }

    [Fact]
    public void AQueueFromBeforeTheFixGetsItsReferenceBack()
    {
      using var folder = new OutboxTests.TestFolder();
      var wall = Element("W1");
      using (var outbox = new SqliteOutbox(folder.Path))
      {
        outbox.Save(Snapshot(wall));
        outbox.RecordBaseline();
        var remote = Remote(new() { ["W1"] = (Print(Element("W1", x: 9)), 5, 5, 7) });
        outbox.ResolveKnown(remote);
        outbox.Save(Snapshot(Element("W1", x: 3)));
        outbox.Complete(Prepare(outbox, remote));
      }
      // As the installed version left it: the change that stayed out sits in `confirmed` and the list has no attempt.
      using (var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + System.IO.Path.Combine(folder.Path, "outbox.sqlite") + ";Pooling=False"))
      {
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO confirmed SELECT * FROM desired; UPDATE differs SET hash=NULL; DELETE FROM meta WHERE key='withheldFormat'";
        command.ExecuteNonQuery();
      }
      using (var outbox = new SqliteOutbox(folder.Path))
      {
        Assert.Single(outbox.Differs);
        Assert.Equal(1, outbox.ResolveKnown(Remote(new() { ["W1"] = (Print(wall), 6, 6, 7) })));
      }
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
