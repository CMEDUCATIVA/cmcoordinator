using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace CmConv.Salida
{
  /// <summary>
  /// The CDE receiver's checks (modules/bim/app/services/bim/revit_live/protocol.rb: manifest!, part!, element!)
  /// applied to a written revision, so a conversion is rejected here before it ever reaches the CDE.
  /// </summary>
  public static class Validador
  {
    private static readonly Regex Id = new(@"\A[a-zA-Z0-9_-]{1,80}\z");
    private static readonly Regex Hex64 = new(@"\A[0-9a-f]{64}\z");
    private static readonly Regex ShapeKey = new(@"\A[0-9a-f]{16,64}\z");
    private static readonly Regex Color = new(@"\A#[0-9a-fA-F]{6}\z");
    private static readonly HashSet<string> QtoTypes = new() { "length", "area", "volume", "count", "weight", "time" };

    public sealed record Report(bool Valid, List<string> Errors, int Parts, int Elements, int Meshes, int SharedShapes);

    public static Report Check(string directory)
    {
      var errors = new List<string>();
      void Require(bool ok, string message) { if (!ok && errors.Count < 50) errors.Add(message); }
      var m = JObject.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
      Require((int?)m["protocol"] == 3, "Protocolo no compatible");
      Require(Id.IsMatch((string)m["jobId"] ?? "") && Id.IsMatch((string)m["sessionId"] ?? ""), "Trabajo o sesión inválidos");
      Require(Text(m["sourceId"]) && Text(m["title"], 512) && Text(m["view"], 512), "Origen inválido");
      if (m["documentId"]?.Type == JTokenType.String) Require(Text(m["documentId"], 128), "Identificador de documento inválido");
      Require(m["reset"]?.Type == JTokenType.Boolean, "Reset requerido");
      Require((long?)m["sequence"] > 0 && (long?)m["sessionStartedAt"] > 0, "Secuencia inválida");
      Require((long?)m["byteLength"] > 0, "Revisión vacía");
      Require(Hex64.IsMatch((string)m["payloadHash"] ?? ""), "Hash inválido");

      var files = Directory.GetFiles(Path.Combine(directory, "parts"), "*.json").OrderBy(f => f).ToList();
      Require(files.Count == (int?)m["chunkCount"], $"Número de paquetes: manifiesto {(int?)m["chunkCount"]}, en disco {files.Count}");
      using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
      long bytes = 0; int elements = 0, meshes = 0, shapes = 0; var catalogs = 0; var seen = new HashSet<string>();
      foreach (var file in files)
      {
        var raw = File.ReadAllBytes(file);
        digest.AppendData(raw); bytes += raw.LongLength;
        Require(raw.Length <= 32 * 1024 * 1024, $"{Path.GetFileName(file)}: paquete mayor de 32 MiB");
        var part = JObject.Parse(System.Text.Encoding.UTF8.GetString(raw));
        var kind = (string)part["kind"];
        Require((int?)part["protocol"] == 3 && kind is "catalog" or "elements" or "removed", $"{Path.GetFileName(file)}: paquete inválido");
        if (kind == "catalog")
        {
          catalogs++;
          var bim = part["bim"] as JObject;
          Require(bim != null && (int?)bim["schema"] == 2 && Text(bim["scopeId"]) && bim["documents"] is JArray, "Semántica BIM schema2 requerida");
          foreach (var key in new[] { "levels", "types", "materials", "phases" }) Require(bim?[key] is JObject, $"Catálogo BIM inválido: {key}");
        }
        else if (kind == "elements")
        {
          var updated = part["updated"] as JArray;
          Require(updated != null && updated.Count <= 20000, "Elementos inválidos");
          var geometries = part["geometries"] as JObject ?? new JObject();
          Require(geometries.Count <= 200000, "Geometrías compartidas inválidas");
          foreach (var (key, shape) in geometries)
          {
            Require(ShapeKey.IsMatch(key) && shape is JObject, "Geometría compartida inválida");
            Triangles(shape?["positions"], shape?["indices"], Require);
            shapes++;
          }
          foreach (JObject e in updated ?? new JArray())
          {
            elements++;
            var name = (string)e["id"];
            Require(seen.Add(name ?? ""), $"Elemento duplicado {name}");
            Element(e, geometries, Require, ref meshes);
          }
        }
      }
      Require(catalogs == 1, "Falta el catálogo BIM o está duplicado");
      Require(bytes == (long?)m["byteLength"], $"Longitud: manifiesto {(long?)m["byteLength"]}, paquetes {bytes}");
      Require(Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant() == (string)m["payloadHash"], "Hash de revisión incorrecto");
      return new Report(errors.Count == 0, errors, files.Count, elements, meshes, shapes);
    }

    private static void Element(JObject e, JObject geometries, Action<bool, string> require, ref int meshes)
    {
      var label = (string)e["id"] ?? "?";
      require(new[] { "id", "elementId", "name", "category" }.All(k => Text(e[k])), $"Elemento inválido {label}");
      require(e["properties"] is JObject && e["bim"] is JObject && e["bim"]["parameters"] is JArray, $"Propiedades BIM requeridas {label}");
      if (e["bim"]?["ifcSets"] is JToken sets && sets.Type != JTokenType.Null)
      {
        require(sets is JArray { Count: <= 256 }, $"Conjuntos IFC inválidos {label}");
        var names = new HashSet<string>();
        foreach (JObject set in sets as JArray ?? new JArray())
        {
          var kind = (string)set["kind"];
          require(Text(set["name"], 256) && kind is "pset" or "qto" && set["properties"] is JArray { Count: <= 512 }, $"Conjunto IFC inválido {label}");
          require(names.Add(kind + "|" + (string)set["name"]), $"Conjuntos IFC duplicados {label}");
          foreach (JObject p in set["properties"] as JArray ?? new JArray())
          {
            var v = p["value"];
            var okValue = v == null || v.Type is JTokenType.Null or JTokenType.Boolean || v.Type == JTokenType.String && ((string)v).Length <= 4096 ||
              v.Type is JTokenType.Integer or JTokenType.Float && double.IsFinite((double)v);
            require(Text(p["name"], 256) && okValue && (p["unit"] == null || p["unit"].Type == JTokenType.Null || Text(p["unit"], 80)) &&
              (kind != "qto" || QtoTypes.Contains((string)p["type"] ?? "")), $"Propiedad IFC inválida {label}");
          }
        }
      }
      require(e["meshes"] is JArray { Count: <= 10000 }, $"Mallas inválidas {label}");
      foreach (JObject mesh in e["meshes"] as JArray ?? new JArray())
      {
        meshes++;
        if (mesh["geometry"] != null)
        {
          require(geometries.ContainsKey((string)mesh["geometry"]), $"Geometría compartida inexistente {label}");
          require(mesh["offset"] is JArray { Count: 3 } o && o.All(n => n.Type is JTokenType.Integer or JTokenType.Float && double.IsFinite((double)n) && Math.Abs((double)n) <= 1e9), $"Desplazamiento inválido {label}");
        }
        else Triangles(mesh["positions"], mesh["indices"], require);
        require(Color.IsMatch((string)mesh["color"] ?? "") && mesh["opacity"]?.Type is JTokenType.Integer or JTokenType.Float && (double)mesh["opacity"] is >= 0 and <= 1, $"Material inválido {label}");
      }
    }

    private static void Triangles(JToken positions, JToken indices, Action<bool, string> require)
    {
      var pos = positions as JArray; var idx = indices as JArray;
      require(pos != null && pos.Count >= 9 && pos.Count % 3 == 0 && pos.All(n => n.Type is JTokenType.Integer or JTokenType.Float && double.IsFinite((double)n) && Math.Abs((double)n) <= 1e9), "Vértices inválidos");
      var vertices = (pos?.Count ?? 0) / 3;
      require(idx != null && idx.Count >= 3 && idx.Count % 3 == 0 && idx.All(n => n.Type == JTokenType.Integer && (long)n >= 0 && (long)n < vertices), "Triángulos inválidos");
    }

    private static bool Text(JToken value, int max = 1024) => value?.Type == JTokenType.String && ((string)value).Length >= 1 && ((string)value).Length <= max;
  }
}
