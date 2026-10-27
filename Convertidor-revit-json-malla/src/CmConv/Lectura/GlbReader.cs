using System.Text;
using Newtonsoft.Json.Linq;

namespace CmConv.Lectura
{
  /// <summary>
  /// Reads the meshes of a rvt-gltf .glb grouped by Revit ElementId. rvt-gltf writes feet with Z up under a single
  /// root node; node names are "Family:Type:ElementId" or "Class-ElementId". Output is metres, Z up.
  /// </summary>
  public static class GlbReader
  {
    private const double FeetToMetres = 0.3048;

    public static Dictionary<long, (List<RvtMesh> Meshes, string IfcType)> Read(string path)
    {
      var bytes = File.ReadAllBytes(path);
      if (bytes.Length < 20 || BitConverter.ToUInt32(bytes, 0) != 0x46546C67) throw new InvalidDataException("No es un archivo GLB");
      JObject gltf = null; var bin = Array.Empty<byte>();
      for (var offset = 12; offset + 8 <= bytes.Length;)
      {
        var length = (int)BitConverter.ToUInt32(bytes, offset);
        var type = BitConverter.ToUInt32(bytes, offset + 4);
        if (type == 0x4E4F534A) gltf = JObject.Parse(Encoding.UTF8.GetString(bytes, offset + 8, length));
        else if (type == 0x004E4942) bin = bytes.AsSpan(offset + 8, length).ToArray();
        offset += 8 + length;
      }
      if (gltf == null) throw new InvalidDataException("GLB sin JSON");

      var nodes = (JArray)gltf["nodes"] ?? new JArray();
      var meshes = (JArray)gltf["meshes"] ?? new JArray();
      var materials = (JArray)gltf["materials"] ?? new JArray();
      var result = new Dictionary<long, (List<RvtMesh>, string)>();
      var roots = ((JArray)gltf["scenes"]?[0]?["nodes"])?.Select(n => (int)n).ToList() ?? Enumerable.Range(0, nodes.Count).ToList();

      void Visit(int index, double[] parent, bool isRoot)
      {
        var node = (JObject)nodes[index];
        // The single root only carries the scene's axis convention; element matrices are already in model space.
        var world = isRoot && node["mesh"] == null ? parent : Multiply(parent, LocalMatrix(node));
        if (node["mesh"] != null && ElementId(node) is long id)
        {
          if (!result.TryGetValue(id, out var entry)) result[id] = entry = (new List<RvtMesh>(), (string)node["extras"]?["ifcType"]);
          foreach (JObject primitive in meshes[(int)node["mesh"]]["primitives"])
          {
            var mesh = Primitive(gltf, bin, primitive, materials, world);
            if (mesh != null) entry.Item1.Add(mesh);
          }
        }
        foreach (var child in (JArray)node["children"] ?? new JArray()) Visit((int)child, world, false);
      }
      foreach (var root in roots) Visit(root, Identity(), true);
      return result;
    }

    private static long? ElementId(JObject node)
    {
      var name = (string)node["name"] ?? "";
      var colon = name.LastIndexOf(':');
      if (colon >= 0 && long.TryParse(name[(colon + 1)..], out var id)) return id;
      var dash = name.IndexOf('-');
      if (dash >= 0)
      {
        var digits = new string(name[(dash + 1)..].TakeWhile(char.IsDigit).ToArray());
        if (long.TryParse(digits, out id)) return id;
      }
      return null;
    }

    private static RvtMesh Primitive(JObject gltf, byte[] bin, JObject primitive, JArray materials, double[] world)
    {
      if ((int?)primitive["mode"] is int mode && mode != 4) return null; // triangles only
      var positions = ReadAccessor(gltf, bin, (int)primitive["attributes"]["POSITION"], 3);
      var vertexCount = positions.Length / 3;
      var indices = primitive["indices"] != null
        ? ReadAccessor(gltf, bin, (int)primitive["indices"], 1).Select(v => (int)v).ToArray()
        : Enumerable.Range(0, vertexCount).ToArray();
      if (vertexCount < 3 || indices.Length < 3) return null;
      var output = new double[positions.Length];
      for (var i = 0; i < vertexCount; i++)
      {
        double x = positions[i * 3], y = positions[i * 3 + 1], z = positions[i * 3 + 2];
        output[i * 3] = Math.Round((world[0] * x + world[4] * y + world[8] * z + world[12]) * FeetToMetres, 4);
        output[i * 3 + 1] = Math.Round((world[1] * x + world[5] * y + world[9] * z + world[13]) * FeetToMetres, 4);
        output[i * 3 + 2] = Math.Round((world[2] * x + world[6] * y + world[10] * z + world[14]) * FeetToMetres, 4);
      }
      // A mirrored placement flips the winding; keep faces outward as Cmcoordinator does.
      if (Determinant(world) < 0)
        for (var t = 0; t + 2 < indices.Length; t += 3) (indices[t + 1], indices[t + 2]) = (indices[t + 2], indices[t + 1]);
      var mesh = new RvtMesh { Positions = output, Indices = indices };
      if (primitive["material"] != null && materials[(int)primitive["material"]]?["pbrMetallicRoughness"]?["baseColorFactor"] is JArray color && color.Count >= 3)
      {
        mesh.Color = "#" + string.Concat(color.Take(3).Select(c => ((int)Math.Round(Math.Clamp((double)c, 0, 1) * 255)).ToString("X2")));
        mesh.Opacity = color.Count > 3 ? Math.Clamp((double)color[3], 0, 1) : 1;
      }
      return mesh;
    }

    private static double[] ReadAccessor(JObject gltf, byte[] bin, int index, int components)
    {
      var accessor = gltf["accessors"][index];
      var view = gltf["bufferViews"][(int)accessor["bufferView"]];
      var count = (int)accessor["count"];
      var componentType = (int)accessor["componentType"];
      var size = componentType switch { 5126 or 5125 => 4, 5123 or 5122 => 2, 5121 or 5120 => 1, _ => throw new InvalidDataException($"Tipo de componente {componentType}") };
      var stride = (int?)view["byteStride"] ?? size * components;
      var start = ((int?)view["byteOffset"] ?? 0) + ((int?)accessor["byteOffset"] ?? 0);
      var values = new double[count * components];
      for (var i = 0; i < count; i++)
        for (var c = 0; c < components; c++)
        {
          var at = start + i * stride + c * size;
          values[i * components + c] = componentType switch
          {
            5126 => BitConverter.ToSingle(bin, at),
            5125 => BitConverter.ToUInt32(bin, at),
            5123 => BitConverter.ToUInt16(bin, at),
            5122 => BitConverter.ToInt16(bin, at),
            5121 => bin[at],
            _ => (sbyte)bin[at]
          };
        }
      return values;
    }

    private static double[] LocalMatrix(JObject node)
    {
      if (node["matrix"] is JArray m) return m.Select(v => (double)v).ToArray();
      var t = node["translation"]?.Select(v => (double)v).ToArray() ?? new double[] { 0, 0, 0 };
      var r = node["rotation"]?.Select(v => (double)v).ToArray() ?? new double[] { 0, 0, 0, 1 };
      var s = node["scale"]?.Select(v => (double)v).ToArray() ?? new double[] { 1, 1, 1 };
      double x = r[0], y = r[1], z = r[2], w = r[3];
      return new[]
      {
        (1 - 2 * (y * y + z * z)) * s[0], 2 * (x * y + z * w) * s[0], 2 * (x * z - y * w) * s[0], 0,
        2 * (x * y - z * w) * s[1], (1 - 2 * (x * x + z * z)) * s[1], 2 * (y * z + x * w) * s[1], 0,
        2 * (x * z + y * w) * s[2], 2 * (y * z - x * w) * s[2], (1 - 2 * (x * x + y * y)) * s[2], 0,
        t[0], t[1], t[2], 1
      };
    }

    // Column-major 4×4, as glTF stores them.
    private static double[] Multiply(double[] a, double[] b)
    {
      var r = new double[16];
      for (var col = 0; col < 4; col++)
        for (var row = 0; row < 4; row++)
          r[col * 4 + row] = a[row] * b[col * 4] + a[4 + row] * b[col * 4 + 1] + a[8 + row] * b[col * 4 + 2] + a[12 + row] * b[col * 4 + 3];
      return r;
    }

    private static double[] Identity() => new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

    private static double Determinant(double[] m) =>
      m[0] * (m[5] * m[10] - m[9] * m[6]) - m[4] * (m[1] * m[10] - m[9] * m[2]) + m[8] * (m[1] * m[6] - m[5] * m[2]);
  }
}
