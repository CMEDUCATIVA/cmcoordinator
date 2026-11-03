using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.UI;

namespace OpenProject.Revit.Entry
{
  /// <summary>
  /// Another copy of Cmcoordinator registered for this Revit version (an old install in another add-ins folder, or the
  /// identifier inherited from the OpenProject Revit Add-In). Revit's own «duplicate add-in ID» message names neither the
  /// file nor the fix, so at startup this one does: the paths, and that running the installer again removes them.
  /// </summary>
  internal static class ComplementoDuplicado
  {
    private static readonly string[] Ids = { "b3ca797e-98df-4670-92e9-7b8d19c5c695", "5f96a79f-0e28-4d02-be10-251c8032a270" };

    public static void Avisar(UIControlledApplication application)
    {
      try
      {
        var anio = application.ControlledApplication.VersionNumber;
        var carpetas = new[]
        {
          Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
          Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
          Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        }.Select(raiz => Path.Combine(raiz, "Autodesk", "Revit", "Addins", anio));
        var otras = Buscar(carpetas, Assembly.GetExecutingAssembly().Location);
        if (otras.Count == 0) return;
        Serilog.Log.Warning("Otras copias de Cmcoordinator registradas: {Copias}", string.Join(" | ", otras));
        new TaskDialog("Cmcoordinator")
        {
          MainIcon = TaskDialogIcon.TaskDialogIconWarning,
          MainInstruction = "Hay otra copia de Cmcoordinator instalada",
          MainContent = "Revit encontró más de una copia del complemento. Puede cargar una versión antigua o avisar de un ID de complemento duplicado:\n\n" +
                        string.Join("\n", otras) +
                        "\n\nPara repararlo, cierra Revit y vuelve a ejecutar el instalador de Cmcoordinator: quita las copias sobrantes y guarda una copia de seguridad."
        }.Show();
      }
      catch (Exception error)
      {
        Serilog.Log.Warning(error, "No se pudo comprobar si hay otras copias de Cmcoordinator.");
      }
    }

    /// <summary>Manifests in `carpetas` that carry one of our identifiers and load another assembly than `propia`.</summary>
    internal static List<string> Buscar(IEnumerable<string> carpetas, string propia)
    {
      var resultado = new List<string>();
      foreach (var carpeta in carpetas.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
        foreach (var manifiesto in Directory.GetFiles(carpeta, "*.addin"))
        {
          string texto;
          try { texto = File.ReadAllText(manifiesto); }
          catch (IOException) { continue; }
          catch (UnauthorizedAccessException) { continue; }
          if (!Ids.Any(id => texto.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
          var ensamblado = Ensamblado(texto, carpeta);
          if (ensamblado == null || !string.Equals(Path.GetFullPath(ensamblado), Path.GetFullPath(propia), StringComparison.OrdinalIgnoreCase))
            resultado.Add(manifiesto);
        }
      return resultado;
    }

    private static string Ensamblado(string texto, string carpeta)
    {
      var inicio = texto.IndexOf("<Assembly>", StringComparison.OrdinalIgnoreCase);
      var fin = texto.IndexOf("</Assembly>", StringComparison.OrdinalIgnoreCase);
      if (inicio < 0 || fin <= inicio) return null;
      var ruta = texto.Substring(inicio + 10, fin - inicio - 10).Trim();
      try { return Path.IsPathRooted(ruta) ? ruta : Path.Combine(carpeta, ruta); }
      catch (ArgumentException) { return null; }
    }
  }
}
