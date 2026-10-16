using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace OpenProject.Revit.LiveSync.Queue
{
  public sealed class SyncJob
  {
    public int Protocol { get; set; } = 2;
    public string JobId { get; set; }
    public string SessionId { get; set; }
    public long SessionStartedAt { get; set; }
    public string SourceId { get; set; }
    public string Title { get; set; }
    public string View { get; set; }
    public string BaseRevision { get; set; }
    public bool Reset { get; set; }
    public long Sequence { get; set; }
    public int ByteLength { get; set; }
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
    public bool Paused { get; init; }
    public bool Attention { get; init; }
    public int PendingElements { get; init; }
    public bool HasPendingChanges { get; init; }
    public string ReceiverRevision { get; init; }
    public string ViewerRevision { get; init; }
    public IReadOnlyList<SyncJobInfo> Jobs { get; init; } = Array.Empty<SyncJobInfo>();
  }

  public static class SyncEncoding
  {
    public const int ChunkBytes = 512 * 1024;
    public const int MaxPayloadBytes = 32 * 1024 * 1024;
    public static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    public static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));
    public static string Json(object value) => JsonConvert.SerializeObject(value, LiveJson.Settings);
  }
}
