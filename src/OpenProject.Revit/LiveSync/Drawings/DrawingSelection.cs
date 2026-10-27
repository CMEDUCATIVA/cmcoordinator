using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace OpenProject.Revit.LiveSync.Drawings
{
  // The 2D views chosen for one document and destination, kept next to its queue (vistas2d.json).
  public static class DrawingSelection
  {
    public const string FileName = "vistas2d.json";

    public static IReadOnlyList<string> Load(string queueDirectory)
    {
      var path = Path.Combine(queueDirectory, FileName);
      try
      {
        if (!File.Exists(path)) return Array.Empty<string>();
        return (JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(path)) ?? new List<string>())
          .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
      }
      // A damaged file only loses the choice; the user picks the views again.
      catch (JsonException) { return Array.Empty<string>(); }
    }

    public static void Save(string queueDirectory, IEnumerable<string> viewIds)
    {
      Directory.CreateDirectory(queueDirectory);
      var path = Path.Combine(queueDirectory, FileName);
      var temporary = path + ".tmp";
      File.WriteAllText(temporary, JsonConvert.SerializeObject(viewIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList()));
      NetCompat.MoveReplacing(temporary, path);
    }
  }
}
