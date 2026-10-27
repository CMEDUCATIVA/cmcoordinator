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
  public class BimContractTests
  {
    private static LiveSnapshot Snapshot(string title, bool withGroups = true)
    {
      var element = new LiveElement { Id = "element", ElementId = "123", Name = "Muro", Category = "Muros",
        Bim = new BimElement { DocumentId = "doc", IfcClass = "IfcWall",
          Parameters = Enumerable.Range(0, 300).Select(i => new BimParameter { Id = i.ToString(), Name = "Mismo nombre",
            GroupId = withGroups ? "autodesk.parameter.group:dimensions-1.0.0" : null,
            GroupName = withGroups ? "Cotas" : null,
            Storage = "Double", Value = 10.0, SiValue = 3.048, SpecId = "length", UnitId = "metres", HasValue = true }).ToList() } };
      return new LiveSnapshot("doc", "Modelo", "3D", new Dictionary<string, LiveElement> { [element.Id] = element },
        new BimContext { ScopeId = "view", Documents = new List<BimDocument> { new() { Id = "doc", Title = title } },
          Types = new Dictionary<string, BimType> { ["type"] = new() { Id = "type", Name = "Tipo de muro",
            Parameters = new List<BimParameter> { new() { Id = "type-param", Name = "Marca de tipo",
              GroupId = withGroups ? "identity" : null, GroupName = withGroups ? "Datos de identidad" : null,
              Value = "T1", HasValue = true } } } } });
    }
    [Fact]
    public void ParametersOmitEmptyAndUnconsumedFieldsButKeepWhatTheCdeReads()
    {
      var snapshot = Snapshot("Estructura");
      var parameter = snapshot.Elements["element"].Bim.Parameters[0];
      parameter.ReadOnly = true; parameter.DisplayUnitValue = 10; parameter.SharedGuid = null; parameter.Display = null;
      using var folder = new OutboxTests.TestFolder();
      using var box = new SqliteOutbox(folder.Path);
      box.Save(snapshot);
      var sent = (JObject)Read(box, box.Prepare("session", 1, null, true))["updated"][0]["bim"]["parameters"][0];
      // IFC publication and the change history read these fields.
      foreach (var name in new[] { "id", "name", "groupId", "groupName", "specId", "unitId", "value", "siValue", "hasValue" })
        Assert.True(sent.ContainsKey(name), name);
      Assert.True(sent["hasValue"].Value<bool>());
      foreach (var name in new[] { "readOnly", "displayUnitValue", "sharedGuid", "display", "referenceId" })
        Assert.False(sent.ContainsKey(name), name);
    }
    [Fact]
    public void NativeIfcSetsSurviveOutboxWithoutReplacingRevitParameters()
    {
      var snapshot = Snapshot("Estructura");
      snapshot.Elements["element"].Bim.IfcSets.Add(new IfcPropertySet { Name = "Qto_WallBaseQuantities", Kind = "qto",
        Properties = new List<IfcProperty> { new() { Name = "NetVolume", Value = 1.25, Unit = "cubic_metre", Type = "volume" } } });
      using var folder = new OutboxTests.TestFolder();
      using var box = new SqliteOutbox(folder.Path);
      box.Save(snapshot);
      var job = box.Prepare("session", 1, null, true);
      var bim = Read(box, job)["updated"][0]["bim"];
      Assert.Equal("Qto_WallBaseQuantities", bim["ifcSets"][0]["name"].Value<string>());
      Assert.Equal(1.25, bim["ifcSets"][0]["properties"][0]["value"].Value<double>());
      Assert.Equal("cubic_metre", bim["ifcSets"][0]["properties"][0]["unit"].Value<string>());
      Assert.Equal("volume", bim["ifcSets"][0]["properties"][0]["type"].Value<string>());
      Assert.Equal(300, bim["parameters"].Count());
    }
    [Fact]
    public void AllTypedParametersSurviveOutboxAndDocumentMetadataIsFrozenWithItsJob()
    {
      using var folder = new OutboxTests.TestFolder();
      using var box = new SqliteOutbox(folder.Path);
      box.Save(Snapshot("Original")); var first = box.Prepare("session", 1, null, true);
      var json = Read(box, first);
      Assert.Equal(300, json["updated"][0]["bim"]["parameters"].Count());
      Assert.Equal(3.048, json["updated"][0]["bim"]["parameters"][0]["siValue"].Value<double>());
      Assert.Equal("autodesk.parameter.group:dimensions-1.0.0", json["updated"][0]["bim"]["parameters"][0]["groupId"].Value<string>());
      Assert.Equal("Cotas", json["updated"][0]["bim"]["parameters"][0]["groupName"].Value<string>());
      Assert.Equal("Datos de identidad", json["bim"]["types"]["type"]["parameters"][0]["groupName"].Value<string>());
      box.Save(Snapshot("Nuevo"));
      Assert.Equal("Original", Read(box, first)["bim"]["documents"][0]["title"].Value<string>());
      box.Complete(first);
      var second = box.Prepare("session", 1, first.JobId, false);
      Assert.Equal("Nuevo", Read(box, second)["bim"]["documents"][0]["title"].Value<string>());
    }
    [Fact]
    public void AddingGroupsToAnUnchangedModelQueuesAParameterUpdate()
    {
      using var folder = new OutboxTests.TestFolder();
      using var box = new SqliteOutbox(folder.Path);
      box.Save(Snapshot("Original", false));
      var first = box.Prepare("session", 1, null, true);
      box.Complete(first);
      Assert.False(box.HasPending);

      box.Save(Snapshot("Original"));
      Assert.Equal(1, box.PendingElements);
      var second = box.Prepare("session", 2, first.JobId, false);
      var json = Read(box, second);
      Assert.Single((JArray)json["updated"]);
      Assert.Equal("Cotas", json["updated"][0]["bim"]["parameters"][0]["groupName"].Value<string>());
    }
    private static JObject Read(SqliteOutbox box, SyncJob job)
    {
      var parts = Enumerable.Range(0, job.ChunkCount).Select(i => JObject.Parse(Encoding.UTF8.GetString(box.Chunk(i)))).ToList();
      return new JObject {
        ["bim"] = parts.First(part => (string)part["kind"] == "catalog")["bim"],
        ["updated"] = new JArray(parts.Where(part => (string)part["kind"] == "elements").SelectMany(part => part["updated"])),
        ["removed"] = new JArray(parts.Where(part => (string)part["kind"] == "removed").SelectMany(part => part["removed"]))
      };
    }
  }
}
