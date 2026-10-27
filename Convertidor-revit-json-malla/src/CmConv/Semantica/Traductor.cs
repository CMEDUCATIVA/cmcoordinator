using System.Security.Cryptography;
using System.Text;
using CmConv.Lectura;
using Newtonsoft.Json.Linq;
using OpenProject.Revit.LiveSync;
using OpenProject.Revit.LiveSync.Bim;

namespace CmConv.Semantica
{
  /// <summary>
  /// Turns a decoded model into Cmcoordinator's live snapshot: the same element ids, categories, IFC classes,
  /// catalogue (levels, types) and parameter records the CDE already receives from Revit.
  /// </summary>
  public sealed class Traductor
  {
    private readonly Dictionary<string, ClassRule> _classes;
    private readonly Dictionary<string, ParameterName> _parameters;
    // System families are decoded with their English name (Basic Wall); Revit shows them in the project's language.
    private readonly Dictionary<string, string> _families;

    public Traductor(string dataDirectory)
    {
      _classes = new Dictionary<string, ClassRule>(StringComparer.Ordinal);
      foreach (var file in new[] { "tabla_clases.json", "tabla_clases_extra.json" })
        foreach (var (name, value) in JObject.Parse(File.ReadAllText(Path.Combine(dataDirectory, file))))
          if (!name.StartsWith("_") && value is JObject rule && !_classes.ContainsKey(name))
            _classes[name] = new ClassRule((string)rule["category"], (string)rule["ifcClass"], (string)rule["ifcPredefined"], (bool?)rule["omitir"] == true);
      _families = JObject.Parse(File.ReadAllText(Path.Combine(dataDirectory, "tabla_familias.json")))
        .Properties().ToDictionary(p => p.Name, p => (string)p.Value, StringComparer.Ordinal);
      _parameters = JObject.Parse(File.ReadAllText(Path.Combine(dataDirectory, "diccionario_parametros.json")))
        .Properties().ToDictionary(p => p.Name, p => new ParameterName((string)p.Value["name"], (string)p.Value["group"], (string)p.Value["storage"], (string)p.Value["spec"]));
    }

    public sealed record Result(LiveSnapshot Snapshot, Dictionary<string, int> Omitted, List<string> UnknownClasses);

    public Result Translate(RvtModel model, string sourcePath)
    {
      // Stable identities derived from the file content's document GUID (or its path), as Cmcoordinator derives them from the RVT.
      var documentId = model.DocumentGuid ?? Hash(Path.GetFullPath(sourcePath));
      var sourceId = "rvt:" + Hash(documentId)[..32];
      var bim = new BimContext { ScopeId = "rvt-archivo:" + Hash(documentId)[..16], Scope = "archivo-rvt" };
      bim.Documents.Add(new BimDocument { Id = documentId, Title = model.Title });
      foreach (var level in model.Levels.Values)
        bim.Levels[LevelKey(level.Id)] = new BimLevel { Id = LevelKey(level.Id), DocumentId = documentId, Name = level.Name, Elevation = level.ElevationMetres };

      var elements = new Dictionary<string, LiveElement>(StringComparer.Ordinal);
      var omitted = new Dictionary<string, int>();
      var unknown = new HashSet<string>();
      // A building element in the table stays even when the decoder draws no mesh for it (stairs of some versions):
      // its data, level and grouping are still right. Unknown classes without a mesh are decoder internals.
      bool Included(RvtElement e) => _classes.TryGetValue(e.ClassName ?? "", out var r) ? !r.Omit : e.Meshes.Count > 0 || IsAssembly(e);
      var included = model.Elements.Where(Included).Select(e => e.Id).ToHashSet();
      var byId = model.Elements.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
      foreach (var e in model.Elements)
      {
        _classes.TryGetValue(e.ClassName ?? "", out var rule);
        if (!included.Contains(e.Id))
        {
          omitted[e.ClassName ?? "?"] = omitted.GetValueOrDefault(e.ClassName ?? "?") + 1;
          continue;
        }
        if (rule == null) unknown.Add(e.ClassName);
        var ifcClass = DirectIfcSemantics.ClassName(rule?.IfcClass) ?? FromHint(e.IfcHint) ?? "IfcBuildingElementProxy";
        var predefined = rule?.Predefined;
        // Revit makes inclined framing a brace (StructuralType.Brace), which Cmcoordinator sends as IfcMember BRACE.
        if (ifcClass == "IfcBeam" && e.AxisInclinationDegrees > BraceInclinationDegrees) (ifcClass, predefined) = ("IfcMember", "BRACE");
        var id = "rvt-" + e.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string typeKey = null;
        if (e.TypeId is long typeId)
        {
          typeKey = "tipo-" + typeId;
          if (!bim.Types.ContainsKey(typeKey))
            bim.Types[typeKey] = new BimType { Id = typeKey, DocumentId = documentId, Name = e.TypeName,
              Family = e.FamilyName != null && _families.TryGetValue(e.FamilyName, out var local) ? local : e.FamilyName };
        }
        var element = new LiveElement
        {
          Id = id, ElementId = e.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
          Name = e.TypeName ?? e.FamilyName ?? e.ClassName, Category = rule?.Category ?? Humanize(e.ClassName),
          Bim = new BimElement
          {
            DocumentId = documentId, IfcGuid = e.IfcGuid, IfcClass = ifcClass, IfcPredefinedType = predefined,
            AggregateParentId = e.AggregateId is long parent && included.Contains(parent) ? "rvt-" + parent : null,
            TypeId = typeKey, LevelId = LevelOf(e, model, byId),
            CategoryId = e.CategoryId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Parameters = Parameters(e), IfcSets = ConjuntosIfc.Build(e, ifcClass, model.Levels)
          }
        };
        foreach (var mesh in e.Meshes)
          element.Meshes.Add(new LiveMesh { Positions = mesh.Positions, Indices = mesh.Indices, Color = mesh.Color, Opacity = mesh.Opacity });
        elements[id] = element;
      }
      var snapshot = new LiveSnapshot(sourceId, model.Title, "Archivo .rvt", elements, bim, documentId);
      return new Result(snapshot, omitted, unknown.Where(u => u != null).OrderBy(u => u).ToList());
    }

    private const double BraceInclinationDegrees = 5;

    // A part without its own level takes its assembly's, so flights and panels sit on their stair's or wall's storey.
    private static string LevelOf(RvtElement e, RvtModel model, Dictionary<long, RvtElement> byId)
    {
      for (var current = e; current != null; current = current.AggregateId is long p && p != current.Id ? byId.GetValueOrDefault(p) : null)
        if (current.LevelId is long l && model.Levels.ContainsKey(l)) return LevelKey(l);
      return LevelByHeight(e, model);
    }

    /// <summary>
    /// Last resort when the file records no level: the highest level at or below the element's base, or its top for
    /// framing (Revit's reference level sits at the top of a beam). Elements below every level take the lowest.
    /// </summary>
    private static string LevelByHeight(RvtElement e, RvtModel model)
    {
      if (e.Meshes.Count == 0 || model.Levels.Count == 0) return null;
      var zs = e.Meshes.SelectMany(m => Enumerable.Range(0, m.Positions.Length / 3).Select(i => m.Positions[i * 3 + 2])).ToList();
      var z = (e.ClassName == "StructuralFraming" ? zs.Max() : zs.Min()) + LevelTolerance;
      var levels = model.Levels.Values.OrderBy(l => l.ElevationMetres).ToList();
      return LevelKey((levels.LastOrDefault(l => l.ElevationMetres <= z) ?? levels[0]).Id);
    }

    private const double LevelTolerance = 0.01;

    private List<BimParameter> Parameters(RvtElement e)
    {
      var list = new List<BimParameter>();
      foreach (var p in e.Parameters)
      {
        if (p.Value == null && p.SiValue == null) continue;
        var key = p.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _parameters.TryGetValue(key, out var known);
        list.Add(new BimParameter
        {
          Id = key, Name = known?.Name ?? p.Name ?? "Parámetro " + key, GroupName = known?.Group ?? "Otros",
          Storage = p.Storage ?? known?.Storage, SpecId = known?.Spec, Value = p.Value, SiValue = p.SiValue ?? Si(p, known?.Spec),
          Display = p.Value is string s ? s : null, HasValue = true
        });
      }
      return list;
    }

    // Same conversion as Cmcoordinator: Revit internal feet to SI for lengths, areas and volumes.
    private static double? Si(RvtParameter p, string spec)
    {
      if (p.Storage != "Double" || p.Value is not IConvertible c || spec == null) return null;
      var raw = c.ToDouble(System.Globalization.CultureInfo.InvariantCulture);
      if (!double.IsFinite(raw)) return null;
      if (spec.Contains(":length-")) return raw * 0.3048;
      if (spec.Contains(":area-")) return raw * 0.09290304;
      if (spec.Contains(":volume-")) return raw * 0.028316846592;
      return null;
    }

    // Curtain walls and stairs draw nothing themselves; their panels, mullions and flights carry the geometry.
    private static bool IsAssembly(RvtElement e) => e.ClassName is "CurtainWall" or "Stair" or "CurtainSystem";

    private static string FromHint(string hint)
    {
      if (string.IsNullOrWhiteSpace(hint)) return null;
      // rvt-rs writes IFC entity names in upper case (IFCWALL); restore the schema casing for the common ones.
      var known = new[] { "IfcWall", "IfcSlab", "IfcRoof", "IfcDoor", "IfcWindow", "IfcColumn", "IfcBeam", "IfcMember", "IfcPlate",
        "IfcCovering", "IfcRailing", "IfcStair", "IfcStairFlight", "IfcFurniture", "IfcLightFixture", "IfcSpace", "IfcCurtainWall",
        "IfcDuctSegment", "IfcDuctFitting", "IfcPipeSegment", "IfcPipeFitting", "IfcAirTerminal", "IfcFlowTerminal", "IfcOutlet",
        "IfcSwitchingDevice", "IfcElectricAppliance", "IfcUnitaryEquipment", "IfcFlowController", "IfcValve", "IfcPump", "IfcFan",
        "IfcSanitaryTerminal", "IfcFooting", "IfcPile", "IfcRamp", "IfcGeographicElement", "IfcBuildingElementProxy",
        "IfcElectricDistributionBoard", "IfcCableCarrierSegment", "IfcCableCarrierFitting", "IfcDamper", "IfcSensor", "IfcAlarm" };
      return known.FirstOrDefault(k => string.Equals(k, hint, StringComparison.OrdinalIgnoreCase));
    }

    private static string Humanize(string className) =>
      string.IsNullOrEmpty(className) ? "Sin categoría" : System.Text.RegularExpressions.Regex.Replace(className, "(?<=[a-z])(?=[A-Z])", " ");

    private static string LevelKey(long id) => "nivel-" + id;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record ClassRule(string Category, string IfcClass, string Predefined, bool Omit);
    private sealed record ParameterName(string Name, string Group, string Storage, string Spec);
  }
}
