using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using OpenProject.Revit.LiveSync;
using OpenProject.Revit.LiveSync.Bim;
using OpenProject.Revit.LiveSync.Queue;
using Xunit;

namespace OpenProject.Tests.LiveSync
{
  public class PartCompactorTests
  {
    private static readonly double[] Triangle = { 0, 0, 0, 1, 0, 0, 0, 1, 0 };

    private static LiveElement Element(string id, double dx, double dy, double dz, double[] shape = null, string payload = null)
    {
      var source = shape ?? Triangle;
      var positions = source.Select((value, index) => value + (index % 3 == 0 ? dx : index % 3 == 1 ? dy : dz)).ToArray();
      var element = new LiveElement { Id = id, ElementId = id, Name = id, Category = "Montantes",
        Bim = new BimElement { DocumentId = "doc", IfcClass = "IfcMember", IfcSets = new List<IfcPropertySet> {
          new() { Name = "Pset_MemberCommon", Kind = "pset", Properties = new List<IfcProperty> { new() { Name = "Reference", Value = "M1" } } },
          new() { Name = "Instancia · Cotas", Kind = "pset", Properties = new List<IfcProperty> { new() { Name = "Longitud", Value = 1.0 } } },
          new() { Name = "Tipo · Cotas · M1", Kind = "pset", Properties = new List<IfcProperty> { new() { Name = "Ancho", Value = 0.05 } } } } } };
      element.Meshes.Add(new LiveMesh { Positions = positions, Indices = new[] { 0, 1, 2 }, Color = "#336699", Opacity = 1 });
      if (payload != null) element.Properties["payload"] = payload;
      return element;
    }

    private static List<JObject> ElementParts(Dictionary<string, LiveElement> elements, bool compact)
    {
      using var folder = new OutboxTests.TestFolder();
      using var box = new SqliteOutbox(folder.Path);
      box.Save(new LiveSnapshot("doc", "Modelo", "3D", elements));
      var job = box.Prepare("session", 1, null, true, compact: compact);
      return Enumerable.Range(0, job.ChunkCount).Select(i => JObject.Parse(Encoding.UTF8.GetString(box.Chunk(i))))
        .Where(part => (string)part["kind"] == "elements").ToList();
    }

    // Mirrors the viewer: shape positions plus the mesh offset.
    private static double[] Expand(JObject part, JObject mesh)
    {
      var shape = part["geometries"][(string)mesh["geometry"]];
      var offset = mesh["offset"].Values<double>().ToArray();
      return shape["positions"].Values<double>().Select((value, index) => value + offset[index % 3]).ToArray();
    }

    [Fact]
    public void TranslatedCopiesShareOneShapeAndExpandToTheOriginalPositions()
    {
      var elements = new Dictionary<string, LiveElement> { ["a"] = Element("a", 0, 0, 0), ["b"] = Element("b", 10.5, -3.25, 2),
        ["c"] = Element("c", 1, 1, 1, new double[] { 0, 0, 0, 2, 0, 0, 0, 0, 3 }) };
      var part = ElementParts(elements, compact: true).Single();
      Assert.Equal(2, ((JObject)part["geometries"]).Count);
      var meshes = part["updated"].ToDictionary(e => (string)e["id"], e => (JObject)e["meshes"][0]);
      Assert.Equal((string)meshes["a"]["geometry"], (string)meshes["b"]["geometry"]);
      Assert.NotEqual((string)meshes["a"]["geometry"], (string)meshes["c"]["geometry"]);
      foreach (var id in elements.Keys)
      {
        Assert.Equal(elements[id].Meshes[0].Positions, Expand(part, meshes[id]));
        Assert.Equal("#336699", (string)meshes[id]["color"]);
        Assert.Null(meshes[id]["positions"]);
      }
    }

    [Fact]
    public void RevitGroupsAreLeftToTheViewerButStandardSetsStay()
    {
      var part = ElementParts(new Dictionary<string, LiveElement> { ["a"] = Element("a", 0, 0, 0) }, compact: true).Single();
      var names = part["updated"][0]["bim"]["ifcSets"].Select(set => (string)set["name"]).ToList();
      Assert.Equal(new[] { "Pset_MemberCommon" }, names);
    }

    [Fact]
    public void ServersWithoutSharedGeometryReceiveTheUnchangedFormat()
    {
      var part = ElementParts(new Dictionary<string, LiveElement> { ["a"] = Element("a", 1, 2, 3) }, compact: false).Single();
      Assert.Null(part["geometries"]);
      Assert.Equal(9, part["updated"][0]["meshes"][0]["positions"].Count());
      Assert.Equal(3, part["updated"][0]["bim"]["ifcSets"].Count());
    }

    [Fact]
    public void ManySmallElementsAreSplitBelowTheCdeLimitPerPart()
    {
      var elements = Enumerable.Range(0, 10_005).ToDictionary(i => "s" + i,
        i => new LiveElement { Id = "s" + i, ElementId = i.ToString(), Name = "s", Category = "Demo" });
      var parts = ElementParts(elements, compact: true);
      Assert.Equal(2, parts.Count);
      Assert.All(parts, part => Assert.True(part["updated"].Count() <= 10_000));
      Assert.Equal(10_005, parts.Sum(part => part["updated"].Count()));
    }

    [Fact]
    public void EveryPartCarriesTheShapesItsMeshesReference()
    {
      var payload = new string('x', 3 * 1024 * 1024);
      var elements = Enumerable.Range(0, 6).ToDictionary(i => "e" + i, i => Element("e" + i, i, 0, 0, payload: payload));
      var parts = ElementParts(elements, compact: true);
      Assert.True(parts.Count > 1);
      foreach (var part in parts)
        foreach (var element in part["updated"])
        {
          var mesh = (JObject)element["meshes"][0];
          Assert.NotNull(part["geometries"][(string)mesh["geometry"]]);
          Assert.Equal(elements[(string)element["id"]].Meshes[0].Positions, Expand(part, mesh));
        }
    }

    [Fact]
    public void AnElementOpeningAPartKeepsAShapeFirstSeenInThePreviousPart()
    {
      // e2 reuses e0's shape, opens the second part, and no later element there brings that shape back.
      var payload = new string('x', 3 * 1024 * 1024);
      var other = new double[] { 0, 0, 0, 2, 0, 0, 0, 0, 3 };
      var elements = new Dictionary<string, LiveElement> {
        ["e0"] = Element("e0", 0, 0, 0, payload: payload), ["e1"] = Element("e1", 5, 0, 0, other, payload),
        ["e2"] = Element("e2", 9, 0, 0, payload: payload), ["e3"] = Element("e3", 7, 0, 0, other, payload) };
      var parts = ElementParts(elements, compact: true);
      Assert.True(parts.Count > 1);
      foreach (var part in parts)
        foreach (var element in part["updated"])
        {
          var mesh = (JObject)element["meshes"][0];
          Assert.NotNull(part["geometries"][(string)mesh["geometry"]]);
          Assert.Equal(elements[(string)element["id"]].Meshes[0].Positions, Expand(part, mesh));
        }
    }
  }
}
