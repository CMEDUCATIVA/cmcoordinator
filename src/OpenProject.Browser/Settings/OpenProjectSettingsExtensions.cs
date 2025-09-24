using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Serilog;

namespace OpenProject.Browser.Settings
{
  public static class OpenProjectSettingsExtensions
  {
    public static List<string> GetOpenProjectInstances(this IOpenProjectSettings settings)
    {
      var result = new List<string>();

      try
      {
        result = JsonConvert.DeserializeObject<List<string>>(settings.OpenProjectInstances);
      }
      catch (Exception exception)
      {
        Log.Error(exception, "Invalid settings value for OpenProject instances: {0}.", settings.OpenProjectInstances);
      }

      return result;
    }
  }
}
