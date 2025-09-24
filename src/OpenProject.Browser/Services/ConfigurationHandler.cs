using System;
using System.IO;
using Config.Net;
using Newtonsoft.Json;
using OpenProject.Browser.Settings;

namespace OpenProject.Browser.Services
{
  public static class ConfigurationHandler
  {
    static ConfigurationHandler()
    {
      try
      {
        var configurationFilePath = GetConfigurationFilePath();
        Settings = new ConfigurationBuilder<IOpenProjectSettings>()
          .UseJsonFile(configurationFilePath)
          .Build();
      }
      catch (Exception exception)
      {
        MessageHandler.ShowError(exception,
          "Cannot fetch OpenProject application settings. Please contact an administrator.");
      }
    }

    public static IOpenProjectSettings Settings { get; }

    public static void RemoveSavedInstance(string instanceUrl)
    {
      var instances = Settings.GetOpenProjectInstances();
      instances.Remove(instanceUrl);
      Settings.OpenProjectInstances = JsonConvert.SerializeObject(instances);
    }

    public static void SaveSelectedInstance(string instanceUrl)
    {
      RemoveSavedInstance(instanceUrl);
      AddOpenProjectInstance(instanceUrl);
    }

    private static void AddOpenProjectInstance(string openProjectInstance)
    {
      if (openProjectInstance == null || string.Empty.Equals(openProjectInstance))
        return;

      var instances = Settings.GetOpenProjectInstances();
      instances.Insert(0, openProjectInstance);
      Settings.OpenProjectInstances = JsonConvert.SerializeObject(instances);
    }

    public static void SaveLastVisitedPage(string url) => Settings.LastVisitedPage = url;

    public static string LastVisitedPage() => Settings.LastVisitedPage ?? string.Empty;

    private static string GetConfigurationFilePath()
    {
      var configPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OpenProject.Revit",
        "OpenProject.Configuration.json");

      if (File.Exists(configPath)) return configPath;

      // If the file doesn't yet exist, the default one is created
      using Stream configStream =
        typeof(ConfigurationHandler).Assembly
          .GetManifestResourceStream("OpenProject.Browser.Settings.OpenProject.Configuration.json");
      if (configStream == null)
        throw new ApplicationException("Missing configuration manifest");

      var configDirName = Path.GetDirectoryName(configPath);

      if (!Directory.Exists(configDirName))
        Directory.CreateDirectory(configDirName);

      using FileStream fs = File.Create(configPath);
      configStream.CopyTo(fs);

      return configPath;
    }
  }
}
