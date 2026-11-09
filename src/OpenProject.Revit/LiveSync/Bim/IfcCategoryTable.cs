using System;
using System.Collections.Generic;
using System.IO;

namespace OpenProject.Revit.LiveSync.Bim
{
  // Revit's IFC export classes table (exportlayers-ifc-IAI.txt), installed with each Revit under ProgramData.
  // Before Revit 2025 an add-in cannot read the project's own category mapping; this file is what the exporter starts from.
  internal sealed class IfcCategoryTable
  {
    public const string FileName = "exportlayers-ifc-IAI.txt";

    // Class null: the category is not exported to IFC.
    public sealed class Entry
    {
      public string Class { get; init; }
      public string Predefined { get; init; }
    }

    private static readonly Dictionary<string, IfcCategoryTable> Installed = new(StringComparer.Ordinal);
    private readonly Dictionary<long, Entry> _byCategory = new();

    public int Count => _byCategory.Count;

    public bool TryGet(long categoryId, out Entry entry) => _byCategory.TryGetValue(categoryId, out entry);

    // Category <tab> Subcategory <tab> Class name <tab> Type name <tab> <tab> Category id. Names are in Revit's
    // language, so only the id is used; subcategory rows and comments are skipped.
    public static IfcCategoryTable Parse(IEnumerable<string> lines)
    {
      var table = new IfcCategoryTable();
      foreach (var line in lines)
      {
        if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#", StringComparison.Ordinal)) continue;
        var fields = line.Split('\t');
        if (fields.Length < 4 || !string.IsNullOrWhiteSpace(fields[1])) continue;
        if (!long.TryParse(fields[fields.Length - 1].Trim(), out var id)) continue;
        var (ifcClass, suffix) = DirectIfcSemantics.ParseEntity(fields[2]);
        var predefined = suffix ?? DirectIfcSemantics.PredefinedToken(fields[3], out var free);
        // A free-text type name is not an enumeration value; it is ignored rather than guessed.
        if (predefined == "USERDEFINED" && suffix == null) predefined = null;
        table._byCategory[id] = new Entry { Class = ifcClass, Predefined = ifcClass == null ? null : predefined };
      }
      return table;
    }

    // The table installed with this Revit (cached per version); null when the file is missing or unreadable.
    public static IfcCategoryTable ForRevit(string revitVersion)
    {
      var key = revitVersion ?? "";
      lock (Installed)
      {
        if (Installed.TryGetValue(key, out var cached)) return cached;
        IfcCategoryTable table = null;
        try
        {
          var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Autodesk", "RVT " + key, FileName);
          // The file is UTF-16 with a byte order mark, which ReadAllLines detects.
          if (File.Exists(path)) table = Parse(File.ReadAllLines(path));
          if (table?.Count == 0) table = null;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        Installed[key] = table;
        return table;
      }
    }
  }
}
