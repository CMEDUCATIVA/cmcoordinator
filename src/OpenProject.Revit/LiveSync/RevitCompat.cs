using System;
using Autodesk.Revit.DB;

namespace OpenProject.Revit.LiveSync
{
  // One reading of parameter specs, groups and units for Revit 2019–2027. Revit 2021 moved units to ForgeTypeId and
  // Revit 2022 moved specs and groups; older versions are mapped to the same spec ids the newer ones return.
  internal static class RevitCompat
  {
    public static string SpecId(Definition definition)
    {
#if REVIT2022_OR_GREATER
      return definition.GetDataType()?.TypeId ?? "";
#elif REVIT2021_OR_GREATER
      return definition.GetSpecTypeId()?.TypeId ?? "";
#else
      return definition.ParameterType switch
      {
        ParameterType.Length => "autodesk.spec.aec:length-2.0.0",
        ParameterType.Area => "autodesk.spec.aec:area-2.0.0",
        ParameterType.Volume => "autodesk.spec.aec:volume-2.0.0",
        ParameterType.Angle => "autodesk.spec.aec:angle-2.0.0",
        ParameterType.YesNo => "autodesk.spec:spec.bool-1.0.0",
        ParameterType.Text => "autodesk.spec:spec.string-2.0.0",
        ParameterType.Integer => "autodesk.spec:spec.int64-2.0.0",
        ParameterType.Number => "autodesk.spec:spec.double-2.0.0",
        _ => ""
      };
#endif
    }

    public static string GroupId(Definition definition)
    {
#if REVIT2022_OR_GREATER
      return definition.GetGroupTypeId()?.TypeId ?? "";
#else
      return definition.ParameterGroup.ToString();
#endif
    }

    // Name of the parameter group as Revit shows it, or "Otros" when it has none.
    public static string GroupLabel(Definition definition)
    {
      try
      {
#if REVIT2022_OR_GREATER
        var label = LabelUtils.GetLabelForGroup(definition.GetGroupTypeId());
#else
        var label = LabelUtils.GetLabelFor(definition.ParameterGroup);
#endif
        return string.IsNullOrWhiteSpace(label) ? "Otros" : label;
      }
      catch (Autodesk.Revit.Exceptions.ArgumentException) { return "Otros"; }
      catch (ArgumentException) { return "Otros"; }
    }

    // The value in the unit the project displays, for measurable numbers only.
    public static bool TryDisplayUnit(Parameter parameter, double raw, out string unitId, out double value)
    {
      unitId = null; value = 0;
      try
      {
#if REVIT2022_OR_GREATER
        if (!UnitUtils.IsMeasurableSpec(parameter.Definition.GetDataType())) return false;
        var unit = parameter.GetUnitTypeId();
        unitId = unit.TypeId; value = UnitUtils.ConvertFromInternalUnits(raw, unit); return true;
#elif REVIT2021_OR_GREATER
        var unit = parameter.GetUnitTypeId();
        if (unit == null || string.IsNullOrEmpty(unit.TypeId)) return false;
        unitId = unit.TypeId; value = UnitUtils.ConvertFromInternalUnits(raw, unit); return true;
#else
        if (parameter.Definition.UnitType == UnitType.UT_Undefined) return false;
        var unit = parameter.DisplayUnitType;
        unitId = unit.ToString(); value = UnitUtils.ConvertFromInternalUnits(raw, unit); return true;
#endif
      }
      // Numbers without a unit (counts, ratios) have no display unit; Revit reports that with an exception.
      catch (Autodesk.Revit.Exceptions.ApplicationException) { unitId = null; value = 0; return false; }
    }

    public static double MetersToInternal(double meters)
    {
#if REVIT2021_OR_GREATER
      return UnitUtils.ConvertToInternalUnits(meters, UnitTypeId.Meters);
#else
      return UnitUtils.ConvertToInternalUnits(meters, DisplayUnitType.DUT_METERS);
#endif
    }

    public static double InternalToMeters(double internalUnits)
    {
#if REVIT2021_OR_GREATER
      return UnitUtils.ConvertFromInternalUnits(internalUnits, UnitTypeId.Meters);
#else
      return UnitUtils.ConvertFromInternalUnits(internalUnits, DisplayUnitType.DUT_METERS);
#endif
    }

    // Link instance that holds an element drawn in a 2D view; Revit 2019–2020 do not report it.
    public static ElementId LinkInstanceId(ElementNode node)
    {
#if REVIT2021_OR_GREATER
      return node.LinkInstanceId;
#else
      return ElementId.InvalidElementId;
#endif
    }
  }
}
