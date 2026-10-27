using System.Diagnostics;
using CmConv.Lectura;
using CmConv.Salida;
using CmConv.Semantica;
using Newtonsoft.Json;

namespace CmConv
{
  /// <summary>
  /// cmconv convertir &lt;archivo.rvt&gt; -o &lt;carpeta&gt; [--mallas mixtas|cajas|guardadas] (mixtas por defecto) [--rvtrs &lt;carpeta&gt;]
  /// cmconv validar &lt;carpeta&gt;
  /// Exit codes: 0 converted and valid, 2 bad arguments, 3 unsupported or empty model, 4 invalid output, 1 other error.
  /// </summary>
  public static class Program
  {
    private static readonly HashSet<string> SupportedVersions = new() { "2023", "2024", "2025", "2026", "2027" };

    public static int Main(string[] args)
    {
      try
      {
        return args.FirstOrDefault() switch
        {
          "convertir" when args.Length >= 2 => Convert(args),
          "validar" when args.Length >= 2 => Validate(args[1]),
          _ => Usage()
        };
      }
      catch (Exception error)
      {
        Console.Error.WriteLine("ERROR: " + error.Message);
        return 1;
      }
    }

    private static int Convert(string[] args)
    {
      var input = Path.GetFullPath(args[1]);
      var output = Path.GetFullPath(Option(args, "-o") ?? Path.ChangeExtension(input, null) + "_cmconv");
      var detail = Option(args, "--mallas") switch { "guardadas" => MeshDetail.Saved, "cajas" => MeshDetail.Boxes, _ => MeshDetail.Mixed };
      var rvtrs = Option(args, "--rvtrs") ?? Environment.GetEnvironmentVariable("CMCONV_RVTRS") ?? Path.Combine(AppContext.BaseDirectory, "rvt-rs");
      if (!File.Exists(input)) { Console.Error.WriteLine("No existe " + input); return 2; }
      Directory.CreateDirectory(output);
      var work = Path.Combine(output, ".trabajo");
      var clock = Stopwatch.StartNew();
      var times = new Dictionary<string, double>();

      IRvtReader reader = new RvtRsReader(rvtrs, work);
      var model = reader.Read(input, detail, CancellationToken.None);
      times["lectura"] = clock.Elapsed.TotalSeconds;
      var summary = new Dictionary<string, object>
      {
        ["archivo"] = Path.GetFileName(input), ["version"] = model.Version, ["build"] = model.Build,
        ["lector"] = reader.Name, ["mallas"] = detail switch { MeshDetail.Saved => "guardadas", MeshDetail.Boxes => "cajas", _ => "mixtas" },
        ["elementos_leidos"] = model.Elements.Count, ["avisos"] = model.Warnings, ["niveles"] = model.Levels.Values.Select(l => $"{l.Name} ({l.ElevationMetres:0.00} m)").ToList()
      };
      if (!SupportedVersions.Contains(model.Version ?? ""))
      {
        summary["error"] = $"Revit {model.Version}: el convertidor admite 2023 a 2027.";
        return Finish(output, summary, 3);
      }

      var translated = new Traductor(Path.Combine(AppContext.BaseDirectory, "Datos")).Translate(model, input);
      times["traduccion"] = clock.Elapsed.TotalSeconds - times["lectura"];
      summary["elementos_convertidos"] = translated.Snapshot.Elements.Count;
      summary["omitidos"] = translated.Omitted;
      summary["clases_sin_tabla"] = translated.UnknownClasses;
      summary["con_malla"] = translated.Snapshot.Elements.Values.Count(e => e.Meshes.Count > 0);
      summary["sin_malla"] = translated.Snapshot.Elements.Values.Where(e => e.Meshes.Count == 0).GroupBy(e => e.Category)
        .OrderByDescending(g => g.Count()).ToDictionary(g => g.Key ?? "?", g => g.Count());
      summary["triangulos"] = translated.Snapshot.Elements.Values.Sum(e => e.Meshes.Sum(m => (long)m.Indices.Length / 3));
      if (translated.Snapshot.Elements.Count == 0)
      {
        summary["error"] = $"Revit {model.Version} (compilación {model.Build}): el lector no reconoce ningún elemento constructivo en este archivo.";
        return Finish(output, summary, 3);
      }

      var job = Empaquetador.Write(translated.Snapshot, output, work);
      times["empaquetado"] = clock.Elapsed.TotalSeconds - times["lectura"] - times["traduccion"];
      var report = Validador.Check(output);
      times["total"] = clock.Elapsed.TotalSeconds;
      summary["paquetes"] = job.ChunkCount;
      summary["mb"] = Math.Round(job.ByteLength / 1048576.0, 1);
      summary["formas_compartidas"] = report.SharedShapes;
      summary["valido"] = report.Valid;
      summary["errores_validacion"] = report.Errors;
      summary["segundos"] = times.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 1));
      if (Directory.Exists(work)) Directory.Delete(work, true);
      return Finish(output, summary, report.Valid ? 0 : 4);
    }

    private static int Validate(string directory)
    {
      var report = Validador.Check(Path.GetFullPath(directory));
      Console.WriteLine(JsonConvert.SerializeObject(report, Formatting.Indented));
      return report.Valid ? 0 : 4;
    }

    private static int Finish(string output, Dictionary<string, object> summary, int code)
    {
      summary["codigo"] = code;
      var json = JsonConvert.SerializeObject(summary, Formatting.Indented);
      File.WriteAllText(Path.Combine(output, "resumen.json"), json);
      Console.WriteLine(json);
      return code;
    }

    private static string Option(string[] args, string name)
    {
      var i = Array.IndexOf(args, name);
      return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int Usage()
    {
      Console.Error.WriteLine("Uso:\n  cmconv convertir <archivo.rvt> [-o <carpeta>] [--mallas mixtas|cajas|guardadas] [--rvtrs <carpeta de rvt-rs>]\n  cmconv validar <carpeta>");
      return 2;
    }
  }
}
