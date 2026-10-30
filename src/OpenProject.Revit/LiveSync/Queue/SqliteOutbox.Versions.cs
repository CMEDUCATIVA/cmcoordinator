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
      CREATE TABLE IF NOT EXISTS forced (id TEXT PRIMARY KEY);");

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
      var values = (element["properties"] as JObject)?.Properties().Where(property => !VersionOnlyProperties.Contains(property.Name))
        .Select(property => PrintValue(property.Value.ToString())).OrderBy(value => value, StringComparer.Ordinal) ?? Enumerable.Empty<string>();
      var identity = element.Value<string>("name") + "|" + element.Value<string>("category") + "|" + string.Join(",", looks);
      return SyncEncoding.Hash(identity + "|" + shape + "|" + string.Join("|", values)).Substring(0, FingerprintLength);
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
      return (SyncEncoding.Hash(meshes), SyncEncoding.Hash(element.ToString(Formatting.None)));
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
        SetKnown(id, same ? (hasVersion ? version[0] ?? 0 : 0) : -1, same ? (hasVersion ? version[1] ?? 0 : 0) : -1); resolved++;
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
        if (prints.Versions.TryGetValue(id, out var version) && version?.Length >= 2) SetKnown(id, version[0] ?? 0, version[1] ?? 0);
      }
      transaction.Commit();
      return resolved;
    }

    private void SetKnown(string id, long g, long p) =>
      Execute("INSERT OR REPLACE INTO known VALUES($id,$g,$p)", ("$id", id), ("$g", g), ("$p", p));

    /// <summary>What entered takes its new version and stops differing; what stayed out is listed with its reason.</summary>
    public void ApplyCommit(CommitResult result)
    {
      if (result == null) return;
      using var transaction = _db.BeginTransaction();
      foreach (var pair in result.Versions ?? new Dictionary<string, long[]>())
      {
        if (pair.Value == null || pair.Value.Length < 2) continue;
        SetKnown(pair.Key, pair.Value[0], pair.Value[1]);
        Execute("DELETE FROM differs WHERE id=$id; DELETE FROM forced WHERE id=$id", ("$id", pair.Key));
      }
      foreach (var item in result.Rejected ?? new List<DifferingElement>()) AddDiffering(item);
      transaction.Commit();
    }

    private void AddDiffering(DifferingElement item) => Execute(
      "INSERT OR REPLACE INTO differs VALUES($id,$name,$reason,$who,$at,$partial)", ("$id", item.Id), ("$name", item.Name),
      ("$reason", item.Reason ?? "stale"), ("$who", item.By), ("$at", item.At), ("$partial", item.Partial ? 1 : 0));

    public IReadOnlyList<DifferingElement> Differs
    {
      get
      {
        using var cmd = Command("SELECT id,name,reason,who,at,partial FROM differs ORDER BY name,id");
        using var reader = cmd.ExecuteReader(); var list = new List<DifferingElement>();
        while (reader.Read()) list.Add(new DifferingElement { Id = reader.GetString(0), Name = reader.IsDBNull(1) ? null : reader.GetString(1),
          Reason = reader.IsDBNull(2) ? "stale" : reader.GetString(2), By = reader.IsDBNull(3) ? null : reader.GetString(3),
          At = reader.IsDBNull(4) ? null : reader.GetString(4), Partial = reader.GetInt32(5) == 1 });
        return list;
      }
    }

    /// <summary>Administración: the next publication carries these elements with «force» (UI-8 case 17).</summary>
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
      public ISet<string> Gone { get; set; } = new HashSet<string>();
      public bool CanForce { get; set; }
      internal HashSet<string> Forced { get; } = new();
      internal Dictionary<string, object> PartVersions { get; } = new();
      internal List<string> PartForce { get; } = new();
      internal HashSet<string> Emitted { get; } = new();
      internal int Predicted { get; set; }
    }

    // What the element carries ({a, bg, bp}), or null when its change would not enter (it is listed as differing instead).
    private object PlanElement(VersionPlan plan, string id, string json, out bool forced)
    {
      var (g, p) = Aspects(json);
      (string G, string P)? reference = null;
      using (var cmd = Command("SELECT json FROM confirmed WHERE id=$id", ("$id", id)))
        if (cmd.ExecuteScalar() is string confirmed) reference = Aspects(confirmed);
      if (reference == null)
        using (var cmd = Command("SELECT g,p FROM baseline_aspects WHERE id=$id", ("$id", id)))
        using (var reader = cmd.ExecuteReader()) if (reader.Read()) reference = (reader.GetString(0), reader.GetString(1));
      var changed = reference == null ? "gp" : (g != reference.Value.G ? "g" : "") + (p != reference.Value.P ? "p" : "");
      if (changed.Length == 0) changed = "gp";
      long bg = -1, bp = -1;
      using (var cmd = Command("SELECT vg,vp FROM known WHERE id=$id", ("$id", id)))
      using (var reader = cmd.ExecuteReader()) if (reader.Read()) { bg = reader.GetInt64(0); bp = reader.GetInt64(1); }
      forced = plan.CanForce && plan.Forced.Contains(id);
      if (!forced && Stale(plan, id, changed, bg, bp, json)) return null;
      plan.Emitted.Add(id);
      return new { a = changed, bg, bp };
    }

    // Case 14: a change from an older version than the CDE's is known before sending; it stays out and is shown.
    private bool Stale(VersionPlan plan, string id, string changed, long bg, long bp, string json)
    {
      if (plan.Remote == null) return false;
      string reason = null; long? author = null;
      if (plan.Remote.TryGetValue(id, out var current) && current?.Length >= 2)
      {
        var staleG = changed.Contains("g") && bg < (current[0] ?? 0);
        var staleP = changed.Contains("p") && bp < (current[1] ?? 0);
        if ((staleG || !changed.Contains("g")) && (staleP || !changed.Contains("p"))) { reason = "stale"; author = current.Length > 2 ? current[2] : null; }
      }
      else if (plan.Gone.Contains(id)) reason = "removed";
      if (reason == null) return false;
      string name = null;
      try { name = JObject.Parse(json).Value<string>("name"); } catch (JsonException) { }
      plan.Authors.TryGetValue(author?.ToString() ?? "", out var by);
      AddDiffering(new DifferingElement { Id = id, Name = name, Reason = reason, By = by });
      plan.Predicted++;
      return true;
    }

    // Removals carry the version they start from; one that the CDE changed meanwhile stays out (case 5).
    private bool PlanRemoval(VersionPlan plan, string id, Dictionary<string, long> bases)
    {
      long known = -1;
      using (var cmd = Command("SELECT max(vg,vp) FROM known WHERE id=$id", ("$id", id)))
        if (cmd.ExecuteScalar() is long value) known = value;
      if (plan.Remote != null && plan.Remote.TryGetValue(id, out var current) && current?.Length >= 2 &&
          known < Math.Max(current[0] ?? 0, current[1] ?? 0) && !(plan.CanForce && plan.Forced.Contains(id)))
      {
        string name = null;
        using (var cmd = Command("SELECT json FROM confirmed WHERE id=$id", ("$id", id)))
          if (cmd.ExecuteScalar() is string json) try { name = JObject.Parse(json).Value<string>("name"); } catch (JsonException) { }
        plan.Authors.TryGetValue((current.Length > 2 ? current[2] : null)?.ToString() ?? "", out var by);
        AddDiffering(new DifferingElement { Id = id, Name = name, Reason = "stale", By = by });
        plan.Predicted++;
        return false;
      }
      bases[id] = known;
      return true;
    }

    private void LoadForced(VersionPlan plan)
    {
      using var cmd = Command("SELECT id FROM forced");
      using var reader = cmd.ExecuteReader(); while (reader.Read()) plan.Forced.Add(reader.GetString(0));
    }
  }
}
