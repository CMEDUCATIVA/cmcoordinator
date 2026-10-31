using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using OpenProject.Revit.LiveSync;
using OpenProject.Revit.LiveSync.Queue;
using Xunit;

namespace OpenProject.Tests.LiveSync
{
  public class QueueFolderTests
  {
    private const string Original = @"C:\Modelos\Torre.rvt";

    private static LiveElement Element(string id) => new()
    {
      Id = id, ElementId = id, Name = "Muro", Category = "Muros",
      Meshes = new() { new LiveMesh { Positions = new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, Indices = new[] { 0, 1, 2 } } }
    };
    private static LiveSnapshot Snapshot(params string[] ids) => new("model", "Model", "3D", ids.ToDictionary(id => id, Element));
    private static bool Removes(SqliteOutbox outbox, string id)
    {
      var job = outbox.Prepare("s", 1, "rev", false, merge: true);
      return job != null && Enumerable.Range(0, job.ChunkCount).Select(i => JObject.Parse(Encoding.UTF8.GetString(outbox.Chunk(i))))
        .Any(part => (string)part["kind"] == "removed" && part["removed"].Values<string>().Contains(id));
    }

    [Fact]
    public void TheFirstFileKeepsTheQueueAndItsCopyGetsAnother()
    {
      using var folder = new OutboxTests.TestFolder();
      var original = QueueFolder.Claim(folder.Path, Original);
      Assert.Equal(folder.Path, original);
      Assert.Equal(original, QueueFolder.Claim(folder.Path, @"c:/modelos/TORRE.rvt"));
      var copy = QueueFolder.Claim(folder.Path, @"C:\Modelos\Torre_copia.rvt");
      Assert.NotEqual(original, copy);
      Assert.Equal(copy, QueueFolder.Claim(folder.Path, @"C:\Modelos\Torre_copia.rvt"));
    }

    [Fact]
    public void LookingAtTheHistoryDoesNotTakeTheQueue()
    {
      using var folder = new OutboxTests.TestFolder();
      Assert.Equal(folder.Path, QueueFolder.Find(folder.Path, @"C:\Modelos\Copia.rvt"));
      QueueFolder.Claim(folder.Path, Original);
      Assert.NotEqual(folder.Path, QueueFolder.Find(folder.Path, @"C:\Modelos\Copia.rvt"));
    }

    [Fact]
    public void SaveAsTakesTheQueueWithIt()
    {
      using var folder = new OutboxTests.TestFolder();
      QueueFolder.Claim(folder.Path, Original);
      QueueFolder.Relocate(folder.Path, @"C:\Modelos\Torre_v2.rvt");
      Assert.Equal(folder.Path, QueueFolder.Claim(folder.Path, @"C:\Modelos\Torre_v2.rvt"));
      Assert.NotEqual(folder.Path, QueueFolder.Claim(folder.Path, Original));
    }

    // Caso 16: what an older copy lacks is not something its person deleted. In the original's queue it would be a removal.
    [Fact]
    public void AnOlderCopyDoesNotRemoveWhatTheOriginalPublished()
    {
      using var folder = new OutboxTests.TestFolder();
      using (var original = new SqliteOutbox(QueueFolder.Claim(folder.Path, Original)))
      {
        original.Save(Snapshot("W1", "W2"));
        original.Complete(original.Prepare("s", 1, null, true));
        original.Save(Snapshot("W1"));
        Assert.True(Removes(original, "W2"));
      }
      using var copy = new SqliteOutbox(QueueFolder.Claim(folder.Path, @"C:\Modelos\Copia_atrasada.rvt"));
      copy.Save(Snapshot("W1"));
      Assert.False(Removes(copy, "W2"));
    }
  }
}
