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
  /// <summary>UI-8 logic 8 after the audit of 31/10: huella per aspect (F4, F6), who changed what (F3), joined elements (F7) and what stayed out without travelling (8.J).</summary>
  public class ConflictRecoveryTests
  {
    private static LiveElement Element(string id, double x = 0, string comment = null, params string[] joined)
    {
      var element = new LiveElement
      {
        Id = id, ElementId = id, Name = "Muro", Category = "Muros", Joined = joined.ToList(),
        Meshes = new() { new LiveMesh { Positions = new[] { x, 0, 0, x + 1, 0, 0, x, 1, 0 }, Indices = new[] { 0, 1, 2 } } }
      };
      if (comment != null) element.Properties["Comentarios"] = comment;
      return element;
    }
    private static LiveSnapshot Snapshot(params LiveElement[] elements) => new("model", "Model", "3D", elements.ToDictionary(e => e.Id, e => e));
    private static string Json(LiveElement element) => SyncEncoding.Json(element);
    private static string Print(LiveElement element) => SyncEncoding.Hash(Json(element)).Substring(0, SqliteOutbox.FingerprintLength);
    private static RemoteFingerprints Remote(Dictionary<string, (string Print, long G, long P, long? By)> elements, long? restored = null) => new()
    {
      Fingerprints = elements.Where(e => e.Value.Print != null).ToDictionary(e => e.Key, e => e.Value.Print),
      Versions = elements.ToDictionary(e => e.Key, e => new long?[] { e.Value.G, e.Value.P, e.Value.By }),
      Authors = new Dictionary<string, string> { ["7"] = "Juan" },
      RestoredClock = restored
    };
    private static SyncJob Prepare(SqliteOutbox outbox, RemoteFingerprints remote) =>
      outbox.Prepare("s", 1, "rev", false, merge: true, remote: remote.Fingerprints, gone: new List<string>(), remoteCatalog: SyncEncoding.Hash(outbox.Get("bim") ?? "null"),
        versions: new SqliteOutbox.VersionPlan { Remote = remote.Versions, Authors = remote.Authors, RestoredClock = remote.RestoredClock });
    private static JObject ElementsPart(SqliteOutbox outbox, SyncJob job) =>
      Enumerable.Range(0, job.ChunkCount).Select(i => JObject.Parse(Encoding.UTF8.GetString(outbox.Chunk(i)))).First(p => (string)p["kind"] == "elements");

    private static string Captured(string typeName, string extra, double[] positions, int[] indices, string decimals, string comment = "", double volume = 1) =>
      "{\"id\":\"W\",\"name\":\"Muro\",\"category\":\"Muros\",\"properties\":{\"Volumen\":\"" + volume + " m3\",\"Largo\":\"" + decimals + " m\"," + typeName + ":\"3.7021 W\",\"Comentarios\":\"" + comment + "\"" + extra +
      "},\"bim\":{\"parameters\":[{\"name\":\"Volumen\",\"groupId\":\"autodesk.parameter.group:dimensions-1.0.0\"}]},\"meshes\":[{\"positions\":" + Newtonsoft.Json.JsonConvert.SerializeObject(positions) + ",\"indices\":" + Newtonsoft.Json.JsonConvert.SerializeObject(indices) + ",\"color\":\"#fff\",\"opacity\":1.0}]}";

    [Fact]
    public void TheHuellaOfEachAspectIsTheSameInAnotherRevitYear()
    {
      var y2026 = Captured("\"Tipo · U\"", "", new double[] { 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0 }, new[] { 0, 1, 2, 0, 2, 3 }, "1763.5639755188622");
      var y2027 = Captured("\"Tipo · Transmitancia\"", ",\"Alojado\":\"No\"", new double[] { 1, 1, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0 }, new[] { 2, 3, 0, 2, 0, 1 }, "1763.5639755188654");
      Assert.Equal(SqliteOutbox.AspectPrints(y2026), SqliteOutbox.AspectPrints(y2027));
    }

    [Fact]
    public void AMoveChangesOnlyTheGeometryAndACommentOnlyTheParameters()
    {
      var original = SqliteOutbox.AspectPrints(Captured("\"U\"", "", new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, new[] { 0, 1, 2 }, "1"));
      var moved = SqliteOutbox.AspectPrints(Captured("\"U\"", "", new double[] { 5, 0, 0, 6, 0, 0, 5, 1, 0 }, new[] { 0, 1, 2 }, "1", volume: 2));
      var commented = SqliteOutbox.AspectPrints(Captured("\"U\"", "", new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, new[] { 0, 1, 2 }, "1", "Carla"));
      Assert.NotEqual(original.Geometry, moved.Geometry);
      Assert.Equal(original.Parameters, moved.Parameters);
      Assert.Equal(original.Geometry, commented.Geometry);
      Assert.NotEqual(original.Parameters, commented.Parameters);
    }

    // F4: a restoration raises every version; what this Revit holds the same can be edited, and nobody is locked out.
    [Fact]
    public void AfterARestorationAnEqualElementStartsFromTheNewVersion()
    {
      using var folder = new OutboxTests.TestFolder();
      using var outbox = new SqliteOutbox(folder.Path);
      var wall = Element("W1");
      outbox.Save(Snapshot(wall));
      outbox.RecordBaseline();
      outbox.ResolveKnown(Remote(new() { ["W1"] = (Print(wall), 5, 5, 7) }));
      var restored = Remote(new() { ["W1"] = (Print(wall), 9, 9, 7) }, restored: 9);
      outbox.ResolveKnown(restored);
      outbox.Save(Snapshot(Element("W1", x: 3)));
      var entry = ElementsPart(outbox, Prepare(outbox, restored))["versions"]["W1"];
      Assert.Equal(9, (long)entry["bg"]);
      Assert.Equal("g", (string)entry["a"]);
    }

    // F4: what is different from the restored model stays out and says why, once.
    [Fact]
    public void WhatDiffersFromTheRestoredModelStaysOutAsRestoredAndIsToldOnce()
    {
      using var folder = new OutboxTests.TestFolder();
      using var outbox = new SqliteOutbox(folder.Path);
      var wall = Element("W1");
      outbox.Save(Snapshot(wall));
      outbox.RecordBaseline();
      outbox.ResolveKnown(Remote(new() { ["W1"] = (Print(wall), 5, 5, 7) }));
      var restored = Remote(new() { ["W1"] = (Print(Element("W1", x: 8)), 9, 9, 7) }, restored: 9);
      outbox.ResolveKnown(restored);
      outbox.Save(Snapshot(Element("W1", x: 3)));
      Assert.Null(Prepare(outbox, restored));
      Assert.Equal("restored", outbox.Differs.Single().Reason);
      Assert.Equal("Juan", outbox.Differs.Single().By);
      Assert.Equal("restored", outbox.PendingWithheld.Single().Reason);
      outbox.ClearWithheld(1);
      Assert.Null(Prepare(outbox, restored));
      Assert.Empty(outbox.PendingWithheld);
    }

    // F6: an element made of two people's changes has no huella; each aspect is recognized by itself.
    [Fact]
    public void EachAspectCatchesUpByItself()
    {
      using var folder = new OutboxTests.TestFolder();
      using var outbox = new SqliteOutbox(folder.Path);
      var wall = Element("W1");
      outbox.Save(Snapshot(wall));
      outbox.RecordBaseline();
      outbox.ResolveKnown(Remote(new() { ["W1"] = (Print(wall), 5, 5, 7) }));
      var mine = SqliteOutbox.AspectPrints(Json(wall));
      var theirs = SqliteOutbox.AspectPrints(Json(Element("W1", x: 8)));
      var composed = Remote(new() { ["W1"] = (null, 9, 9, 7) });
      composed.AspectPrints["W1"] = new[] { theirs.Geometry, mine.Parameters };
      outbox.ResolveKnown(composed);
      outbox.Save(Snapshot(Element("W1", comment: "Carla")));
      var entry = ElementsPart(outbox, Prepare(outbox, composed))["versions"]["W1"];
      Assert.Equal(5, (long)entry["bg"]);
      Assert.Equal(9, (long)entry["bp"]);
    }

    // F3: the author and the host of the aspect that stayed out, not of the last change of the element.
    [Fact]
    public void TheAuthorIsOfTheAspectThatStayedOut()
    {
      using var folder = new OutboxTests.TestFolder();
      using var outbox = new SqliteOutbox(folder.Path);
      var wall = Element("W1");
      outbox.Save(Snapshot(wall));
      outbox.RecordBaseline();
      outbox.ResolveKnown(Remote(new() { ["W1"] = (Print(wall), 5, 5, 8) }));
      var remote = Remote(new() { ["W1"] = (Print(Element("W1", x: 8)), 9, 5, 8) });
      remote.Versions["W1"] = new long?[] { 9, 5, 8, null, null, 3, 7, 8, 1 };
      outbox.Save(Snapshot(Element("W1", x: 3)));
      Assert.Null(Prepare(outbox, remote));
      Assert.Equal("host", outbox.Differs.Single().Reason);
      Assert.Equal("Juan", outbox.Differs.Single().By);
    }

    // F7: the CDE decides the group, so it is told what is joined and what stayed out without travelling.
    [Fact]
    public void AJoinedElementTravelsWithWhatIsJoinedAndWhatStayedOut()
    {
      using var folder = new OutboxTests.TestFolder();
      using var outbox = new SqliteOutbox(folder.Path);
      var (first, second) = (Element("W1", joined: "W2"), Element("W2", joined: "W1"));
      outbox.Save(Snapshot(first, second));
      outbox.RecordBaseline();
      outbox.ResolveKnown(Remote(new() { ["W1"] = (Print(first), 1, 1, 7), ["W2"] = (Print(second), 1, 1, 7) }));
      var remote = Remote(new() { ["W1"] = (Print(first), 1, 1, 7), ["W2"] = (Print(Element("W2", x: 9)), 9, 9, 7) });
      outbox.Save(Snapshot(Element("W1", x: 2, joined: "W2"), Element("W2", x: 2, joined: "W1")));
      var part = ElementsPart(outbox, Prepare(outbox, remote));
      Assert.Equal(new[] { "W1" }, part["updated"].Select(e => (string)e["id"]).ToArray());
      Assert.Equal(new[] { "W2" }, part["joined"]["W1"].Values<string>().ToArray());
      Assert.Equal(new[] { "W2" }, part["outside"].Values<string>().ToArray());
      Assert.Equal(2, part["aspectPrints"]["W1"].Count());
      Assert.Null(part["updated"][0]["joined"]);
    }

    // What Revit keeps joined is not part of the element: adding it does not make the whole model look changed.
    [Fact]
    public void TheJoinsDoNotChangeTheHashOfAnElement() =>
      Assert.Equal(SqliteOutbox.ElementHash(Json(Element("W1"))), SqliteOutbox.ElementHash(Json(Element("W1", joined: "W2"))));

    // A joined element out with another waits for it instead of being refused on every send.
    [Fact]
    public void WhatIsJoinedToOneStillOutIsNotSentAgain()
    {
      using var folder = new OutboxTests.TestFolder();
      using var outbox = new SqliteOutbox(folder.Path);
      var (first, second) = (Element("W1", joined: "W2"), Element("W2", joined: "W1"));
      outbox.Save(Snapshot(first, second));
      outbox.RecordBaseline();
      var remote = Remote(new() { ["W1"] = (Print(first), 1, 1, 7), ["W2"] = (Print(second), 1, 1, 7) });
      outbox.ResolveKnown(remote);
      outbox.Save(Snapshot(Element("W1", x: 2, joined: "W2"), Element("W2", x: 2, joined: "W1")));
      var job = Prepare(outbox, remote);
      outbox.Complete(job, new CommitResult
      {
        RejectedCount = 2,
        Rejected = new()
        {
          new DifferingElement { Id = "W1", Reason = "joined", Aspects = new[] { "g" } },
          new DifferingElement { Id = "W2", Reason = "stale", Aspects = new[] { "g" } }
        }
      });
      var later = Remote(new() { ["W1"] = (Print(first), 1, 1, 7), ["W2"] = (Print(Element("W2", x: 9)), 9, 9, 7) });
      outbox.Save(Snapshot(Element("W1", x: 2, joined: "W2"), Element("W2", x: 2, joined: "W1")));
      Assert.Null(Prepare(outbox, later));
      Assert.Equal(2, outbox.Differs.Count);
    }
  }
}
