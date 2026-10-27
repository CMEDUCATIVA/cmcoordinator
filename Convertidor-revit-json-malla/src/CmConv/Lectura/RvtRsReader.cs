using System.Diagnostics;
using Newtonsoft.Json.Linq;

namespace CmConv.Lectura
{
  /// <summary>
  /// Reads a .rvt with the rvt-rs command-line tools (Apache-2.0), run as external programs:
  /// rvt-info (version), rvt-elements (elements and parameters), rvt-gltf (meshes) and rvt-ifc (level names).
  /// </summary>
  public sealed class RvtRsReader : IRvtReader
  {
    private readonly string _bin;
    private readonly string _work;
    public string Name => "rvt-rs";
    /// <summary>Per element, about what Cmcoordinator sends for a detailed family (furniture averages ~1.400).</summary>
    public const int TriangleBudget = 1500;
    private static readonly HashSet<string> PlaceholderClasses = new() { "Planting", "Entourage" };
    private const double PlaceholderOpacity = 0.25;
    /// <summary>Four boxes; a modelled tree family has hundreds of triangles and stays opaque.</summary>
    private const int PlaceholderMaxTriangles = 48;
    public RvtRsReader(string binDirectory, string workDirectory) { _bin = binDirectory; _work = workDirectory; }

    public RvtModel Read(string path, MeshDetail detail, CancellationToken token)
    {
      Directory.CreateDirectory(_work);
      var model = new RvtModel();
      var info = JObject.Parse(Run("rvt-info", new[] { "--json", path }, null, token));
      model.Version = (string)info["version"] ?? (string)info["revit_version"];
      model.Build = (string)info["build"];
      model.Title = (string)info["title"] ?? Path.GetFileNameWithoutExtension(path);
      model.DocumentGuid = (string)info["document_guid"] ?? (string)info["guid"];

      var elementsPath = Path.Combine(_work, "elements.json");
      var boxesPath = Path.Combine(_work, "model.glb");
      var savedPath = Path.Combine(_work, "model.saved.glb");
      var ifcPath = Path.Combine(_work, "model.ifc");
      var primaryGlb = detail == MeshDetail.Saved ? savedPath : boxesPath;
      var primaryArgs = detail == MeshDetail.Saved ? new[] { "--saved-meshes", path, "-o", savedPath } : new[] { path, "-o", boxesPath };
      // The light decoders are independent: run them side by side.
      Task.WaitAll(new[]
      {
        Task.Run(() => Run("rvt-elements", new[] { path }, elementsPath, token), token),
        Task.Run(() => Run("rvt-gltf", primaryArgs, null, token), token),
        Task.Run(() => Run("rvt-ifc", new[] { path, "-o", ifcPath }, null, token), token)
      }, token);
      // The saved meshes need the most memory (2–3 GB on large models): decoded on their own afterwards.
      if (detail == MeshDetail.Mixed) Run("rvt-gltf", new[] { "--saved-meshes", path, "-o", savedPath }, null, token);

      using (var reader = new Newtonsoft.Json.JsonTextReader(new StreamReader(elementsPath)))
      {
        var anonymous = 0;
        // Some records come without an ElementId: nothing can refer to them, so they are left out.
        foreach (JObject e in JArray.Load(reader))
          if (e["id"]?.Type == JTokenType.Integer) model.Elements.Add(Element(e));
          else anonymous++;
        if (anonymous > 0) model.Warnings.Add($"{anonymous} registros sin ElementId omitidos");
      }
      var meshes = GlbReader.Read(primaryGlb);
      var saved = detail == MeshDetail.Mixed ? GlbReader.Read(savedPath) : null;
      var replaced = 0;
      var placeholders = 0;
      foreach (var element in model.Elements)
      {
        if (!meshes.TryGetValue(element.Id, out var entry)) continue;
        element.IfcHint = entry.IfcType;
        if (saved != null && Simplificador.IsBox(entry.Meshes) && saved.TryGetValue(element.Id, out var real) && real.Meshes.Count > 0)
        {
          element.Meshes.AddRange(Simplificador.ToBudget(real.Meshes, TriangleBudget));
          replaced++;
        }
        else element.Meshes.AddRange(entry.Meshes);
        // RPC trees and people keep only a few boxes in the file (Revit renders the real plant): a stand-in, not a solid.
        if (PlaceholderClasses.Contains(element.ClassName ?? "") && element.Meshes.Sum(m => m.Indices.Length / 3) <= PlaceholderMaxTriangles)
        {
          foreach (var mesh in element.Meshes) mesh.Opacity = Math.Min(mesh.Opacity, PlaceholderOpacity);
          placeholders++;
        }
      }
      if (detail == MeshDetail.Mixed) model.Warnings.Add($"{replaced} elementos dibujados con la malla guardada por Revit, simplificada a {TriangleBudget} triángulos como máximo");
      if (placeholders > 0) model.Warnings.Add($"{placeholders} elementos de vegetación o entorno (RPC) sin forma real en el archivo: se dibujan como cajas translúcidas");
      var levelOf = model.Elements.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First().LevelId);
      var (levels, guids) = IfcStoreys.Match(ifcPath, levelOf);
      foreach (var level in levels) model.Levels[level.Key] = level.Value;
      // The level Revit shows for the element (a beam's reference level) wins over the record's own level link.
      var levelByName = model.Levels.Values.GroupBy(l => l.Name).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First().Id);
      foreach (var element in model.Elements)
        if (element.LevelName != null && levelByName.TryGetValue(element.LevelName, out var named)) element.LevelId = named;
      foreach (var element in model.Elements)
        if (element.IfcGuid == null && guids.TryGetValue(element.Id, out var guid)) element.IfcGuid = guid;
      return model;
    }

    private static RvtElement Element(JObject e)
    {
      var fields = ((JArray)e["fields"] ?? new JArray()).OfType<JObject>().GroupBy(f => (string)f["name"]).ToDictionary(g => g.Key, g => g.First());
      object Field(string name) => fields.TryGetValue(name, out var f) ? (object)(f["value"] ?? f["id"]) : null;
      long? Id(string name) => Field(name) is JValue v && long.TryParse(v.ToString(), out var id) ? id : null;
      var element = new RvtElement
      {
        Id = (long)e["id"], ClassName = (string)e["class_name"],
        CategoryId = Id("m_builtinCategory"), FamilyName = Field("m_family_name")?.ToString(),
        TypeName = Field("m_type_name")?.ToString(), TypeId = Id("m_type_id"), LevelId = Id("m_levelId"),
        AggregateId = Id("m_aggregate_whole")
      };
      double? Number(string name) => Field(name) is JValue v && v.Value is IConvertible c ? c.ToDouble(System.Globalization.CultureInfo.InvariantCulture) : null;
      if (Number("m_wall_axis_start_x") is double x0 && Number("m_wall_axis_start_y") is double y0 &&
          Number("m_wall_axis_end_x") is double x1 && Number("m_wall_axis_end_y") is double y1)
        element.WallAxisFeet = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
      element.WallThicknessFeet = Number("m_wall_type_thickness");
      element.LevelName = Field("m_level_parameter")?.ToString();
      if (Number("m_beam_axis_start_x") is double bx0 && Number("m_beam_axis_start_y") is double by0 && Number("m_beam_axis_start_z") is double bz0 &&
          Number("m_beam_axis_end_x") is double bx1 && Number("m_beam_axis_end_y") is double by1 && Number("m_beam_axis_end_z") is double bz1)
        element.AxisInclinationDegrees = Math.Atan2(Math.Abs(bz1 - bz0), Math.Sqrt((bx1 - bx0) * (bx1 - bx0) + (by1 - by0) * (by1 - by0))) * 180 / Math.PI;
      foreach (JObject p in (JArray)e["parameters"] ?? new JArray())
      {
        if (p["parameter_id"]?.Type != JTokenType.Integer) continue;
        element.Parameters.Add(new RvtParameter
        {
          Id = (long)p["parameter_id"], Name = (string)p["name"], Storage = (string)p["storage_type"],
          Value = (p["value"] as JValue)?.Value, SiValue = (double?)p["si_value"], SiUnit = (string)p["si_unit"],
          Scope = (string)p["level"]
        });
      }
      element.IfcGuid = element.Parameters.FirstOrDefault(p => p.Id == Semantica.ConjuntosIfc.IfcGuid && p.Scope != "type" && p.Value is string s && s.Length == 22)?.Value as string;
      return element;
    }

    private string Run(string tool, string[] args, string stdoutPath, CancellationToken token)
    {
      var psi = new ProcessStartInfo(Path.Combine(_bin, tool)) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
      foreach (var a in args) psi.ArgumentList.Add(a);
      using var process = Process.Start(psi) ?? throw new InvalidOperationException($"No se pudo iniciar {tool}");
      using var registration = token.Register(() => { try { process.Kill(true); } catch { } });
      string stdout = null;
      var stderr = process.StandardError.ReadToEndAsync();
      if (stdoutPath != null) using (var file = File.Create(stdoutPath)) process.StandardOutput.BaseStream.CopyTo(file);
      else stdout = process.StandardOutput.ReadToEnd();
      process.WaitForExit();
      token.ThrowIfCancellationRequested();
      if (process.ExitCode != 0) throw new InvalidOperationException($"{tool} terminó con código {process.ExitCode}: {Tail(stderr.Result)}");
      return stdout;
    }

    private static string Tail(string text) => text.Length <= 400 ? text : text[^400..];
  }
}
