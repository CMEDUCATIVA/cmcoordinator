using System;
using System.Collections.Generic;

namespace OpenProject.Revit.LiveSync.Bim
{
  // Rules that do not depend on a particular RVT, language or Revit UI state.
  internal static class DirectIfcSemantics
  {
    private static readonly Dictionary<string, string> LinearQuantities = new(StringComparer.Ordinal)
    {
      ["IfcBeam"] = "Qto_BeamBaseQuantities",
      ["IfcColumn"] = "Qto_ColumnBaseQuantities",
      ["IfcMember"] = "Qto_MemberBaseQuantities"
    };

    private static readonly Dictionary<string, string> BaseQuantities = new(StringComparer.Ordinal)
    {
      ["IfcBeam"] = "Qto_BeamBaseQuantities", ["IfcColumn"] = "Qto_ColumnBaseQuantities",
      ["IfcMember"] = "Qto_MemberBaseQuantities", ["IfcWall"] = "Qto_WallBaseQuantities",
      ["IfcSlab"] = "Qto_SlabBaseQuantities", ["IfcRoof"] = "Qto_RoofBaseQuantities",
      ["IfcPlate"] = "Qto_PlateBaseQuantities", ["IfcCovering"] = "Qto_CoveringBaseQuantities",
      ["IfcDoor"] = "Qto_DoorBaseQuantities", ["IfcWindow"] = "Qto_WindowBaseQuantities",
      ["IfcPipeSegment"] = "Qto_PipeSegmentBaseQuantities", ["IfcDuctSegment"] = "Qto_DuctSegmentBaseQuantities",
      ["IfcCableCarrierSegment"] = "Qto_CableCarrierSegmentBaseQuantities"
    };

    private static readonly Dictionary<string, string> CommonPropertySets = new(StringComparer.Ordinal)
    {
      ["IfcBeam"] = "Pset_BeamCommon", ["IfcColumn"] = "Pset_ColumnCommon",
      ["IfcMember"] = "Pset_MemberCommon", ["IfcWall"] = "Pset_WallCommon",
      ["IfcSlab"] = "Pset_SlabCommon", ["IfcRoof"] = "Pset_RoofCommon",
      ["IfcPlate"] = "Pset_PlateCommon", ["IfcCovering"] = "Pset_CoveringCommon",
      ["IfcDoor"] = "Pset_DoorCommon", ["IfcWindow"] = "Pset_WindowCommon",
      ["IfcRailing"] = "Pset_RailingCommon", ["IfcStair"] = "Pset_StairCommon",
      ["IfcStairFlight"] = "Pset_StairFlightCommon", ["IfcCurtainWall"] = "Pset_CurtainWallCommon",
      ["IfcBuildingElementProxy"] = "Pset_BuildingElementProxyCommon"
    };

    // buildingSMART classes for Revit categories whose meaning does not depend on the project template.
    // They win over the template (an old template maps mullions and panels to IfcCurtainWall),
    // but never over an explicit IfcExportAs chosen by the author.
    private static readonly Dictionary<string, (string Class, string Predefined)> NormativeCategories = new(StringComparer.Ordinal)
    {
      ["OST_CurtainWallMullions"] = ("IfcMember", "MULLION"),
      ["OST_CurtainWallPanels"] = ("IfcPlate", "CURTAIN_PANEL"),
      ["OST_CurtaSystem"] = ("IfcCurtainWall", null),
      ["OST_Topography"] = ("IfcGeographicElement", "TERRAIN"),
      ["OST_Toposolid"] = ("IfcGeographicElement", "TERRAIN"),
      ["OST_Planting"] = ("IfcGeographicElement", "VEGETATION"),
      ["OST_StairsRuns"] = ("IfcStairFlight", null),
      ["OST_StairsLandings"] = ("IfcSlab", "LANDING"),
      ["OST_StairsStringerCarriage"] = ("IfcMember", "STRINGER"),
      ["OST_StructuralFramingSystem"] = ("IfcElementAssembly", "BEAM_GRID")
    };

    // Same defaults as the Autodesk exporter when nothing more specific is declared.
    private static readonly Dictionary<string, string> DefaultPredefinedTypes = new(StringComparer.Ordinal)
    {
      ["OST_Floors|IfcSlab"] = "FLOOR", ["OST_Ceilings|IfcCovering"] = "CEILING",
      ["OST_StructuralColumns|IfcColumn"] = "COLUMN", ["OST_Columns|IfcColumn"] = "COLUMN",
      ["OST_StructuralFraming|IfcBeam"] = "BEAM"
    };

    public static (string Class, string Predefined)? NormativeCategory(string category) =>
      NormativeCategories.TryGetValue(category ?? "", out var rule) ? rule : null;

    public static string DefaultPredefinedType(string category, string ifcClass) =>
      DefaultPredefinedTypes.TryGetValue((category ?? "") + "|" + (ifcClass ?? ""), out var value) ? value : null;

    // "IfcSlab.ROOF" carries the predefined type after the dot, as in Revit's IfcExportAs parameter.
    public static (string Class, string Predefined) ParseEntity(string candidate)
    {
      if (string.IsNullOrWhiteSpace(candidate)) return (null, null);
      var text = candidate.Trim();
      var dot = text.IndexOf('.');
      // A suffix that is not an enumeration value is ignored rather than guessed.
      string predefined = null;
      if (dot > 0)
      {
        predefined = PredefinedToken(text.Substring(dot + 1), out var free);
        if (free != null) predefined = null;
      }
      var name = ClassName(dot > 0 ? text.Substring(0, dot) : text);
      return name == null ? (null, null) : (OccurrenceClass(name), predefined);
    }

    // A type object (IfcLightFixtureType, IfcDoorStyle) never classifies a placed element:
    // buildingSMART assigns it to the occurrence through IfcRelDefinesByType.
    public static string OccurrenceClass(string ifcClass)
    {
      if (ifcClass == null) return null;
      if (ifcClass is "IfcDoorStyle" or "IfcWindowStyle") return ifcClass.Substring(0, ifcClass.Length - 5);
      if (ifcClass.EndsWith("Type", StringComparison.Ordinal) && ifcClass.Length > "IfcType".Length &&
          ifcClass is not ("IfcTypeObject" or "IfcTypeProduct" or "IfcTypeProcess" or "IfcTypeResource"))
        return ifcClass.Substring(0, ifcClass.Length - 4);
      return ifcClass;
    }

    // IfcSite is the single spatial container; physical terrain and extra site elements are geographic elements in IFC4.
    public static string NormalizeSite(string ifcClass) => ifcClass == "IfcSite" ? "IfcGeographicElement" : ifcClass;

    // Enumerated values are upper-case identifiers; any other text becomes USERDEFINED with that text as ObjectType.
    public static string PredefinedToken(string candidate, out string objectType)
    {
      objectType = null;
      if (string.IsNullOrWhiteSpace(candidate)) return null;
      var text = candidate.Trim();
      var token = text.Length is > 0 and <= 64 && text[0] >= 'A' && text[0] <= 'Z';
      foreach (var c in text)
        if (!(c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '_')) { token = false; break; }
      if (token) return text;
      objectType = text;
      return "USERDEFINED";
    }

    // Vertical columns have no location curve; their length runs between the constrained base and top.
    public static IfcProperty VerticalLength(double? baseElevation, double? baseOffset, double? topElevation, double? topOffset)
    {
      if (baseElevation == null || topElevation == null) return null;
      var feet = topElevation.Value + (topOffset ?? 0) - baseElevation.Value - (baseOffset ?? 0);
      return feet > 0 ? Length("Length", feet) : null;
    }

    public static string ClassName(string candidate)
    {
      if (string.IsNullOrWhiteSpace(candidate)) return null;
      var name = candidate.Trim();
      if (!name.StartsWith("Ifc", StringComparison.Ordinal) || name.Length < 4 || name.Length > 80) return null;
      foreach (var c in name)
        if (!(c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9')) return null;
      return name;
    }

    // IFC4 deprecates instantiating the generic distribution supertypes (IfcFlowTerminal, IfcFlowSegment…);
    // when the template names one, the category's specific subtype is the buildingSMART occurrence.
    private static readonly Dictionary<string, string> DistributionSupertypes = new(StringComparer.Ordinal)
    {
      ["IfcSanitaryTerminal"] = "IfcFlowTerminal", ["IfcLightFixture"] = "IfcFlowTerminal",
      ["IfcPipeSegment"] = "IfcFlowSegment", ["IfcDuctSegment"] = "IfcFlowSegment", ["IfcCableCarrierSegment"] = "IfcFlowSegment"
    };

    public static string PreferTypedClass(string mappedClass, string categoryClass)
    {
      var mapped = ClassName(mappedClass);
      var fallback = ClassName(categoryClass);
      if (fallback == null || fallback == "IfcBuildingElementProxy") return mapped ?? fallback;
      return mapped == "IfcBuildingElementProxy" || mapped != null && DistributionSupertypes.TryGetValue(fallback, out var parent) && parent == mapped
        ? fallback : mapped ?? fallback;
    }

    public static string BaseQuantitySet(string ifcClass) =>
      BaseQuantities.TryGetValue(ifcClass ?? "", out var name) ? name : null;

    public static string CommonPropertySet(string ifcClass) =>
      CommonPropertySets.TryGetValue(ifcClass ?? "", out var name) ? name : null;

    public static string AdditionalSetName(bool isType, string group, string typeName = null)
    {
      var safeGroup = string.IsNullOrWhiteSpace(group) ? "Otros" : group.Trim();
      if (!isType) return "Instancia · " + safeGroup;
      return "Tipo · " + safeGroup + (string.IsNullOrWhiteSpace(typeName) ? "" : " · " + typeName.Trim());
    }

    public static IfcProperty Length(string name, double internalFeet) =>
      Measure(name, internalFeet * 0.3048, "metre", "length");

    public static IfcProperty Area(string name, double internalSquareFeet) =>
      Measure(name, internalSquareFeet * 0.09290304, "square_metre", "area");

    public static IfcProperty Volume(string name, double internalCubicFeet) =>
      Measure(name, internalCubicFeet * 0.028316846592, "cubic_metre", "volume");

    private static IfcProperty Measure(string name, double value, string unit, string type)
    {
      if (string.IsNullOrWhiteSpace(name) || !NetCompat.IsFinite(value) || value < 0) return null;
      return new IfcProperty { Name = name, Value = value, Unit = unit, Type = type };
    }

    public static IfcPropertySet StraightLength(string ifcClass, double internalFeet)
    {
      if (!LinearQuantities.TryGetValue(ifcClass ?? "", out var name)) return null;
      var length = Length("Length", internalFeet);
      if (length == null || (double)length.Value <= 0) return null;
      return new IfcPropertySet { Name = name, Kind = "qto",
        Properties = new List<IfcProperty> { length } };
    }

    public static IfcPropertySet SlabNetVolume(double internalCubicFeet)
    {
      var volume = Volume("NetVolume", internalCubicFeet);
      if (volume == null) return null;
      return new IfcPropertySet { Name = "Qto_SlabBaseQuantities", Kind = "qto",
        Properties = new List<IfcProperty> { volume } };
    }
  }
}
