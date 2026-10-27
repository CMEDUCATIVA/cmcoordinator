using System.Text.RegularExpressions;

namespace CmConv.Lectura
{
  /// <summary>
  /// Names the Revit levels from rvt-ifc's STEP output. Storeys carry name and elevation but not their ElementId;
  /// products carry their ElementId as Tag and sit in a storey, so each level id is matched by majority vote.
  /// </summary>
  public static class IfcStoreys
  {
    private static readonly Regex Entity = new(@"^#(\d+)=([A-Z0-9]+)\((.*)\);\s*$", RegexOptions.Compiled);

    /// <summary>Revit level id → named level, and Revit element id → IFC GlobalId (Revit's own, which rvt-ifc writes).</summary>
    public static (Dictionary<long, RvtLevel> Levels, Dictionary<long, string> Guids) Match(string stepPath, IReadOnlyDictionary<long, long?> levelOfElement)
    {
      var storeys = new Dictionary<int, RvtLevel>();
      var tags = new Dictionary<int, long>();
      var guids = new Dictionary<long, string>();
      var containment = new List<(int Storey, List<int> Elements)>();
      foreach (var line in File.ReadLines(stepPath))
      {
        var m = Entity.Match(line);
        if (!m.Success) continue;
        var id = int.Parse(m.Groups[1].Value);
        var type = m.Groups[2].Value;
        var args = SplitArguments(m.Groups[3].Value);
        if (type == "IFCBUILDINGSTOREY" && args.Count >= 10)
          storeys[id] = new RvtLevel { Name = Text(args[2]), IfcGuid = Text(args[0]), ElevationMetres = double.TryParse(args[9], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z) ? z : 0 };
        else if (type == "IFCRELCONTAINEDINSPATIALSTRUCTURE" && args.Count >= 6)
          containment.Add((Ref(args[5]), SplitArguments(args[4].Trim('(', ')')).Select(Ref).Where(r => r > 0).ToList()));
        else if (args.Count >= 8 && long.TryParse(Text(args[7]), out var tag))
        {
          tags[id] = tag;
          // Type objects share the Tag of their instances' type; only products (with a placement) name an element.
          if (args[5].Trim().StartsWith("#") && Text(args[0]) is string guid && guid.Length == 22) guids.TryAdd(tag, guid);
        }
      }
      var votes = new Dictionary<long, Dictionary<int, int>>();
      foreach (var (storey, elements) in containment)
      {
        if (!storeys.ContainsKey(storey)) continue;
        foreach (var e in elements)
          if (tags.TryGetValue(e, out var elementId) && levelOfElement.TryGetValue(elementId, out var levelId) && levelId is long level)
          {
            if (!votes.TryGetValue(level, out var counter)) votes[level] = counter = new();
            counter[storey] = counter.GetValueOrDefault(storey) + 1;
          }
      }
      var result = new Dictionary<long, RvtLevel>();
      foreach (var (level, counter) in votes)
      {
        var storey = storeys[counter.OrderByDescending(kv => kv.Value).First().Key];
        result[level] = new RvtLevel { Id = level, Name = storey.Name, ElevationMetres = storey.ElevationMetres, IfcGuid = storey.IfcGuid };
      }
      return (result, guids);
    }

    private static int Ref(string value) => value.Trim().StartsWith("#") && int.TryParse(value.Trim()[1..], out var r) ? r : 0;

    private static string Text(string value)
    {
      value = value.Trim();
      if (value.Length < 2 || value[0] != '\'') return value == "$" ? null : value;
      return DecodeStep(value[1..^1].Replace("''", "'"));
    }

    // STEP strings encode non-ASCII as \X2\hhhh...\X0\ (UTF-16) and \X\hh (ISO 8859-1).
    private static string DecodeStep(string s)
    {
      s = Regex.Replace(s, @"\\X2\\([0-9A-F]+)\\X0\\", m =>
        string.Concat(Enumerable.Range(0, m.Groups[1].Value.Length / 4).Select(i => (char)Convert.ToInt32(m.Groups[1].Value.Substring(i * 4, 4), 16))));
      return Regex.Replace(s, @"\\X\\([0-9A-F]{2})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
    }

    /// <summary>Splits STEP arguments at top-level commas, respecting quotes and parentheses.</summary>
    private static List<string> SplitArguments(string text)
    {
      var parts = new List<string>(); var depth = 0; var quoted = false; var start = 0;
      for (var i = 0; i < text.Length; i++)
      {
        var c = text[i];
        if (c == '\'') quoted = !quoted;
        else if (!quoted && c == '(') depth++;
        else if (!quoted && c == ')') depth--;
        else if (!quoted && depth == 0 && c == ',') { parts.Add(text[start..i]); start = i + 1; }
      }
      parts.Add(text[start..]);
      return parts;
    }
  }
}
