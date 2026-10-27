using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OpenProject.Revit.LiveSync.Queue
{
  /// <summary>
  /// Shrinks one packaged part for a CDE that announces "sharedGeometry":
  /// identical shapes (equal up to a translation) are stored once per part and meshes refer to them,
  /// and the Revit "Instancia ·"/"Tipo ·" groups are dropped because the viewer rebuilds them from
  /// the parameters and the type catalogue. The outbox keeps the full elements; only the wire form changes.
  /// </summary>
  internal sealed class PartCompactor
  {
    private readonly Dictionary<string, string> _shapes = new(StringComparer.Ordinal);
    public int Bytes { get; private set; }

    /// <param name="added">Shapes this element introduced to the current table.</param>
    /// <param name="used">Every shape this element refers to, new or already in the table.</param>
    public string Compact(string elementJson, out IReadOnlyList<string> added, out IReadOnlyList<string> used)
    {
      var fresh = new List<string>(); var all = new List<string>();
      var element = JObject.Parse(elementJson);
      // Elements without BIM data serialise "bim" as null, which has no children.
      if (element["bim"] is JObject bim && bim["ifcSets"] is JArray sets)
        foreach (var set in sets.Where(s => ((string)s["name"] ?? "").StartsWith("Instancia · ", StringComparison.Ordinal) ||
                                            ((string)s["name"] ?? "").StartsWith("Tipo · ", StringComparison.Ordinal)).ToList())
          set.Remove();
      if (element["meshes"] is JArray meshes)
        for (var index = 0; index < meshes.Count; index++)
          if (meshes[index] is JObject mesh && mesh["positions"] is JArray positions && positions.Count >= 3 && mesh["indices"] is JArray indices)
            meshes[index] = Share(mesh, positions, indices, fresh, all);
      added = fresh; used = all;
      return element.ToString(Formatting.None);
    }

    // Removes every shape except those referenced by the element that opens the next part,
    // including shapes it shares with elements of the part just closed.
    public void KeepOnly(IReadOnlyList<string> keys)
    {
      var kept = keys.Distinct(StringComparer.Ordinal).ToDictionary(key => key, key => _shapes[key], StringComparer.Ordinal);
      _shapes.Clear(); Bytes = 0;
      foreach (var pair in kept) { _shapes[pair.Key] = pair.Value; Bytes += EntryBytes(pair.Key, pair.Value); }
    }

    public string Table(IReadOnlyList<string> exclude = null)
    {
      var skip = new HashSet<string>(exclude ?? Array.Empty<string>(), StringComparer.Ordinal);
      return "{" + string.Join(",", _shapes.Where(pair => !skip.Contains(pair.Key)).Select(pair => "\"" + pair.Key + "\":" + pair.Value)) + "}";
    }

    private JObject Share(JObject mesh, JArray positions, JArray indices, List<string> fresh, List<string> all)
    {
      var offset = new[] { (double)positions[0], (double)positions[1], (double)positions[2] };
      var local = new double[positions.Count];
      for (var i = 0; i < positions.Count; i++) local[i] = Math.Round((double)positions[i] - offset[i % 3], 4);
      var shape = "{\"positions\":" + Numbers(local) + ",\"indices\":" + indices.ToString(Formatting.None) + "}";
      var key = NetCompat.Hex(NetCompat.Sha256(Encoding.UTF8.GetBytes(shape))).Substring(0, 24);
      if (!_shapes.ContainsKey(key)) { _shapes[key] = shape; Bytes += EntryBytes(key, shape); fresh.Add(key); }
      all.Add(key);
      var shared = new JObject { ["geometry"] = key, ["offset"] = new JArray(offset[0], offset[1], offset[2]) };
      foreach (var property in mesh.Properties())
        if (property.Name is not ("positions" or "indices")) shared[property.Name] = property.Value;
      return shared;
    }

    private static string Numbers(double[] values) =>
      "[" + string.Join(",", values.Select(value => value.ToString("R", CultureInfo.InvariantCulture))) + "]";

    private static int EntryBytes(string key, string shape) => Encoding.UTF8.GetByteCount(shape) + key.Length + 4;
  }
}
