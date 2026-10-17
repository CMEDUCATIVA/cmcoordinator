using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
        Execute("PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA busy_timeout=3000; PRAGMA max_page_count=131072;");
        Execute(@"CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY,value TEXT);
          CREATE TABLE IF NOT EXISTS desired (id TEXT PRIMARY KEY,json TEXT NOT NULL,hash TEXT NOT NULL);
          CREATE TABLE IF NOT EXISTS confirmed (id TEXT PRIMARY KEY,json TEXT NOT NULL,hash TEXT NOT NULL);
          CREATE TABLE IF NOT EXISTS target (id TEXT PRIMARY KEY,json TEXT NOT NULL,hash TEXT NOT NULL);
          CREATE TABLE IF NOT EXISTS chunks (idx INTEGER PRIMARY KEY,payload BLOB NOT NULL,hash TEXT NOT NULL,acked INTEGER NOT NULL DEFAULT 0);
          CREATE TABLE IF NOT EXISTS history (id TEXT PRIMARY KEY,state TEXT,detail TEXT,time TEXT);");
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
      transaction.Commit();
      _previous = snapshot;
      return changed;
    }

    public SyncJob Active => JsonConvert.DeserializeObject<SyncJob>(Get("job") ?? "null");
    public bool HasPending => Get("dirty") == "1";
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

    public SyncJob Prepare(string sessionId, long startedAt, string baseRevision, bool reset)
    {
      using var transaction = _db.BeginTransaction();
      reset |= Get("forceReset") == "1";
      Execute("DELETE FROM target; INSERT INTO target SELECT * FROM desired; DELETE FROM chunks;");
      var updated = new List<LiveElement>();
      using (var cmd = Command(reset ? "SELECT json FROM target" :
        "SELECT t.json FROM target t LEFT JOIN confirmed c ON t.id=c.id WHERE c.id IS NULL OR c.hash<>t.hash"))
      using (var reader = cmd.ExecuteReader()) while (reader.Read())
        updated.Add(JsonConvert.DeserializeObject<LiveElement>(reader.GetString(0)));
      var removed = new List<string>();
      if (!reset)
      {
        using var cmd = Command("SELECT c.id FROM confirmed c LEFT JOIN target t ON c.id=t.id WHERE t.id IS NULL");
        using var reader = cmd.ExecuteReader(); while (reader.Read()) removed.Add(reader.GetString(0));
      }
      var sequence = long.TryParse(Get("sequence"), out var number) ? number + 1 : 1;
      var packet = new LivePacket { SessionId = sessionId, SessionStartedAt = startedAt, SourceId = Get("source"),
        DocumentId = Get("documentId"), LegacySourceId = Get("legacySourceId"),
        Title = Get("title"), View = Get("view"), Sequence = sequence, BaseSequence = 0,
        Reset = reset, Updated = updated, Removed = removed,
        Bim = JsonConvert.DeserializeObject<Bim.BimContext>(Get("bim") ?? "null") };
      var bytes = Encoding.UTF8.GetBytes(SyncEncoding.Json(packet));
      if (bytes.Length > SyncEncoding.MaxPayloadBytes)
        throw new InvalidOperationException("La revisión excede 32 MB; reduce el alcance de la vista.");
      var job = new SyncJob { JobId = Guid.NewGuid().ToString("N"), SessionId = sessionId, SessionStartedAt = startedAt,
        SourceId = packet.SourceId, DocumentId = packet.DocumentId, LegacySourceId = packet.LegacySourceId, Title = packet.Title, View = packet.View, Sequence = sequence,
        BaseRevision = baseRevision, Reset = reset, ByteLength = bytes.Length,
        ChunkCount = (bytes.Length + SyncEncoding.ChunkBytes - 1) / SyncEncoding.ChunkBytes, PayloadHash = SyncEncoding.Hash(bytes) };
      for (var index = 0; index < job.ChunkCount; index++)
      {
        var part = bytes.Skip(index * SyncEncoding.ChunkBytes).Take(SyncEncoding.ChunkBytes).ToArray();
        Execute("INSERT INTO chunks(idx,payload,hash) VALUES($i,$p,$h)", ("$i", index), ("$p", part), ("$h", SyncEncoding.Hash(part)));
      }
      Set("sequence", sequence.ToString()); Set("job", SyncEncoding.Json(job)); Set("dirty", "0");
      Set("forceReset", null);
      History(job.JobId, "En cola", $"{updated.Count} actualizados · {removed.Count} bajas · {job.ChunkCount} lotes");
      transaction.Commit();
      return job;
    }

    public byte[] Chunk(int index)
    {
      using var cmd = Command("SELECT payload FROM chunks WHERE idx=$i", ("$i", index));
      return (byte[])cmd.ExecuteScalar();
    }

    public void Ack(int index) => Execute("UPDATE chunks SET acked=1 WHERE idx=$i", ("$i", index));
    public void Complete(SyncJob job)
    {
      using var transaction = _db.BeginTransaction();
      Execute("DELETE FROM confirmed; INSERT INTO confirmed SELECT * FROM target; DELETE FROM target; DELETE FROM chunks;");
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
      Execute("DELETE FROM target; DELETE FROM chunks;"); transaction.Commit();
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
    public void Dispose() { _db.Dispose(); _lease.Dispose(); }
  }
}
