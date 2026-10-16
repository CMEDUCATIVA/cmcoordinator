using System;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using OpenProject.Shared;
using OpenProject.Revit.Models;
using Serilog;

namespace OpenProject.Revit.Services
{
  public static class McpSettingsStore
  {
    private const string FileName = "McpSettings.json";
    private static readonly string StableSettingsPath =
      Path.Combine(ConfigurationConstant.OpenProjectApplicationData, FileName);

    public static McpSettings Load()
    {
      try
      {
        var path = EnsureSettingsFilePath();
        if (!File.Exists(path))
        {
          Log.Information("McpSettingsStore.Load missing file path={Path}", path);
          return HydrateFromBrowserUrl(new McpSettings());
        }

        var json = File.ReadAllText(path);
        var settings = JsonConvert.DeserializeObject<McpSettings>(json) ?? new McpSettings();
        settings = HydrateFromBrowserUrl(settings);
        Log.Information("McpSettingsStore.Load path={Path} bytes={Bytes}", path, json.Length);
        return settings;
      }
      catch (Exception ex)
      {
        Log.Error(ex, "McpSettingsStore.Load failed");
        return new McpSettings();
      }
    }

    public static void Save(McpSettings settings)
    {
      try
      {
        var path = GetSettingsPath();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
          Directory.CreateDirectory(dir);

        var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
        File.WriteAllText(path, json);
        Log.Information("McpSettingsStore.Save path={Path} bytes={Bytes}", path, json.Length);
      }
      catch (Exception ex)
      {
        Log.Error(ex, "McpSettingsStore.Save failed");
      }
    }

    public static string GetSettingsPath()
    {
      return StableSettingsPath;
    }

    private static string GetLegacySettingsPath()
    {
      var assemblyPath = Assembly.GetExecutingAssembly().Location;
      var dir = Path.GetDirectoryName(assemblyPath) ?? AppDomain.CurrentDomain.BaseDirectory;
      return Path.Combine(dir, FileName);
    }

    private static string EnsureSettingsFilePath()
    {
      var stable = GetSettingsPath();
      var legacy = GetLegacySettingsPath();
      if (!File.Exists(stable) && File.Exists(legacy))
      {
        try
        {
          var dir = Path.GetDirectoryName(stable);
          if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

          File.Copy(legacy, stable, overwrite: false);
          Log.Information("McpSettingsStore migrated legacy settings from={From} to={To}", legacy, stable);
        }
        catch (Exception ex)
        {
          Log.Warning(ex, "McpSettingsStore failed to migrate legacy settings.");
        }
      }

      return stable;
    }

    private static McpSettings HydrateFromBrowserUrl(McpSettings settings)
    {
      settings ??= new McpSettings();
      if (!string.IsNullOrWhiteSpace(settings.Url))
        return settings;

      try
      {
        var browserConfigPath = Path.Combine(
          ConfigurationConstant.OpenProjectApplicationData,
          "OpenProject.Configuration.json");
        if (!File.Exists(browserConfigPath))
          return settings;

        var json = File.ReadAllText(browserConfigPath);
        var jObject = JObject.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);

        var lastVisited = jObject["LastVisitedPage"]?.ToString();
        var url = NormalizeToBaseUrl(lastVisited);
        if (string.IsNullOrWhiteSpace(url))
        {
          var instancesRaw = jObject["OpenProjectInstances"]?.ToString();
          if (!string.IsNullOrWhiteSpace(instancesRaw))
          {
            var array = JArray.Parse(instancesRaw);
            url = NormalizeToBaseUrl(array.Count > 0 ? array[0]?.ToString() : null);
          }
        }

        if (!string.IsNullOrWhiteSpace(url))
          settings.Url = url;
      }
      catch (Exception ex)
      {
        Log.Warning(ex, "McpSettingsStore failed to hydrate URL from browser config.");
      }

      return settings;
    }

    private static string NormalizeToBaseUrl(string url)
    {
      if (string.IsNullOrWhiteSpace(url))
        return null;

      if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        return null;

      var builder = new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port)
      {
        Path = string.Empty
      };
      return builder.Uri.ToString().TrimEnd('/');
    }
  }
}
