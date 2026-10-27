using CmConv.Lectura;
using OpenProject.Revit.LiveSync.Bim;

namespace CmConv.Semantica
{
  /// <summary>
  /// Pset_*Common and Qto_*BaseQuantities from the decoded parameters, with the rules of Cmcoordinator's
  /// BimElementReader (AddDirectProperties, AddDirectQuantities): only measured values, never estimates.
  /// Parameter ids are Revit's BuiltInParameter values (Revit 2026 API).
  /// </summary>
  public static class ConjuntosIfc
  {
    public const long HostArea = -1012805, HostVolume = -1012806, HostPerimeter = -1001953, CurveLength = -1004005;
    public const long OpeningWidth = -1001301, OpeningHeight = -1001300, FloorStructural = -1001954, WallStructural = -1001596;
    public const long Function = -1001006, WallWidth = -1001000, FloorThickness = -1001900, InstanceLength = -1001375, IfcGuid = -1019000;

    private static readonly HashSet<string> StructuralClasses = new() { "StructuralColumn", "StructuralFraming", "StructuralFoundation" };

    public const long BaseLevel = -1001350, TopLevel = -1001351, BaseOffset = -1001357, TopOffset = -1001358;

    public static List<IfcPropertySet> Build(RvtElement e, string ifcClass, IReadOnlyDictionary<long, RvtLevel> levels)
    {
      var sets = new List<IfcPropertySet>();
      var common = DirectIfcSemantics.CommonPropertySet(ifcClass);
      if (common != null)
      {
        if (!string.IsNullOrWhiteSpace(e.TypeName)) Add(sets, common, "pset", new IfcProperty { Name = "Reference", Value = e.TypeName });
        var structural = ifcClass == "IfcSlab" ? Integer(e, FloorStructural) : ifcClass == "IfcWall" ? Integer(e, WallStructural) : null;
        if (structural is long s) Add(sets, common, "pset", new IfcProperty { Name = "LoadBearing", Value = s != 0 });
        // Only Revit's structural categories carry loads by definition; architectural columns and mullions stay undeclared.
        else if (StructuralClasses.Contains(e.ClassName ?? "")) Add(sets, common, "pset", new IfcProperty { Name = "LoadBearing", Value = true });
        if (Integer(e, Function, "type") is long function) Add(sets, common, "pset", new IfcProperty { Name = "IsExternal", Value = function == 1 });
      }

      var quantities = DirectIfcSemantics.BaseQuantitySet(ifcClass);
      if (ifcClass == "IfcWall")
      {
        if (e.WallAxisFeet is double axis && axis > 0) Add(sets, "Qto_WallBaseQuantities", "qto", DirectIfcSemantics.Length("Length", axis));
        if ((Double(e, WallWidth, "type") ?? e.WallThicknessFeet) is double width && width > 0) Add(sets, "Qto_WallBaseQuantities", "qto", DirectIfcSemantics.Length("Width", width));
      }
      if (ifcClass is "IfcBeam" or "IfcMember" && Double(e, InstanceLength) is double length && length > 0)
        foreach (var property in DirectIfcSemantics.StraightLength(ifcClass, length)?.Properties ?? new List<IfcProperty>())
          Add(sets, DirectIfcSemantics.BaseQuantitySet(ifcClass), "qto", property);
      // Vertical columns run between their constrained base and top (levels plus offsets), as in Cmcoordinator.
      if (ifcClass == "IfcColumn")
      {
        double? Elevation(long parameter) => Reference(e, parameter) is long id && levels.TryGetValue(id, out var level) ? level.ElevationMetres / 0.3048 : null;
        var vertical = DirectIfcSemantics.VerticalLength(Elevation(BaseLevel), Double(e, BaseOffset), Elevation(TopLevel), Double(e, TopOffset));
        if (vertical != null) Add(sets, "Qto_ColumnBaseQuantities", "qto", vertical);
      }
      var segment = ifcClass switch { "IfcPipeSegment" => "Qto_PipeSegmentBaseQuantities", "IfcDuctSegment" => "Qto_DuctSegmentBaseQuantities", "IfcCableCarrierSegment" => "Qto_CableCarrierSegmentBaseQuantities", _ => null };
      if (segment != null && Double(e, CurveLength) is double run && run > 0) Add(sets, segment, "qto", DirectIfcSemantics.Length("Length", run));
      if (ifcClass is "IfcDoor" or "IfcWindow")
      {
        var set = ifcClass == "IfcDoor" ? "Qto_DoorBaseQuantities" : "Qto_WindowBaseQuantities";
        // Instance dimensions win over the type, as for any occurrence property.
        if ((Double(e, OpeningWidth, "instance") ?? Double(e, OpeningWidth, "type")) is double w && w > 0) Add(sets, set, "qto", DirectIfcSemantics.Length("Width", w));
        if ((Double(e, OpeningHeight, "instance") ?? Double(e, OpeningHeight, "type")) is double h && h > 0) Add(sets, set, "qto", DirectIfcSemantics.Length("Height", h));
      }
      if (ifcClass == "IfcSlab" && Double(e, HostVolume) is double slabVolume)
        foreach (var property in DirectIfcSemantics.SlabNetVolume(slabVolume)?.Properties ?? new List<IfcProperty>())
          Add(sets, "Qto_SlabBaseQuantities", "qto", property);
      if (quantities != null)
      {
        // Revit's computed area already excludes openings, so it is only NetArea. GrossArea is not claimed.
        if (Double(e, HostArea) is double area && area >= 0) Add(sets, quantities, "qto", DirectIfcSemantics.Area(ifcClass == "IfcWall" ? "NetSideArea" : "NetArea", area));
        if (Double(e, HostVolume) is double volume && volume >= 0) Add(sets, quantities, "qto", DirectIfcSemantics.Volume("NetVolume", volume));
        if (Double(e, HostPerimeter) is double perimeter && perimeter >= 0) Add(sets, quantities, "qto", DirectIfcSemantics.Length("Perimeter", perimeter));
        if (ifcClass == "IfcSlab" && Double(e, FloorThickness, "type") is double depth && depth > 0) Add(sets, quantities, "qto", DirectIfcSemantics.Length("Depth", depth));
      }
      return sets;
    }

    private static double? Double(RvtElement e, long id, string scope = null)
    {
      foreach (var p in e.Parameters)
        if (p.Id == id && (scope == null || p.Scope == scope) && p.Value is IConvertible c && p.Storage == "Double")
        {
          var value = c.ToDouble(System.Globalization.CultureInfo.InvariantCulture);
          if (double.IsFinite(value)) return value;
        }
      return null;
    }

    private static long? Reference(RvtElement e, long id)
    {
      foreach (var p in e.Parameters)
        if (p.Id == id && p.Storage == "ElementId" && p.Value is IConvertible c) return c.ToInt64(System.Globalization.CultureInfo.InvariantCulture);
      return null;
    }

    private static long? Integer(RvtElement e, long id, string scope = null)
    {
      foreach (var p in e.Parameters)
        if (p.Id == id && (scope == null || p.Scope == scope) && p.Storage == "Integer" && p.Value is IConvertible c)
          return c.ToInt64(System.Globalization.CultureInfo.InvariantCulture);
      return null;
    }

    private static void Add(List<IfcPropertySet> sets, string name, string kind, IfcProperty property)
    {
      if (name == null || property?.Value == null || property.Value is double d && !double.IsFinite(d)) return;
      var set = sets.Find(s => s.Name == name && s.Kind == kind);
      if (set == null) { set = new IfcPropertySet { Name = name, Kind = kind }; sets.Add(set); }
      if (set.Properties.TrueForAll(p => p.Name != property.Name)) set.Properties.Add(property);
    }
  }
}
