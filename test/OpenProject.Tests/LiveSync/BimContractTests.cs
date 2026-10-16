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
    private static LiveSnapshot Snapshot(string title)
    {
      var element = new LiveElement { Id = "element", ElementId = "123", Name = "Muro", Category = "Muros",
        Bim = new BimElement { DocumentId = "doc", IfcClass = "IfcWall",
          Parameters = Enumerable.Range(0, 300).Select(i => new BimParameter { Id = i.ToString(), Name = "Mismo nombre",
            Storage = "Double", Value = 10.0, SiValue = 3.048, SpecId = "length", UnitId = "metres", HasValue = true }).ToList() } };
      return new LiveSnapshot("doc", "Modelo", "3D", new Dictionary<string, LiveElement> { [element.Id] = element },
        new BimContext { ScopeId = "view", Documents = new List<BimDocument> { new() { Id = "doc", Title = title } } });
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
      box.Save(Snapshot("Nuevo"));
      Assert.Equal("Original", Read(box, first)["bim"]["documents"][0]["title"].Value<string>());
      box.Complete(first);
      var second = box.Prepare("session", 1, first.JobId, false);
      Assert.Equal("Nuevo", Read(box, second)["bim"]["documents"][0]["title"].Value<string>());
    }
    private static JObject Read(SqliteOutbox box, SyncJob job) => JObject.Parse(Encoding.UTF8.GetString(
      Enumerable.Range(0, job.ChunkCount).SelectMany(box.Chunk).ToArray()));
  }
}
