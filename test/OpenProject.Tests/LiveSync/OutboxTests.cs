using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using OpenProject.Revit.LiveSync;
using OpenProject.Revit.LiveSync.Queue;
using Xunit;

namespace OpenProject.Tests.LiveSync
{
  public class OutboxTests
  {
    private static LiveSnapshot Snapshot(params string[] ids) => new("model", "Modelo", "3D",
      ids.ToDictionary(id => id, id => new LiveElement { Id = id, ElementId = id, Name = id, Category = "Demo" }));
    [Fact]
    public void ActiveTargetIsImmutableWhileNewChangesPersistAndDeletedIdsSurviveRecovery()
    {
      using var folder = new TestFolder();
      string firstId;
      using (var box = new SqliteOutbox(folder.Path))
      {
        box.Save(Snapshot("a", "b"));
        var first = box.Prepare("session", 10, null, true); firstId = first.JobId;
        box.Save(Snapshot("a", "c"));
        Assert.Contains("\"b\"", Encoding.UTF8.GetString(box.Chunk(0)));
        box.Ack(0);
      }
      using (var box = new SqliteOutbox(folder.Path))
      {
        Assert.Equal(firstId, box.Active.JobId);
        box.Complete(box.Active);
        Assert.True(box.HasPending);
        var delta = box.Prepare("session", 10, firstId, false);
        var json = JObject.Parse(Encoding.UTF8.GetString(box.Chunk(0)));
        Assert.False(delta.Reset);
        Assert.Equal("b", json["removed"][0].ToString());
        Assert.Equal("c", json["updated"][0]["id"].ToString());
        box.Complete(delta); Assert.False(box.HasPending);
      }
    }
    [Fact]
    public void ByteChunksPreserveUnicodeAndHashesAcrossRestart()
    {
      using var folder = new TestFolder();
      var element = new LiveElement { Id = "one", ElementId = "1", Name = "Cubo", Category = "Demo",
        Properties = Enumerable.Range(0, 256).ToDictionary(i => "Dato" + i, _ => new string('\u00f1', 2048)) };
      var snapshot = new LiveSnapshot("doc", "Model", "3D", new Dictionary<string, LiveElement> { [element.Id] = element });
      using var box = new SqliteOutbox(folder.Path);
      box.Save(snapshot); var job = box.Prepare("session", 10, null, true);
      using var stream = new MemoryStream();
      for (var i = 0; i < job.ChunkCount; i++) { var bytes = box.Chunk(i); Assert.True(bytes.Length <= SyncEncoding.ChunkBytes); stream.Write(bytes); }
      Assert.True(job.ChunkCount > 1); Assert.Equal(job.PayloadHash, SyncEncoding.Hash(stream.ToArray()));
      Assert.Equal(element.Properties["Dato0"], JObject.Parse(Encoding.UTF8.GetString(stream.ToArray()))["updated"][0]["properties"]["Dato0"].ToString());
    }
    [Fact]
    public void IdenticalCaptureDoesNotCreateAnotherDirtyRevisionAndLeaseRejectsSecondWriter()
    {
      using var folder = new TestFolder();
      using var box = new SqliteOutbox(folder.Path);
      Assert.True(box.Save(Snapshot("one")));
      var job = box.Prepare("session", 10, null, true); box.Complete(job);
      Assert.False(box.Save(Snapshot("one"))); Assert.False(box.HasPending);
      Assert.Throws<IOException>(() => new SqliteOutbox(folder.Path));
    }
    internal sealed class TestFolder : IDisposable
    {
      public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cmp-sync-test-" + Guid.NewGuid().ToString("N"));
      public void Dispose()
      {
        if (!Directory.Exists(Path)) return;
        var full = System.IO.Path.GetFullPath(Path);
        var parent = System.IO.Path.GetDirectoryName(full);
        var expected = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        if (!string.Equals(parent, expected, StringComparison.OrdinalIgnoreCase) || !System.IO.Path.GetFileName(full).StartsWith("cmp-sync-test-", StringComparison.Ordinal))
          throw new InvalidOperationException("La carpeta de limpieza no pertenece a esta prueba.");
        Directory.Delete(full, recursive: true);
      }
    }
  }
}
