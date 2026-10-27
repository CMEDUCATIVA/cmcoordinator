using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace OpenProject.Revit.LiveSync.Queue
{
  public sealed class SyncJob
  {
    public int Protocol { get; set; } = 3;
    public string JobId { get; set; }
    public string SessionId { get; set; }
    public long SessionStartedAt { get; set; }
    public string SourceId { get; set; }
    public string DocumentId { get; set; }
    public string LegacySourceId { get; set; }
    public string Title { get; set; }
    public string View { get; set; }
    public string BaseRevision { get; set; }
    public bool Reset { get; set; }
    public long Sequence { get; set; }
    public long ByteLength { get; set; }
    public int ChunkCount { get; set; }
    public string PayloadHash { get; set; }
  }

  public sealed class SyncJobInfo
  {
    public string Id { get; set; }
    public string State { get; set; }
    public string Detail { get; set; }
    public string Time { get; set; }
  }

  public sealed class SyncProgress
  {
    public string Stage { get; init; } = "Preparando cola local";
    public string Detail { get; init; } = "Esperando la captura del modelo";
    public string JobId { get; init; }
    public int Done { get; init; }
    public int Total { get; init; }
    public int Percent => Total == 0 ? 0 : (int)(100L * Done / Total);
    public long BytesDone { get; init; }
    public long BytesTotal { get; init; }
    public bool Paused { get; init; }
    public bool Attention { get; init; }
    public int PendingElements { get; init; }
    public bool HasPendingChanges { get; init; }
    // 2D drawings kept in the queue, whether some still have to reach the CDE, and whether the CDE accepts them.
    public int Drawings { get; init; }
    public bool DrawingsPending { get; init; }
    public bool DrawingsSupported { get; init; }
    public string ReceiverRevision { get; init; }
    public string ViewerRevision { get; init; }
    public IReadOnlyList<SyncJobInfo> Jobs { get; init; } = Array.Empty<SyncJobInfo>();
  }

  public static class SyncEncoding
  {
    public const int ChunkBytes = 8 * 1024 * 1024;
    public const int MaxChunkBytes = 32 * 1024 * 1024;
    public const int MaxPayloadBytes = 32 * 1024 * 1024;
    public static string Hash(byte[] value) => NetCompat.Hex(NetCompat.Sha256(value));
    public static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));
    public static string Json(object value) => JsonConvert.SerializeObject(value, LiveJson.Settings);
    public static byte[] Gzip(byte[] value)
    {
      using var output = new System.IO.MemoryStream();
      using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        gzip.Write(value, 0, value.Length);
      return output.ToArray();
    }
  }
}
