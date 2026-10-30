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
    // Merge mode: adds and updates this capture's elements and removes only those deleted in Revit; never replaces.
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public bool? Merge { get; set; }
    // UI-8 logic 9: hash of the BIM catalog of this capture; a merge leaves the catalog out when the CDE has the same one.
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string CatalogHash { get; set; }
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

  /// <summary>What «Reemplazar modelo…» would change in the CDE, compared by huellas with the last local capture.</summary>
  public sealed class ReplacePreview
  {
    public int RemoteCount { get; init; }
    public string LastPublishedBy { get; init; }
    public DateTimeOffset? LastPublishedAt { get; init; }
    // False when there is no local capture yet or the CDE lists no huellas: only the size of the model is known.
    public bool Compared { get; init; }
    // False when part of the CDE model has no huellas (published by an older Cmcoordinator): the counts are approximate.
    public bool Exact { get; init; }
    public int Added { get; init; }
    public int Changed { get; init; }
    public int Removed { get; init; }
    // Local capture size and how many of its elements are new or different in the CDE (UI-8 #24 and #29).
    public int Local { get; init; }
    public int Unchanged => Math.Max(0, Local - Added - Changed);
    public bool Matches => Compared && Added == 0 && Changed == 0 && Removed == 0;
    // A first merge in which nearly everything differs would send the whole model: it is confirmed first (UI-8 #29).
    public bool MostlyDifferent => Compared && Local >= MostlyDifferentMinimum && Added + Changed >= Local * MostlyDifferentShare;
    public const int MostlyDifferentMinimum = 50;
    public const double MostlyDifferentShare = 0.8;
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
    // Group of this connection in the CDE model, as last read from the receiver.
    public Transport.CdeModelAccess Access { get; init; } = Transport.CdeModelAccess.Unknown;
    // Place in the CDE line while waiting for the turn (0 when unknown or not waiting).
    public int QueuePosition { get; init; }
    // The CDE no longer accepts this connection: a new link and token are needed.
    public bool Revoked { get; init; }
    // UI-8 logic 4: the role lost «Ver modelos IFC»; it resumes by itself when the permission comes back.
    public bool NoPermission { get; init; }
    // What the last replacement of the model left to restore, as last read from the receiver.
    public Transport.RestorePointInfo RestorePoint { get; init; }
    // What this Revit's capture is against the CDE, read once when connecting (UI-8 #24); null until known.
    public ReplacePreview Comparison { get; init; }
    // This connection has stored at least one revision in the CDE: it is part of the live model (UI-8 #26).
    public bool Joined { get; init; }
    // The first merge would send nearly the whole model: it waits for the person to confirm (UI-8 #29).
    public bool NeedsConfirmation { get; init; }
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
