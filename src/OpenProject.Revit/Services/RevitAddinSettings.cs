using System.IO;
using Newtonsoft.Json;
using OpenProject.Shared;

namespace OpenProject.Revit.Services
{
  public enum ViewPreference
  {
    ActiveView,
    CmcoordinatorView
  }

  public sealed class RevitAddinSettings
  {
    public ViewPreference? ViewPreference { get; set; }
    public bool RememberViewPreference { get; set; }
  }

  public static class RevitAddinSettingsStore
  {
    private static readonly string SettingsPath =
      Path.Combine(ConfigurationConstant.OpenProjectApplicationData, "RevitAddin.Settings.json");

    public static RevitAddinSettings Load()
    {
      try
      {
        if (!File.Exists(SettingsPath))
        {
          return new RevitAddinSettings();
        }

        var json = File.ReadAllText(SettingsPath);
        return JsonConvert.DeserializeObject<RevitAddinSettings>(json) ?? new RevitAddinSettings();
      }
      catch
      {
        return new RevitAddinSettings();
      }
    }

    public static void Save(RevitAddinSettings settings)
    {
      try
      {
        var dir = Path.GetDirectoryName(SettingsPath);
        if (!Directory.Exists(dir))
          Directory.CreateDirectory(dir);

        var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
        File.WriteAllText(SettingsPath, json);
      }
      catch
      {
        // ignored
      }
    }
  }
}
