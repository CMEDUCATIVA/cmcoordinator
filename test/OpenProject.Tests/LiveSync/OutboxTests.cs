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
        Assert.Contains("\"b\"", Encoding.UTF8.GetString(box.Chunk(1)));
        box.Ack(0);
      }
      using (var box = new SqliteOutbox(folder.Path))
      {
        Assert.Equal(firstId, box.Active.JobId);
        box.Complete(box.Active);
        Assert.True(box.HasPending);
        var delta = box.Prepare("session", 10, firstId, false);
        var parts = Enumerable.Range(0, delta.ChunkCount).Select(i => JObject.Parse(Encoding.UTF8.GetString(box.Chunk(i)))).ToList();
        var json = parts.First(part => (string)part["kind"] == "elements");
        var removed = parts.First(part => (string)part["kind"] == "removed");
        Assert.False(delta.Reset);
        Assert.Equal("b", removed["removed"][0].ToString());
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
      var elementPart = Enumerable.Range(0, job.ChunkCount).Select(i => JObject.Parse(Encoding.UTF8.GetString(box.Chunk(i))))
        .First(part => (string)part["kind"] == "elements");
      Assert.Equal(element.Properties["Dato0"], elementPart["updated"][0]["properties"]["Dato0"].ToString());
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
    [Fact]
    public void RevisionBeyondLegacyLimitIsStoredAsIndependentResumablePackages()
    {
      using var folder = new TestFolder();
      using var box = new SqliteOutbox(folder.Path);
      var elements = Enumerable.Range(0, 6).ToDictionary(i => "large-" + i, i => new LiveElement {
        Id = "large-" + i, ElementId = i.ToString(), Name = "Elemento " + i, Category = "Demo",
        Properties = new Dictionary<string,string> { ["payload"] = new string((char)('a' + i), 6 * 1024 * 1024) }
      });
      box.Save(new LiveSnapshot("large-model", "Modelo grande", "3D", elements));
      var job = box.Prepare("session", 10, null, true);
      Assert.Equal(3, job.Protocol);
      Assert.True(job.ByteLength > SyncEncoding.MaxPayloadBytes);
      Assert.True(job.ChunkCount > 2);
      for (var index = 0; index < job.ChunkCount; index++)
      {
        var bytes = box.Chunk(index);
        Assert.InRange(bytes.Length, 1, SyncEncoding.MaxChunkBytes);
        Assert.Equal(3, JObject.Parse(Encoding.UTF8.GetString(bytes))["protocol"].Value<int>());
      }
    }
    [Fact]
    public void PreparingRevisionReportsElementProgress()
    {
      using var folder = new TestFolder();
      using var box = new SqliteOutbox(folder.Path);
      box.Save(Snapshot("one", "two"));
      var reports = new List<(int Done, int Total, long Bytes)>();
      var job = box.Prepare("session", 10, null, true,
        (done, total, bytes) => reports.Add((done, total, bytes)));
      Assert.NotEmpty(reports);
      Assert.Equal((2, 2), (reports[^1].Done, reports[^1].Total));
      Assert.Equal(job.ByteLength, reports[^1].Bytes);
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
