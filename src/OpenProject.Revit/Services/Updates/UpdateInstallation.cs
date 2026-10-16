using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OpenProject.Revit.Services.Updates
{
  public static class UpdateInstallation
  {
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cmcoordinator", "Updates");
    public static string ReceiptPath(string assembly) => Path.Combine(Root, "installed-" + UpdateRelease.FileHashOfText(assembly) + ".json");

    public static bool IsCurrent(UpdateRelease release, string assembly, Version version)
    {
      try
      {
        var path = ReceiptPath(assembly);
        if (File.Exists(path))
        {
          var receipt = JObject.Parse(File.ReadAllText(path));
          if (receipt.Value<string>("AssemblyHash") == UpdateRelease.FileHash(assembly))
          {
            if (receipt.Value<string>("Key") == release.Key) return true;
            return UpdateRelease.ParseVersion(release.Tag) < UpdateRelease.ParseVersion(receipt.Value<string>("Tag"));
          }
        }
      }
      catch (IOException) { }
      catch (JsonException) { }
      catch (InvalidOperationException) { }
      var normalized = new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
      return UpdateRelease.ParseVersion(release.Tag) <= normalized;
    }

    public static string PendingStatus()
    {
      var path = Path.Combine(Root, "pending.json");
      if (!File.Exists(path)) return null;
      try
      {
        var job = JObject.Parse(File.ReadAllText(path));
        var state = job.Value<string>("State");
        if (state == "waiting" || state == "installing")
        {
          using var process = Process.GetProcessById(job.Value<int>("HelperPid"));
          if (!process.HasExited && process.ProcessName.Equals("powershell", StringComparison.OrdinalIgnoreCase))
            return state == "waiting" ? "Actualización preparada. Cierra todas las ventanas de Revit para instalar." : "Instalación en curso.";
        }
        if (state == "failed") return "La última actualización falló o se canceló. Puedes reintentar; consulta Updates/update.log.";
      }
      catch (Exception error) when (error is IOException || error is JsonException || error is ArgumentException ||
        error is InvalidOperationException || error is UnauthorizedAccessException || error is System.ComponentModel.Win32Exception || error is FormatException) { }
      return null;
    }

    public static void Schedule(UpdateRelease release, string installer, string assembly, string helperSource)
    {
      Directory.CreateDirectory(Root);
      var folder = Path.GetDirectoryName(installer);
      var helper = Path.Combine(folder, "apply-update.ps1");
      File.Copy(helperSource, helper, false);
      var jobPath = Path.Combine(folder, "job.json");
      File.WriteAllText(jobPath, JsonConvert.SerializeObject(new { release.Tag, release.Key, release.Sha256,
        Installer = installer, Assembly = assembly, PreviousHash = UpdateRelease.FileHash(assembly),
        Receipt = ReceiptPath(assembly), Root, State = "waiting" }));
      var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
      foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", helper, "-JobFile", jobPath })
        start.ArgumentList.Add(arg);
      using var process = Process.Start(start) ?? throw new InvalidOperationException("No pudo iniciarse el instalador diferido.");
    }
  }
}
