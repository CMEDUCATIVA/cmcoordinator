using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenProject.Revit.LiveSync.Transport;

namespace OpenProject.Revit.LiveSync.Queue
{
  // UI-8 logic 8, second part: the CDE version each element of this Revit starts from, what the CDE refused (or would) and
  // what Administración forces. The geometry (the meshes) and the parameters (the rest of the element) are versioned apart,
  // so two people changing different aspects of one element both enter.
  public sealed partial class SqliteOutbox
  {
    private void EnsureVersions() => Execute(@"
      CREATE TABLE IF NOT EXISTS known (id TEXT PRIMARY KEY,vg INTEGER NOT NULL,vp INTEGER NOT NULL);
      CREATE TABLE IF NOT EXISTS baseline_aspects (id TEXT PRIMARY KEY,g TEXT NOT NULL,p TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS differs (id TEXT PRIMARY KEY,name TEXT,reason TEXT,who TEXT,at TEXT,partial INTEGER NOT NULL DEFAULT 0);
      CREATE TABLE IF NOT EXISTS forced (id TEXT PRIMARY KEY);
      CREATE TABLE IF NOT EXISTS joins (id TEXT PRIMARY KEY,json TEXT NOT NULL);");

    private void EnsureVersionColumns()
    {
      EnsureColumn("known", "n", "INTEGER");
      foreach (var (name, type) in new[] { ("hash", "TEXT"), ("aspects", "TEXT"), ("mine", "INTEGER"), ("theirs", "INTEGER") }) EnsureColumn("differs", name, type);
    }
    private void EnsureColumn(string table, string column, string type)
    {
      using (var cmd = Command($"SELECT count(*) FROM pragma_table_info('{table}') WHERE name=$name", ("$name", column)))
        if (Convert.ToInt32(cmd.ExecuteScalar()) > 0) return;
      Execute($"ALTER TABLE {table} ADD COLUMN {column} {type}");
    }

    // Parameters a newer Revit adds to the same element: they say nothing about whether it changed.
    private static readonly HashSet<string> VersionOnlyProperties = new(StringComparer.Ordinal) { "Alojado" };

    /// <summary>
    /// The huella of an element, the same in every Revit year: Revit 2026 and 2027 triangulate the same wall differently,
    /// rename parameters and add decimals, so the bytes of the capture never match. It reads the box and the centre of the
    /// vertices (1 mm and 1 cm) and the values of the properties, without their names. Its rounding hides what is smaller.
    /// </summary>
    public static string SemanticPrint(string json)
    {
      var element = JObject.Parse(json);
      var (looks, shape) = ShapeOf(element);
      var values = (element["properties"] as JObject)?.Properties().Where(property => !VersionOnlyProperties.Contains(property.Name))
        .Select(property => PrintValue(property.Value.ToString())).OrderBy(value => value, StringComparer.Ordinal) ?? Enumerable.Empty<string>();
      var identity = element.Value<string>("name") + "|" + element.Value<string>("category") + "|" + looks;
      return SyncEncoding.Hash(identity + "|" + shape + "|" + string.Join("|", values)).Substring(0, FingerprintLength);
    }

    private static (string Looks, string Shape) ShapeOf(JObject element)
    {
      var points = new HashSet<(double, double, double)>();
      var looks = new SortedSet<string>(StringComparer.Ordinal);
      foreach (var mesh in element["meshes"] ?? new JArray())
      {
        looks.Add(mesh["color"] + "/" + mesh["opacity"]?.Value<double>().ToString("R", CultureInfo.InvariantCulture));
        var positions = mesh["positions"]?.Values<double>().ToList() ?? new List<double>();
        for (var i = 0; i + 2 < positions.Count; i += 3) points.Add((Round(positions[i], 3), Round(positions[i + 1], 3), Round(positions[i + 2], 3)));
      }
      var shape = points.Count == 0 ? "" : string.Join(",", new[]
      {
        points.Min(p => p.Item1), points.Min(p => p.Item2), points.Min(p => p.Item3), points.Max(p => p.Item1), points.Max(p => p.Item2), points.Max(p => p.Item3),
        Round(points.Average(p => p.Item1), 2), Round(points.Average(p => p.Item2), 2), Round(points.Average(p => p.Item3), 2)
      }.Select(value => value.ToString("R", CultureInfo.InvariantCulture)));
      return (string.Join(",", looks), shape);
    }

    /// <summary>
    /// The huella of each aspect, the same in every Revit year: the CDE keeps the one of whoever sent that aspect last, so an
    /// element made of two people's changes can still be recognized. What follows the geometry does not count as parameters.
    /// </summary>
    public static (string Geometry, string Parameters) AspectPrints(string json)
    {
      var element = JObject.Parse(json);
      var (looks, shape) = ShapeOf(element);
      var derived = DerivedNames(element);
      var values = (element["properties"] as JObject)?.Properties().Where(property => !VersionOnlyProperties.Contains(property.Name) && !derived.Contains(property.Name))
        .Select(property => PrintValue(property.Value.ToString())).OrderBy(value => value, StringComparer.Ordinal) ?? Enumerable.Empty<string>();
      var identity = element.Value<string>("name") + "|" + element.Value<string>("category");
      return (SyncEncoding.Hash(identity + "|" + looks + "|" + shape).Substring(0, FingerprintLength),
        SyncEncoding.Hash(identity + "|" + string.Join("|", values)).Substring(0, FingerprintLength));
    }

    private static HashSet<string> DerivedNames(JObject element)
    {
      var derived = new HashSet<string>(StringComparer.Ordinal) { "Peso", "Peso exacto", "Área de pintura" };
      foreach (var parameter in ((element["bim"] as JObject)?["parameters"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
        if (parameter.Value<string>("groupId")?.Contains(":dimensions-") == true) derived.Add(parameter.Value<string>("name") ?? "");
      return derived;
    }

    private void SaveJoined(string id, IReadOnlyCollection<string> joined)
    {
      if (joined == null || joined.Count == 0) Execute("DELETE FROM joins WHERE id=$id", ("$id", id));
      else Execute("INSERT OR REPLACE INTO joins VALUES($id,$json)", ("$id", id), ("$json", JsonConvert.SerializeObject(joined)));
    }
    private List<string> JoinedOf(string id)
    {
      using var cmd = Command("SELECT json FROM joins WHERE id=$id", ("$id", id));
      return cmd.ExecuteScalar() is string json ? JsonConvert.DeserializeObject<List<string>>(json) : null;
    }
    // Adding 0.0 turns -0 into 0, so an element at the origin prints the same on both sides.
    private static double Round(double value, int decimals) => Math.Round(value, decimals) + 0.0;
    private static readonly Regex Numeric = new(@"^(-?\d+(?:\.\d+)?)(.*)$", RegexOptions.Compiled | RegexOptions.Singleline);
    private static string PrintValue(string value)
    {
      var match = Numeric.Match(value);
      return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
        ? number.ToString("G5", CultureInfo.InvariantCulture) + match.Groups[2].Value : value;
    }

    /// <summary>The hash kept for an element: its semantic huella, then the start of the exact hash, so any change still shows.</summary>
    public static string ElementHash(string json) => SemanticPrint(json) + SyncEncoding.Hash(json).Substring(0, 64 - FingerprintLength);
    // Whether a CDE huella is this hash. Models published before the semantic huella hold the start of the exact hash.
    private static bool SamePrint(string remote, string hash) =>
      remote == hash.Substring(0, FingerprintLength) || remote == hash.Substring(FingerprintLength, FingerprintLength);

    // Queues written before the semantic huella: same elements, same bytes, new hash. Without this all would look changed.
    private void MigrateElementHashes()
    {
      if (Get("printFormat") == "2") return;
      using var transaction = _db.BeginTransaction();
      foreach (var table in new[] { "desired", "confirmed", "target" })
      {
        var rows = new List<(string Id, string Json, string Old)>();
        using (var cmd = Command($"SELECT id,json,hash FROM {table}"))
        using (var reader = cmd.ExecuteReader()) while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        foreach (var (id, json, old) in rows)
        {
          var hash = ElementHash(json);
          Execute($"UPDATE {table} SET hash=$hash WHERE id=$id", ("$id", id), ("$hash", hash));
          if (table == "desired") Execute("UPDATE baseline SET hash=$hash WHERE id=$id AND hash=$old", ("$id", id), ("$hash", hash), ("$old", old));
        }
      }
      Set("printFormat", "2");
      transaction.Commit();
    }

    /// <summary>Hashes of the geometry and of the parameters of an element's captured JSON.</summary>
    public static (string Geometry, string Parameters) Aspects(string json)
    {
      var element = JObject.Parse(json);
      var meshes = element["meshes"]?.ToString(Formatting.None) ?? "";
      element.Remove("meshes");
      KeepOnlyWhatAPersonEdits(element);
      return (SyncEncoding.Hash(meshes), SyncEncoding.Hash(element.ToString(Formatting.None)));
    }

    // Moving an element changes its volume, length, area and weight and the decimals of others: they follow the geometry,
    // so they do not count as a change of parameters. Otherwise a move would stop someone else's comment on the same element.
    private static void KeepOnlyWhatAPersonEdits(JObject element)
    {
      var derived = new HashSet<string>(StringComparer.Ordinal) { "Peso", "Peso exacto", "Área de pintura" };
      if (element["bim"] is JObject bim)
      {
        bim.Remove("ifcSets");
        if (bim["parameters"] is JArray parameters)
        {
          foreach (var parameter in parameters.OfType<JObject>().ToList())
          {
            if (parameter.Value<string>("groupId")?.Contains(":dimensions-") == true || derived.Contains(parameter.Value<string>("name") ?? ""))
            { derived.Add(parameter.Value<string>("name") ?? ""); parameter.Remove(); continue; }
            parameter.Remove("value"); parameter.Remove("siValue");
          }
        }
      }
      if (element["properties"] is JObject properties)
        foreach (var property in properties.Properties().ToList())
        {
          if (derived.Contains(property.Name)) property.Remove();
          else if (property.Value.Type == JTokenType.String) property.Value = PrintValue(property.Value.ToString());
        }
    }

    // The aspects of the starting point, to tell later which of them this person changed.
    private void RecordBaselineAspects()
    {
      var rows = new List<(string Id, string Json)>();
      using (var cmd = Command("SELECT d.id,d.json FROM desired d JOIN baseline b ON d.id=b.id"))
      using (var reader = cmd.ExecuteReader()) while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
      foreach (var (id, json) in rows)
      {
        var (g, p) = Aspects(json);
        Execute("INSERT OR REPLACE INTO baseline_aspects VALUES($id,$g,$p)", ("$id", id), ("$g", g), ("$p", p));
      }
    }

    /// <summary>
    /// The CDE version each element starts from, fixed the first time it is known: the CDE's version when this Revit's
    /// element (as last sent, or as it was when connecting) is the same as the CDE's; -1 when it already differed, an old
    /// copy whose changes cannot enter. An element that matches the CDE again stops differing. Returns how many were fixed.
    /// </summary>
    public int ResolveKnown(RemoteFingerprints prints)
    {
      if (prints?.Versions == null) return 0;
      var remote = prints.Fingerprints ?? new Dictionary<string, string>();
      var resolved = 0;
      using var transaction = _db.BeginTransaction();
      var pending = new List<(string Id, string Reference, bool Old)>();
      // An old copy (-1) is looked at again: it starts from the CDE's version as soon as it is the same as the CDE's.
      using (var cmd = Command(@"SELECT d.id,COALESCE(c.hash,b.hash,d.hash),k.id IS NOT NULL FROM desired d LEFT JOIN confirmed c ON d.id=c.id
        LEFT JOIN baseline b ON d.id=b.id LEFT JOIN known k ON d.id=k.id WHERE k.id IS NULL OR k.vg=-1"))
      using (var reader = cmd.ExecuteReader()) while (reader.Read()) pending.Add((reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));
      foreach (var (id, reference, old) in pending)
      {
        // An element the CDE has from before it versioned elements is at version 0.
        var hasVersion = prints.Versions.TryGetValue(id, out var version) && version?.Length >= 2;
        if (!hasVersion && !remote.ContainsKey(id)) continue;
        var same = remote.TryGetValue(id, out var print) && SamePrint(print, reference);
        if (old && !same) continue;
        SetKnown(id, same ? (hasVersion ? version[0] ?? 0 : 0) : -1, same ? (hasVersion ? version[1] ?? 0 : 0) : -1, same ? Number(version) : null); resolved++;
        // The change kept out for starting from an old copy can enter now: this copy is the CDE's. It goes with the next send.
        if (old && same && Execute("DELETE FROM differs WHERE id=$id", ("$id", id)) > 0) Set("dirty", "1");
      }
      var matching = new List<string>();
      using (var cmd = Command("SELECT d.id,d.hash FROM differs x JOIN desired d ON x.id=d.id"))
      using (var reader = cmd.ExecuteReader())
        while (reader.Read())
          if (remote.TryGetValue(reader.GetString(0), out var print) && SamePrint(print, reader.GetString(1)))
            matching.Add(reader.GetString(0));
      foreach (var id in matching)
      {
        Execute("DELETE FROM differs WHERE id=$id", ("$id", id));
        if (prints.Versions.TryGetValue(id, out var version) && version?.Length >= 2) SetKnown(id, version[0] ?? 0, version[1] ?? 0, Number(version));
      }
      CatchUp(prints, remote);
      RefreshDiffering(prints, remote);
      RefreshNumbers(prints);
      transaction.Commit();
      return resolved;
    }

    // F4 and F6: this Revit may hold what the CDE has even if the CDE's version went up (a restoration, or someone leaving the
    // same result). Each aspect that is the same starts from the CDE's version; what is different stays where it was.
    private void CatchUp(RemoteFingerprints prints, IReadOnlyDictionary<string, string> remote)
    {
      var behind = new List<(string Id, long G, long P)>();
      using (var cmd = Command("SELECT id,vg,vp FROM known"))
      using (var reader = cmd.ExecuteReader())
        while (reader.Read())
        {
          var id = reader.GetString(0);
          if (prints.Versions.TryGetValue(id, out var version) && version?.Length >= 2 && (reader.GetInt64(1) < (version[0] ?? 0) || reader.GetInt64(2) < (version[1] ?? 0)))
            behind.Add((id, reader.GetInt64(1), reader.GetInt64(2)));
        }
      foreach (var (id, g, p) in behind)
      {
        var version = prints.Versions[id];
        string confirmed = null, desired = null, desiredHash = null, reference = null;
        using (var cmd = Command("SELECT c.json,d.json,d.hash,COALESCE(c.hash,b.hash) FROM desired d LEFT JOIN confirmed c ON d.id=c.id LEFT JOIN baseline b ON d.id=b.id WHERE d.id=$id", ("$id", id)))
        using (var reader = cmd.ExecuteReader())
        {
          if (!reader.Read()) continue;
          confirmed = reader.IsDBNull(0) ? null : reader.GetString(0); desired = reader.GetString(1); desiredHash = reader.GetString(2); reference = reader.IsDBNull(3) ? null : reader.GetString(3);
        }
        // What this Revit started from counts too: a change made on what the CDE has now is not made on anything older.
        var sameWhole = remote.TryGetValue(id, out var print) && (SamePrint(print, desiredHash) || reference != null && SamePrint(print, reference));
        prints.AspectPrints.TryGetValue(id, out var remoteAspects);
        var mine = PrintsHeld(confirmed, desired);
        var sameG = sameWhole || remoteAspects?.Length == 2 && mine.Contains(("g", remoteAspects[0]));
        var sameP = sameWhole || remoteAspects?.Length == 2 && mine.Contains(("p", remoteAspects[1]));
        var newG = sameG ? Math.Max(g, version[0] ?? 0) : g; var newP = sameP ? Math.Max(p, version[1] ?? 0) : p;
        if (newG == g && newP == p) continue;
        SetKnown(id, newG, newP, newG == (version[0] ?? 0) && newP == (version[1] ?? 0) ? Number(version) : KnownNumber(id));
        // What was kept out for starting from an old version can enter now. It goes with the next send.
        if (newG == (version[0] ?? 0) && newP == (version[1] ?? 0) && Execute("DELETE FROM differs WHERE id=$id AND reason IN ('stale','restored','joined')", ("$id", id)) > 0) Set("dirty", "1");
      }
    }

    // What this Revit really holds of an element: its last confirmed state and what it has now.
    private static HashSet<(string, string)> PrintsHeld(string confirmed, string desired)
    {
      var held = new HashSet<(string, string)>();
      foreach (var json in new[] { confirmed, desired })
      {
        if (json == null) continue;
        var (g, p) = AspectPrints(json);
        held.Add(("g", g)); held.Add(("p", p));
      }
      return held;
    }

    // Position 5 of a version entry: the visible number of the element. Servers that do not send it leave it unknown.
    private static long? Number(long?[] version) => version?.Length > 5 ? version[5] : null;

    // The list says what the CDE has now; a removal kept out is over once the CDE no longer has the element either.
    private void RefreshDiffering(RemoteFingerprints prints, IReadOnlyDictionary<string, string> remote)
    {
      var rows = new List<(string Id, bool Local)>();
      using (var cmd = Command("SELECT x.id,d.id IS NOT NULL FROM differs x LEFT JOIN desired d ON x.id=d.id"))
      using (var reader = cmd.ExecuteReader()) while (reader.Read()) rows.Add((reader.GetString(0), reader.GetBoolean(1)));
      foreach (var (id, local) in rows)
      {
        var hasVersion = prints.Versions.TryGetValue(id, out var version) && version?.Length >= 2;
        if (!local && !hasVersion && !remote.ContainsKey(id)) { Execute("DELETE FROM differs WHERE id=$id", ("$id", id)); continue; }
        if (!hasVersion) continue;
        string by = null;
        if (version.Length > 2 && version[2] is { } author) prints.Authors?.TryGetValue(author.ToString(CultureInfo.InvariantCulture), out by);
        string at = null;
        prints.VersionTimes?.TryGetValue(id, out at);
        Execute("UPDATE differs SET theirs=COALESCE($n,theirs),who=COALESCE($by,who),at=COALESCE($at,at) WHERE id=$id", ("$id", id), ("$n", Number(version)), ("$by", by), ("$at", at));
      }
    }

    // A version this Revit already holds gets its number as soon as the CDE sends it.
    private void RefreshNumbers(RemoteFingerprints prints)
    {
      var rows = new List<(string Id, long G, long P)>();
      using (var cmd = Command("SELECT id,vg,vp FROM known"))
      using (var reader = cmd.ExecuteReader()) while (reader.Read()) rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
      foreach (var (id, g, p) in rows)
        if (prints.Versions.TryGetValue(id, out var version) && Number(version) is { } number && (version[0] ?? 0) == g && (version[1] ?? 0) == p)
          Execute("UPDATE known SET n=$n WHERE id=$id AND (n IS NULL OR n<>$n)", ("$id", id), ("$n", number));
    }

    /// <summary>
    /// Contract 1: the elements whose CDE huella is the old one (start of the exact hash) and their semantic huella. Only the
    /// Revit that holds the same bytes can see it, and that proves both describe the same element.
    /// </summary>
    public Dictionary<string, PrintUpgrade> PrintUpgrades(IReadOnlyDictionary<string, string> remote)
    {
      var upgrades = new Dictionary<string, PrintUpgrade>(StringComparer.Ordinal);
      if (remote == null || remote.Count == 0) return upgrades;
      foreach (var table in new[] { "desired", "confirmed", "baseline" })
      {
        using var cmd = Command($"SELECT id,hash FROM {table}");
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
          var id = reader.GetString(0); var hash = reader.GetString(1);
          if (upgrades.ContainsKey(id) || hash.Length < 2 * FingerprintLength || !remote.TryGetValue(id, out var print)) continue;
          var semantic = hash.Substring(0, FingerprintLength);
          if (print != semantic && print == hash.Substring(FingerprintLength, FingerprintLength)) upgrades[id] = new PrintUpgrade { Old = print, New = semantic };
        }
      }
      return upgrades;
    }

    /// <summary>Whether some element of this Revit still starts from an old copy of the CDE's (known -1).</summary>
    public bool HasOldCopies { get { using var cmd = Command("SELECT EXISTS(SELECT 1 FROM known WHERE vg=-1) OR EXISTS(SELECT 1 FROM differs)"); return Convert.ToInt32(cmd.ExecuteScalar()) == 1; } }

    private void SetKnown(string id, long g, long p, long? n = null) =>
      Execute("INSERT OR REPLACE INTO known(id,vg,vp,n) VALUES($id,$g,$p,$n)", ("$id", id), ("$g", g), ("$p", p), ("$n", n));

    /// <summary>What entered takes its new version and stops differing; what stayed out is listed with its reason.</summary>
    public void ApplyCommit(CommitResult result)
    {
      if (result == null) return;
      using var transaction = _db.BeginTransaction();
      ApplyResult(result);
      transaction.Commit();
    }

    private void ApplyResult(CommitResult result)
    {
      if (result == null) return;
      foreach (var pair in result.Versions ?? new Dictionary<string, long[]>())
      {
        if (pair.Value == null || pair.Value.Length < 2) continue;
        long? number = null;
        result.VersionNumbers?.TryGetValue(pair.Key, out number);
        SetKnown(pair.Key, pair.Value[0], pair.Value[1], number);
        Execute("DELETE FROM differs WHERE id=$id; DELETE FROM forced WHERE id=$id", ("$id", pair.Key));
      }
      foreach (var item in result.Rejected ?? new List<DifferingElement>())
      {
        // Only what was really sent counts as attempted: a newer capture may already be waiting in the queue.
        item.Attempt ??= HashOf("target", item.Id) ?? HashOf("desired", item.Id) ?? "";
        item.Name ??= NameIn("target", item.Id) ?? NameIn("confirmed", item.Id);
        item.Mine ??= KnownNumber(item.Id);
        AddDiffering(item);
      }
    }

    private string HashOf(string table, string id) { using var cmd = Command($"SELECT hash FROM {table} WHERE id=$id", ("$id", id)); return cmd.ExecuteScalar() as string; }
    private long? KnownNumber(string id) { using var cmd = Command("SELECT n FROM known WHERE id=$id", ("$id", id)); return cmd.ExecuteScalar() is long n ? n : null; }
    private string NameIn(string table, string id)
    {
      using var cmd = Command($"SELECT json FROM {table} WHERE id=$id", ("$id", id));
      if (cmd.ExecuteScalar() is not string json) return null;
      try { return JObject.Parse(json).Value<string>("name"); } catch (JsonException) { return null; }
    }

    // F3: what stayed out did not reach the CDE, so it is not this Revit's confirmed state. Its row keeps what the CDE really
    // received from here: the change is still a change, its reference is still true and it can be sent when it can enter.
    private void ConfirmTarget(CommitResult result)
    {
      var kept = new HashSet<string>(result?.Rejected?.Select(item => item.Id) ?? Enumerable.Empty<string>());
      var withheld = Get("jobWithheld");
      if (withheld != null) kept.UnionWith(JsonConvert.DeserializeObject<List<string>>(withheld));
      Execute("CREATE TEMP TABLE IF NOT EXISTS kept (id TEXT PRIMARY KEY); DELETE FROM kept;");
      foreach (var id in kept) Execute("INSERT OR IGNORE INTO kept VALUES($id)", ("$id", id));
      Execute(@"DELETE FROM confirmed WHERE id NOT IN (SELECT id FROM kept);
        INSERT OR REPLACE INTO confirmed SELECT * FROM target WHERE id NOT IN (SELECT id FROM kept);
        DELETE FROM target; DELETE FROM chunks;");
      Set("jobWithheld", null);
    }

    // Queues from before F3 hold in `confirmed` changes that stayed out: their reference falls back to the starting point.
    private void RepairWithheld()
    {
      if (Get("withheldFormat") == "1") return;
      using var transaction = _db.BeginTransaction();
      Execute(@"DELETE FROM confirmed WHERE id IN (SELECT id FROM differs WHERE hash IS NULL);
        UPDATE differs SET hash=COALESCE((SELECT d.hash FROM desired d WHERE d.id=differs.id),'') WHERE hash IS NULL;");
      Set("withheldFormat", "1");
      transaction.Commit();
    }

    // An edit undone is no longer a change that stayed out. The huella compares it: an undone move may not repeat every decimal.
    private void ForgetReverted()
    {
      const string reverted = @"(SELECT x.id FROM differs x JOIN desired d ON x.id=d.id LEFT JOIN confirmed c ON d.id=c.id LEFT JOIN baseline b ON d.id=b.id
        WHERE x.hash IS NOT NULL AND substr(d.hash,1,16)=substr(COALESCE(c.hash,b.hash),1,16))";
      Execute($"DELETE FROM forced WHERE id IN {reverted}");
      Execute($"DELETE FROM differs WHERE id IN {reverted}");
    }

    // Prepare found nothing to send: no job is left behind and the queue is at rest.
    private void FinishWithoutJob(string jobId, VersionPlan plan)
    {
      Execute("DELETE FROM target; DELETE FROM chunks; DELETE FROM drawing_target;");
      Set("dirty", "0"); Set("forceReset", null); Set("jobDrawings", null); Set("jobWithheld", null);
      if (Get("drawingsSupported") == "1") Set("drawingsDirty", "0");
      var outside = plan?.Withheld.Count ?? 0;
      if (outside == 0) Execute("DELETE FROM history WHERE id=$id", ("$id", jobId));
      else History(jobId, NoJobState, (outside == 1 ? "1 elemento no entró" : $"{outside:N0} elementos no entraron") + "; no había nada más que enviar.");
    }
    public const string NoJobState = "Sin envío";

    private void AddDiffering(DifferingElement item) => Execute(
      "INSERT OR REPLACE INTO differs(id,name,reason,who,at,partial,hash,aspects,mine,theirs) VALUES($id,$name,$reason,$who,$at,$partial,$hash,$aspects,$mine,$theirs)",
      ("$id", item.Id), ("$name", item.Name), ("$reason", item.Reason ?? "stale"), ("$who", item.By), ("$at", item.At), ("$partial", item.Partial ? 1 : 0),
      ("$hash", item.Attempt ?? ""), ("$aspects", item.Aspects == null ? null : string.Join(",", item.Aspects)), ("$mine", item.Mine), ("$theirs", item.Theirs));

    public int DesiredCount
    {
      get { using var cmd = Command("SELECT COUNT(*) FROM desired"); return Convert.ToInt32(cmd.ExecuteScalar()); }
    }

    public IReadOnlyList<DifferingElement> Differs
    {
      get
      {
        using var cmd = Command("SELECT id,name,reason,who,at,partial,aspects,mine,theirs FROM differs ORDER BY name,id");
        using var reader = cmd.ExecuteReader(); var list = new List<DifferingElement>();
        while (reader.Read()) list.Add(new DifferingElement { Id = reader.GetString(0), Name = reader.IsDBNull(1) ? null : reader.GetString(1),
          Reason = reader.IsDBNull(2) ? "stale" : reader.GetString(2), By = reader.IsDBNull(3) ? null : reader.GetString(3),
          At = reader.IsDBNull(4) ? null : reader.GetString(4), Partial = reader.GetInt32(5) == 1,
          Aspects = reader.IsDBNull(6) ? null : reader.GetString(6).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries),
          Mine = reader.IsDBNull(7) ? null : reader.GetInt64(7), Theirs = reader.IsDBNull(8) ? null : reader.GetInt64(8) });
        return list;
      }
    }


    /// <summary>Administración: the next publication carries these elements with «force» (UI-8 case 17).</summary>
    /// <summary>After this Revit restored the model: what it holds and the restored model lost. Nothing is sent by itself; forcing brings it back.</summary>
    public int ListNotRestored(RemoteFingerprints prints)
    {
      if (prints?.Removed == null || prints.Removed.Count == 0) return 0;
      var gone = new HashSet<string>(prints.Removed);
      var rows = new List<(string Id, string Hash, string Json)>();
      using (var cmd = Command("SELECT d.id,d.hash,d.json FROM desired d LEFT JOIN differs x ON d.id=x.id WHERE x.id IS NULL"))
      using (var reader = cmd.ExecuteReader()) while (reader.Read()) if (gone.Contains(reader.GetString(0))) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
      using var transaction = _db.BeginTransaction();
      foreach (var (id, hash, json) in rows)
      {
        string name = null;
        try { name = JObject.Parse(json).Value<string>("name"); } catch (JsonException) { }
        AddDiffering(new DifferingElement { Id = id, Name = name, Reason = "notRestored", Attempt = hash, Aspects = new[] { "g", "p" } });
      }
      transaction.Commit();
      return rows.Count;
    }

    public int Force(IEnumerable<string> ids)
    {
      using var transaction = _db.BeginTransaction();
      var count = 0;
      foreach (var id in ids ?? Array.Empty<string>()) count += Execute("INSERT OR IGNORE INTO forced VALUES($id)", ("$id", id));
      if (count > 0) Set("dirty", "1");
      transaction.Commit();
      return count;
    }

    /// <summary>Decides, while a merge is prepared, what each element carries and what stays out before it travels.</summary>
    public sealed class VersionPlan
    {
      public IReadOnlyDictionary<string, long?[]> Remote { get; set; }
      public IReadOnlyDictionary<string, string> Authors { get; set; } = new Dictionary<string, string>();
      public IReadOnlyDictionary<string, string> Times { get; set; } = new Dictionary<string, string>();
      public ISet<string> Gone { get; set; } = new HashSet<string>();
      public bool CanForce { get; set; }
      public long? RestoredClock { get; set; }
      internal HashSet<string> Forced { get; } = new();
      internal Dictionary<string, string[]> PartPrints { get; } = new();
      internal Dictionary<string, List<string>> PartJoined { get; } = new();
      // Geometry kept out without travelling: the CDE leaves out with it what is joined to it (F7).
      internal HashSet<string> WithheldGeometry { get; } = new();
      internal string LastAspects { get; set; } = "";
      internal Dictionary<string, object> PartVersions { get; } = new();
      internal List<string> PartForce { get; } = new();
      internal HashSet<string> Emitted { get; } = new();
      internal HashSet<string> Withheld { get; } = new();
      internal int Predicted { get; set; }
    }

    // Already listed, with this same change: it is not sent again and again. A stale change is looked at again whenever the
    // CDE's versions are known, since it may enter now; one the CDE refused for its host or in part waits for a new edit.
    private bool AlreadyOut(VersionPlan plan, string id, string attempt)
    {
      using var cmd = Command("SELECT reason,partial,hash,aspects FROM differs WHERE id=$id", ("$id", id));
      using var reader = cmd.ExecuteReader();
      if (!reader.Read() || reader.IsDBNull(2) || reader.GetString(2) != attempt) return false;
      if (reader.IsDBNull(3) || reader.GetString(3).Split(',').Contains("g")) plan.WithheldGeometry.Add(id);
      if (plan.Remote == null) return true;
      var reason = reader.GetString(0);
      // What is joined to one that is still out waits for it: sending it again would only be refused again.
      if (reason == "joined") return JoinedOf(id)?.Any(other => { using var still = Command("SELECT 1 FROM differs WHERE id=$id", ("$id", other)); return still.ExecuteScalar() != null; }) == true;
      return reader.GetInt32(1) == 1 || reason is not ("stale" or "removed" or "restored");
    }

    // What the element carries ({a, bg, bp}), or null when its change would not enter (it is listed as differing instead).
    private object PlanElement(VersionPlan plan, string id, string json, string hash, out bool forced)
    {
      forced = plan.CanForce && plan.Forced.Contains(id);
      if (!forced && AlreadyOut(plan, id, hash)) { plan.Withheld.Add(id); return null; }
      var (g, p) = Aspects(json);
      (string G, string P)? reference = null;
      using (var cmd = Command("SELECT json FROM confirmed WHERE id=$id", ("$id", id)))
        if (cmd.ExecuteScalar() is string confirmed) reference = Aspects(confirmed);
      if (reference == null)
        using (var cmd = Command("SELECT g,p FROM baseline_aspects WHERE id=$id", ("$id", id)))
        using (var reader = cmd.ExecuteReader()) if (reader.Read()) reference = (reader.GetString(0), reader.GetString(1));
      var changed = reference == null ? "gp" : (g != reference.Value.G ? "g" : "") + (p != reference.Value.P ? "p" : "");
      // Nothing a person edits changed: only what follows the geometry did. Forcing sends the element as it is.
      if (changed.Length == 0) changed = forced ? "gp" : "g";
      long bg = -1, bp = -1; long? mine = null;
      using (var cmd = Command("SELECT vg,vp,n FROM known WHERE id=$id", ("$id", id)))
      using (var reader = cmd.ExecuteReader()) if (reader.Read()) { bg = reader.GetInt64(0); bp = reader.GetInt64(1); mine = reader.IsDBNull(2) ? null : reader.GetInt64(2); }
      if (!forced && Stale(plan, id, changed, bg, bp, mine, json, hash)) return null;
      plan.Emitted.Add(id);
      plan.LastAspects = changed;
      plan.WithheldGeometry.Remove(id);
      return new { a = changed, bg, bp };
    }

    // Case 14: a change from an older version than the CDE's is known before sending; it stays out and is shown.
    private bool Stale(VersionPlan plan, string id, string changed, long bg, long bp, long? mine, string json, string hash)
    {
      if (plan.Remote == null) return false;
      string reason = null; long? author = null; long? theirs = null; var geometryOut = false;
      if (plan.Remote.TryGetValue(id, out var current) && current?.Length >= 2)
      {
        var staleG = changed.Contains("g") && bg < (current[0] ?? 0);
        var staleP = changed.Contains("p") && bp < (current[1] ?? 0);
        if ((staleG || !changed.Contains("g")) && (staleP || !changed.Contains("p")))
        {
          geometryOut = staleG;
          // F3: who changed the aspect that stayed out, and whether a host moved that geometry.
          reason = plan.RestoredClock != null && ((staleG && current[0] == plan.RestoredClock) || (staleP && current[1] == plan.RestoredClock)) ? "restored"
            : staleG && current.Length > 8 && current[8] == 1 ? "host" : "stale";
          author = current.Length > 7 ? (staleG ? current[6] : current[7]) : current.Length > 2 ? current[2] : null;
          author ??= current.Length > 2 ? current[2] : null;
          theirs = Number(current);
        }
      }
      else if (plan.Gone.Contains(id)) reason = "removed";
      if (reason == null) return false;
      string name = null;
      try { name = JObject.Parse(json).Value<string>("name"); } catch (JsonException) { }
      plan.Authors.TryGetValue(author?.ToString() ?? "", out var by);
      plan.Times.TryGetValue(id, out var at);
      var aspects = changed.Select(aspect => aspect.ToString()).ToArray();
      RememberWithheld(id, name, reason, aspects, hash);
      AddDiffering(new DifferingElement { Id = id, Name = name, Reason = reason, By = by, At = at, Attempt = hash, Mine = mine, Theirs = theirs, Aspects = aspects });
      if (geometryOut || reason == "removed") plan.WithheldGeometry.Add(id);
      plan.Withheld.Add(id);
      plan.Predicted++;
      return true;
    }

    // Removals carry the version they start from; one that the CDE changed meanwhile stays out (case 5).
    private bool PlanRemoval(VersionPlan plan, string id, Dictionary<string, long> bases)
    {
      var forced = plan.CanForce && plan.Forced.Contains(id);
      if (!forced && AlreadyOut(plan, id, "")) { plan.Withheld.Add(id); return false; }
      long known = -1;
      using (var cmd = Command("SELECT max(vg,vp) FROM known WHERE id=$id", ("$id", id)))
        if (cmd.ExecuteScalar() is long value) known = value;
      if (plan.Remote != null && plan.Remote.TryGetValue(id, out var current) && current?.Length >= 2 &&
          known < Math.Max(current[0] ?? 0, current[1] ?? 0) && !forced)
      {
        plan.Authors.TryGetValue((current.Length > 2 ? current[2] : null)?.ToString() ?? "", out var by);
        plan.Times.TryGetValue(id, out var at);
        RememberWithheld(id, NameIn("confirmed", id), "stale", new[] { DifferingElement.RemovalAspect }, "");
        AddDiffering(new DifferingElement { Id = id, Name = NameIn("confirmed", id), Reason = "stale", By = by, At = at, Attempt = "",
          Aspects = new[] { DifferingElement.RemovalAspect }, Mine = KnownNumber(id), Theirs = Number(current) });
        plan.Withheld.Add(id);
        plan.Predicted++;
        return false;
      }
      Execute("DELETE FROM differs WHERE id=$id", ("$id", id));
      bases[id] = known;
      return true;
    }


    // 8.J: a change that stays out for the first time will be told to the CDE; the same change, looked at again, is not news.
    private void RememberWithheld(string id, string name, string reason, string[] aspects, string attempt)
    {
      using (var known = Command("SELECT 1 FROM differs WHERE id=$id AND hash=$hash", ("$id", id), ("$hash", attempt)))
        if (known.ExecuteScalar() != null) return;
      var list = PendingWithheld.ToList();
      list.Add(new WithheldReport { Id = id, Name = name, Reason = reason, Aspects = aspects });
      Set("withheldPending", JsonConvert.SerializeObject(list.Skip(Math.Max(0, list.Count - WithheldKept)).ToList()));
    }
    private const int WithheldKept = 1000;

    /// <summary>What stayed out without travelling and the CDE does not know yet.</summary>
    public IReadOnlyList<WithheldReport> PendingWithheld =>
      JsonConvert.DeserializeObject<List<WithheldReport>>(Get("withheldPending") ?? "[]") ?? new List<WithheldReport>();
    public void ClearWithheld(int count) => Set("withheldPending", JsonConvert.SerializeObject(PendingWithheld.Skip(count).ToList()));

    private void LoadForced(VersionPlan plan)
    {
      using var cmd = Command("SELECT id FROM forced");
      using var reader = cmd.ExecuteReader(); while (reader.Read()) plan.Forced.Add(reader.GetString(0));
    }
  }
}
