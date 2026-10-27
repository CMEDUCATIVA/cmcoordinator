using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;

namespace OpenProject.Revit.LiveSync.Queue
{
  // Owned by one worker. Revit API objects never enter this database or its thread.
  public sealed class SqliteOutbox : IDisposable
  {
    private readonly SqliteConnection _db;
    private readonly FileStream _lease;
    private LiveSnapshot _previous;
    public string DirectoryPath { get; }

    public SqliteOutbox(string directory)
    {
      DirectoryPath = directory;
      Directory.CreateDirectory(directory);
      _lease = new FileStream(Path.Combine(directory, "owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
      try
      {
        _db = new SqliteConnection(new SqliteConnectionStringBuilder
          { DataSource = Path.Combine(directory, "outbox.sqlite"), Pooling = false }.ToString());
        _db.Open();
        Execute("PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA busy_timeout=3000; PRAGMA max_page_count=16777216;");
        Execute(@"CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY,value TEXT);
          CREATE TABLE IF NOT EXISTS desired (id TEXT PRIMARY KEY,json TEXT NOT NULL,hash TEXT NOT NULL);
          CREATE TABLE IF NOT EXISTS confirmed (id TEXT PRIMARY KEY,json TEXT NOT NULL,hash TEXT NOT NULL);
          CREATE TABLE IF NOT EXISTS target (id TEXT PRIMARY KEY,json TEXT NOT NULL,hash TEXT NOT NULL);
          CREATE TABLE IF NOT EXISTS chunks (idx INTEGER PRIMARY KEY,payload BLOB NOT NULL,hash TEXT NOT NULL,acked INTEGER NOT NULL DEFAULT 0);
          CREATE TABLE IF NOT EXISTS history (id TEXT PRIMARY KEY,state TEXT,detail TEXT,time TEXT);
          CREATE TABLE IF NOT EXISTS drawing_desired (id TEXT PRIMARY KEY,json TEXT NOT NULL,hash TEXT NOT NULL);
          CREATE TABLE IF NOT EXISTS drawing_target (id TEXT PRIMARY KEY,json TEXT NOT NULL,hash TEXT NOT NULL);
          CREATE TABLE IF NOT EXISTS drawing_confirmed (id TEXT PRIMARY KEY,json TEXT NOT NULL,hash TEXT NOT NULL);");
      }
      catch { _db?.Dispose(); _lease.Dispose(); throw; }
    }

    public string Get(string key)
    {
      using var cmd = Command("SELECT value FROM meta WHERE key=$key", ("$key", key));
      return cmd.ExecuteScalar() as string;
    }

    private void Set(string key, string value) => Execute(
      "INSERT INTO meta VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$key", key), ("$value", value));

    public bool Save(LiveSnapshot snapshot)
    {
      using var transaction = _db.BeginTransaction();
      var changed = Get("source") != snapshot.SourceId || Get("title") != snapshot.Title || Get("view") != snapshot.View;
      var bimJson = SyncEncoding.Json(snapshot.Bim);
      changed |= Get("bim") != bimJson || Get("documentId") != snapshot.DocumentId || Get("legacySourceId") != snapshot.LegacySourceId;
      var existing = new HashSet<string>();
      using (var cmd = Command("SELECT id FROM desired"))
      using (var reader = cmd.ExecuteReader()) while (reader.Read()) existing.Add(reader.GetString(0));
      foreach (var pair in snapshot.Elements)
      {
        existing.Remove(pair.Key);
        if (_previous?.Elements.TryGetValue(pair.Key, out var old) == true && ReferenceEquals(old, pair.Value)) continue;
        var json = SyncEncoding.Json(pair.Value);
        changed |= Execute(@"INSERT INTO desired VALUES($id,$json,$hash) ON CONFLICT(id) DO UPDATE
          SET json=excluded.json,hash=excluded.hash WHERE desired.hash<>excluded.hash",
          ("$id", pair.Key), ("$json", json), ("$hash", SyncEncoding.Hash(json))) > 0;
      }
      foreach (var id in existing) { Execute("DELETE FROM desired WHERE id=$id", ("$id", id)); changed = true; }
      Set("source", snapshot.SourceId); Set("title", snapshot.Title); Set("view", snapshot.View);
      Set("bim", bimJson); Set("documentId", snapshot.DocumentId); Set("legacySourceId", snapshot.LegacySourceId);
      if (changed) Set("dirty", "1");
      var drawingsChanged = snapshot.Drawings != null && SaveDrawings(snapshot.Drawings);
      if (drawingsChanged) Set("drawingsDirty", "1");
      transaction.Commit();
      _previous = snapshot;
      return changed || drawingsChanged;
    }

    // A snapshot without drawings carries no information about them and leaves the stored ones as they are.
    private bool SaveDrawings(IReadOnlyDictionary<string, Drawings.LiveDrawing> drawings)
    {
      var changed = false;
      var existing = new HashSet<string>();
      using (var cmd = Command("SELECT id FROM drawing_desired"))
      using (var reader = cmd.ExecuteReader()) while (reader.Read()) existing.Add(reader.GetString(0));
      foreach (var pair in drawings)
      {
        existing.Remove(pair.Key);
        if (_previous?.Drawings?.TryGetValue(pair.Key, out var old) == true && ReferenceEquals(old, pair.Value)) continue;
        var json = SyncEncoding.Json(pair.Value);
        changed |= Execute(@"INSERT INTO drawing_desired VALUES($id,$json,$hash) ON CONFLICT(id) DO UPDATE
          SET json=excluded.json,hash=excluded.hash WHERE drawing_desired.hash<>excluded.hash",
          ("$id", pair.Key), ("$json", json), ("$hash", SyncEncoding.Hash(json))) > 0;
      }
      foreach (var id in existing) { Execute("DELETE FROM drawing_desired WHERE id=$id", ("$id", id)); changed = true; }
      return changed;
    }

    // Whether the destination accepts 2D drawings (feature drawings2d). Until it does, drawings wait in the queue
    // and a change that only touches drawings does not start a publication.
    public void SetDrawingsSupported(bool supported)
    { if ((Get("drawingsSupported") == "1") != supported) Set("drawingsSupported", supported ? "1" : null); }
    public bool HasPendingDrawings => Get("drawingsDirty") == "1";
    public int DrawingCount
    {
      get { using var cmd = Command("SELECT count(*) FROM drawing_desired"); return Convert.ToInt32(cmd.ExecuteScalar()); }
    }

    public SyncJob Active => JsonConvert.DeserializeObject<SyncJob>(Get("job") ?? "null");
    public bool HasPending => Get("dirty") == "1" || Get("drawingsSupported") == "1" && HasPendingDrawings;
    public int PendingElements
    {
      get
      {
        if (!HasPending) return 0;
        var table = Active == null ? "confirmed" : "target";
        using var cmd = Command($@"SELECT
          (SELECT count(*) FROM desired d LEFT JOIN {table} t ON d.id=t.id WHERE t.id IS NULL OR t.hash<>d.hash) +
          (SELECT count(*) FROM {table} t LEFT JOIN desired d ON t.id=d.id WHERE d.id IS NULL)");
        return Convert.ToInt32(cmd.ExecuteScalar());
      }
    }

    public SyncJob Prepare(string sessionId, long startedAt, string baseRevision, bool reset,
      Action<int, int, long> progress = null, bool compact = false, bool drawings = false)
    {
      var jobId = Guid.NewGuid().ToString("N");
      History(jobId, "Preparando paquetes", "Agrupando la revisión sin cargar el modelo completo en memoria");
      using var transaction = _db.BeginTransaction();
      reset |= Get("forceReset") == "1";
      Execute("DELETE FROM target; INSERT INTO target SELECT * FROM desired; DELETE FROM chunks;");
      var removed = new List<string>();
      if (!reset)
      {
        using var removedCommand = Command("SELECT c.id FROM confirmed c LEFT JOIN target t ON c.id=t.id WHERE t.id IS NULL");
        using var removedReader = removedCommand.ExecuteReader();
        while (removedReader.Read()) removed.Add(removedReader.GetString(0));
      }
      var sequence = long.TryParse(Get("sequence"), out var number) ? number + 1 : 1;
      var job = new SyncJob { JobId = jobId, SessionId = sessionId, SessionStartedAt = startedAt,
        SourceId = Get("source"), DocumentId = Get("documentId"), LegacySourceId = Get("legacySourceId"),
        Title = Get("title"), View = Get("view"), Sequence = sequence,
        BaseRevision = baseRevision, Reset = reset };
      using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
      var updatedSql = reset ? "SELECT json FROM target ORDER BY id" :
        "SELECT t.json FROM target t LEFT JOIN confirmed c ON t.id=c.id WHERE c.id IS NULL OR c.hash<>t.hash ORDER BY t.id";
      var updatedCountSql = reset ? "SELECT count(*) FROM target" :
        "SELECT count(*) FROM target t LEFT JOIN confirmed c ON t.id=c.id WHERE c.id IS NULL OR c.hash<>t.hash";
      using var countCommand = Command(updatedCountSql);
      var updatedTotal = Convert.ToInt32(countCommand.ExecuteScalar());
      var preparationTotal = updatedTotal + removed.Count;
      long totalBytes = 0; var partIndex = 0; var updatedCount = 0;
      void Store(string json)
      {
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > SyncEncoding.MaxChunkBytes)
          throw new InvalidOperationException("Un paquete individual excede 32 MiB; reduce la complejidad de ese elemento.");
        digest.AppendData(bytes); totalBytes += bytes.LongLength;
        Execute("INSERT INTO chunks(idx,payload,hash) VALUES($i,$p,$h)",
          ("$i", partIndex++), ("$p", bytes), ("$h", SyncEncoding.Hash(bytes)));
      }
      Store("{\"protocol\":3,\"kind\":\"catalog\",\"bim\":" + (Get("bim") ?? "null") + "}");
      progress?.Invoke(0, preparationTotal, totalBytes);
      const string prefix = "{\"protocol\":3,\"kind\":\"elements\",\"updated\":[";
      const string tableKey = "],\"geometries\":";
      var compactor = compact ? new PartCompactor() : null;
      var frame = Encoding.UTF8.GetByteCount(prefix) + 2 + (compact ? tableKey.Length : 0);
      var builder = new StringBuilder(prefix); var inPart = 0; var partBytes = frame;
      // The opening element's new shapes leave this part's table; every shape it uses goes to the next part.
      void Flush(IReadOnlyList<string> introduced, IReadOnlyList<string> nextPartShapes)
      {
        builder.Append(compactor == null ? "]}" : tableKey + compactor.Table(introduced) + "}");
        Store(builder.ToString()); builder.Clear().Append(prefix); inPart = 0; partBytes = frame;
        compactor?.KeepOnly(nextPartShapes ?? Array.Empty<string>());
      }
      using (var command = Command(updatedSql))
      using (var reader = command.ExecuteReader()) while (reader.Read())
      {
        var json = reader.GetString(0);
        IReadOnlyList<string> fresh = Array.Empty<string>(), used = Array.Empty<string>();
        if (compactor != null) json = compactor.Compact(json, out fresh, out used);
        var jsonBytes = Encoding.UTF8.GetByteCount(json) + (inPart > 0 ? 1 : 0);
        // Shapes first seen in this element move with it if it opens the next part.
        if (inPart > 0 && (partBytes + jsonBytes + (compactor?.Bytes ?? 0) > SyncEncoding.ChunkBytes || inPart >= Capture.CaptureLimits.PartElements))
        { Flush(fresh, used); jsonBytes -= 1; }
        if (inPart++ > 0) builder.Append(',');
        builder.Append(json); partBytes += jsonBytes; updatedCount++;
        if (updatedCount == updatedTotal || updatedCount % 25 == 0)
          progress?.Invoke(updatedCount, preparationTotal, totalBytes + partBytes + (compactor?.Bytes ?? 0));
      }
      if (inPart > 0) Flush(null, null);
      const int removedPerPart = 20000;
      for (var offset = 0; offset < removed.Count; offset += removedPerPart)
      {
        Store("{\"protocol\":3,\"kind\":\"removed\",\"removed\":" +
          JsonConvert.SerializeObject(removed.Skip(offset).Take(removedPerPart), LiveJson.Settings) + "}");
        progress?.Invoke(updatedCount + Math.Min(offset + removedPerPart, removed.Count), preparationTotal, totalBytes);
      }
      var drawingCount = drawings ? StoreDrawings(reset, Store) : 0;
      progress?.Invoke(preparationTotal, preparationTotal, totalBytes);
      job.ByteLength = totalBytes; job.ChunkCount = partIndex;
      job.PayloadHash = NetCompat.Hex(digest.GetHashAndReset());
      Set("sequence", sequence.ToString()); Set("job", SyncEncoding.Json(job)); Set("dirty", "0");
      Set("forceReset", null);
      Set("jobDrawings", drawings ? "1" : null);
      if (drawings) Set("drawingsDirty", "0");
      History(job.JobId, "En cola", $"{updatedCount} actualizados · {removed.Count} bajas · " +
        (drawings ? $"{drawingCount} vistas 2D · " : "") + $"{job.ChunkCount} paquetes · {FormatBytes(job.ByteLength)}");
      transaction.Commit();
      return job;
    }

    // Drawings travel in the same revision as the 3D model, in parts no bigger than those of the elements.
    private int StoreDrawings(bool reset, Action<string> store)
    {
      Execute("DELETE FROM drawing_target; INSERT INTO drawing_target SELECT * FROM drawing_desired;");
      var removed = new List<string>();
      if (!reset)
      {
        using var removedCommand = Command("SELECT c.id FROM drawing_confirmed c LEFT JOIN drawing_target t ON c.id=t.id WHERE t.id IS NULL ORDER BY c.id");
        using var removedReader = removedCommand.ExecuteReader();
        while (removedReader.Read()) removed.Add(removedReader.GetString(0));
      }
      var updatedSql = reset ? "SELECT json FROM drawing_target ORDER BY id" :
        "SELECT t.json FROM drawing_target t LEFT JOIN drawing_confirmed c ON t.id=c.id WHERE c.id IS NULL OR c.hash<>t.hash ORDER BY t.id";
      const string prefix = "{\"protocol\":3,\"kind\":\"drawings\",\"updated\":[";
      var builder = new StringBuilder(prefix);
      var bytes = Encoding.UTF8.GetByteCount(prefix);
      int count = 0, inPart = 0;
      void Close(IReadOnlyList<string> gone)
      {
        store(builder.Append("],\"removed\":").Append(JsonConvert.SerializeObject(gone, LiveJson.Settings)).Append('}').ToString());
        builder.Clear().Append(prefix); bytes = Encoding.UTF8.GetByteCount(prefix); inPart = 0;
      }
      using (var command = Command(updatedSql))
      using (var reader = command.ExecuteReader()) while (reader.Read())
      {
        var json = reader.GetString(0);
        var size = Encoding.UTF8.GetByteCount(json) + 1;
        if (inPart > 0 && bytes + size > SyncEncoding.ChunkBytes) Close(Array.Empty<string>());
        if (inPart++ > 0) builder.Append(',');
        builder.Append(json); bytes += size; count++;
      }
      if (inPart > 0 || removed.Count > 0) Close(removed);
      return count;
    }

    public byte[] Chunk(int index)
    {
      using var cmd = Command("SELECT payload FROM chunks WHERE idx=$i", ("$i", index));
      return (byte[])cmd.ExecuteScalar();
    }

    public void Ack(int index) => Execute("UPDATE chunks SET acked=1 WHERE idx=$i", ("$i", index));
    public long AcknowledgedBytes
    {
      get
      {
        using var cmd = Command("SELECT COALESCE(sum(length(payload)),0) FROM chunks WHERE acked=1");
        return Convert.ToInt64(cmd.ExecuteScalar());
      }
    }
    public void Complete(SyncJob job)
    {
      using var transaction = _db.BeginTransaction();
      Execute("DELETE FROM confirmed; INSERT INTO confirmed SELECT * FROM target; DELETE FROM target; DELETE FROM chunks;");
      if (Get("jobDrawings") == "1")
        Execute("DELETE FROM drawing_confirmed; INSERT INTO drawing_confirmed SELECT * FROM drawing_target; DELETE FROM drawing_target;");
      Set("jobDrawings", null);
      Set("revision", job.JobId); Set("session", job.SessionId); Set("job", null);
      History(job.JobId, "Guardado en destino", "Esperando confirmación de presentación del visor");
      transaction.Commit();
    }

    public void Abandon(string reason)
    {
      using var transaction = _db.BeginTransaction();
      var active = Active;
      if (active != null) History(active.JobId, "Reconciliado", reason);
      Set("job", null); Set("dirty", "1");
      Set("forceReset", "1");
      if (Get("jobDrawings") == "1") Set("drawingsDirty", "1");
      Set("jobDrawings", null);
      Execute("DELETE FROM target; DELETE FROM chunks; DELETE FROM drawing_target;"); transaction.Commit();
    }

    public void History(string id, string state, string detail) => Execute(@"INSERT INTO history VALUES($id,$s,$d,$t)
      ON CONFLICT(id) DO UPDATE SET state=excluded.state,detail=excluded.detail,time=excluded.time",
      ("$id", id), ("$s", state), ("$d", detail), ("$t", DateTime.UtcNow.ToString("O")));

    public List<SyncJobInfo> History()
    {
      Execute("DELETE FROM history WHERE id NOT IN (SELECT id FROM history ORDER BY time DESC LIMIT 100)");
      using var cmd = Command("SELECT id,state,detail,time FROM history ORDER BY time DESC LIMIT 20");
      using var reader = cmd.ExecuteReader(); var result = new List<SyncJobInfo>();
      while (reader.Read()) result.Add(new SyncJobInfo { Id = reader.GetString(0), State = reader.GetString(1),
        Detail = reader.GetString(2), Time = DateTime.Parse(reader.GetString(3)).ToLocalTime().ToString("HH:mm:ss") });
      return result;
    }

    private SqliteCommand Command(string sql, params (string Key, object Value)[] values)
    {
      var cmd = _db.CreateCommand(); cmd.CommandText = sql;
      foreach (var pair in values) cmd.Parameters.AddWithValue(pair.Key, pair.Value ?? DBNull.Value);
      return cmd;
    }
    private int Execute(string sql, params (string Key, object Value)[] values)
    { using var cmd = Command(sql, values); return cmd.ExecuteNonQuery(); }
    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024
      ? $"{bytes / (1024d * 1024 * 1024):0.00} GiB" : $"{bytes / (1024d * 1024):0.00} MiB";
    public void Dispose() { _db.Dispose(); _lease.Dispose(); }
  }
}
