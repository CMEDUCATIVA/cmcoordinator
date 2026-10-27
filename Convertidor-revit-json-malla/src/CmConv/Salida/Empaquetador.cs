using OpenProject.Revit.LiveSync;
using OpenProject.Revit.LiveSync.Queue;

namespace CmConv.Salida
{
  /// <summary>
  /// Writes a snapshot as a protocol-3 revision with Cmcoordinator's own outbox: the same parts, shared geometry,
  /// manifest and hash the add-in uploads, so the CDE receiver accepts them unchanged.
  /// Layout: manifest.json + parts/000000.json … (uncompressed; gzip is applied only on the wire).
  /// </summary>
  public static class Empaquetador
  {
    public static SyncJob Write(LiveSnapshot snapshot, string outputDirectory, string workDirectory)
    {
      var queue = Path.Combine(workDirectory, "cola");
      if (Directory.Exists(queue)) Directory.Delete(queue, true);
      var parts = Path.Combine(outputDirectory, "parts");
      if (Directory.Exists(parts)) Directory.Delete(parts, true);
      Directory.CreateDirectory(parts);
      SyncJob job;
      using (var outbox = new SqliteOutbox(queue))
      {
        outbox.Save(snapshot);
        var started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        job = outbox.Prepare("convertidor-" + Guid.NewGuid().ToString("N")[..12], started, null, reset: true, compact: true);
        for (var i = 0; i < job.ChunkCount; i++)
          File.WriteAllBytes(Path.Combine(parts, i.ToString("D6") + ".json"), outbox.Chunk(i));
      }
      File.WriteAllText(Path.Combine(outputDirectory, "manifest.json"), SyncEncoding.Json(job));
      Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
      Directory.Delete(queue, true);
      return job;
    }
  }
}
