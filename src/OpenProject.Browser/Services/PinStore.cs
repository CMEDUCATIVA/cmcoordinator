using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using OpenProject.Shared;
using OpenProject.Shared.Pins;
using Serilog;

namespace OpenProject.Browser.Services
{
  public sealed class PinStore
  {
    private static readonly string StorePath =
      Path.Combine(ConfigurationConstant.OpenProjectApplicationData, "Pins.json");

    public List<PinRecord> Load()
    {
      try
      {
        if (!File.Exists(StorePath))
        {
          Log.Information("PinStore.Load missing file path={Path}", StorePath);
          return new List<PinRecord>();
        }

        var json = File.ReadAllText(StorePath);
        var records = JsonConvert.DeserializeObject<List<PinRecord>>(json) ?? new List<PinRecord>();
        Log.Information("PinStore.Load path={Path} count={Count} bytes={Bytes}", StorePath, records.Count, json.Length);
        return records;
      }
      catch
      {
        Log.Error("PinStore.Load failed path={Path}", StorePath);
        return new List<PinRecord>();
      }
    }

    public void Save(IEnumerable<PinRecord> records)
    {
      try
      {
        var dir = Path.GetDirectoryName(StorePath);
        if (!Directory.Exists(dir))
          Directory.CreateDirectory(dir);

        var json = JsonConvert.SerializeObject(records.ToList(), Formatting.Indented);
        File.WriteAllText(StorePath, json);
        Log.Information("PinStore.Save path={Path} count={Count} bytes={Bytes}", StorePath, records.Count(), json.Length);
      }
      catch
      {
        // ignored
        Log.Error("PinStore.Save failed path={Path}", StorePath);
      }
    }
  }
}
