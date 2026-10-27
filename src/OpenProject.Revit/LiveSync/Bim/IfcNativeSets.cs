#if Version2026 || Version2027
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xbim.Ifc;
using Xbim.Ifc4.Interfaces;

namespace OpenProject.Revit.LiveSync.Bim
{
  // Read only values emitted by Revit's IFC exporter. No Pset is inferred from a Revit parameter name.
  internal static class IfcNativeSets
  {
    internal sealed class ElementData
    {
      public string IfcClass { get; init; }
      public List<IfcPropertySet> Sets { get; } = new();
    }

    public static Dictionary<string, List<IfcPropertySet>> Read(string path)
      => ReadElements(path).ToDictionary(pair => pair.Key, pair => pair.Value.Sets, StringComparer.Ordinal);

    public static Dictionary<string, ElementData> ReadElements(string path)
    {
      using var model = IfcStore.Open(path);
      var units = model.Instances.OfType<IIfcProject>().FirstOrDefault()?.UnitsInContext?.Units
        .OfType<IIfcNamedUnit>().ToDictionary(unit => unit.UnitType.ToString(), unit => UnitName(unit))
        ?? new Dictionary<string, string>();
      var result = new Dictionary<string, ElementData>(StringComparer.Ordinal);
      foreach (var element in model.Instances.OfType<IIfcElement>())
      {
        var guid = element.GlobalId.ToString();
        if (!string.IsNullOrWhiteSpace(guid))
          result[guid] = new ElementData { IfcClass = element.ExpressType.Name };
      }
      foreach (var relation in model.Instances.OfType<IIfcRelDefinesByProperties>())
      {
        var set = ReadDefinition(relation.RelatingPropertyDefinition, units);
        if (set?.Properties.Count is not > 0) continue;
        foreach (var element in relation.RelatedObjects.OfType<IIfcElement>())
        {
          var guid = element.GlobalId.ToString();
          if (result.TryGetValue(guid, out var data)) ReplaceByName(data.Sets, set);
        }
      }
      foreach (var relation in model.Instances.OfType<IIfcRelDefinesByType>())
      {
        var inherited = relation.RelatingType.HasPropertySets
          .Select(definition => ReadDefinition(definition, units))
          .Where(set => set?.Properties.Count > 0)
          .ToList();
        if (inherited.Count == 0) continue;
        foreach (var element in relation.RelatedObjects.OfType<IIfcElement>())
        {
          var guid = element.GlobalId.ToString();
          if (!result.TryGetValue(guid, out var data)) continue;
          foreach (var set in inherited)
          {
            if (!data.Sets.Any(existing => existing.Kind == set.Kind && existing.Name == set.Name)) data.Sets.Add(set);
          }
        }
      }
      return result;
    }

    private static IfcPropertySet ReadDefinition(IIfcPropertySetDefinitionSelect definition,
      IReadOnlyDictionary<string, string> units)
    {
      if (definition is IIfcPropertySet nativeSet && !string.IsNullOrWhiteSpace(nativeSet.Name?.ToString()))
      {
        var set = new IfcPropertySet { Name = nativeSet.Name.ToString(), Kind = "pset" };
        foreach (var property in nativeSet.HasProperties.OfType<IIfcPropertySingleValue>())
        {
          var raw = property.NominalValue?.Value;
          // NominalValue is optional in IFC. Keep the declared property even when
          // empty so the live contract has the same fields as the exported IFC.
          var type = property.NominalValue?.GetType().Name ?? "null";
          var unitType = type.Contains("PlaneAngleMeasure", StringComparison.Ordinal) ? "PLANEANGLEUNIT" :
            type.Contains("LengthMeasure", StringComparison.Ordinal) ? "LENGTHUNIT" :
            type.Contains("AreaMeasure", StringComparison.Ordinal) ? "AREAUNIT" :
            type.Contains("VolumeMeasure", StringComparison.Ordinal) ? "VOLUMEUNIT" :
            type.Contains("MassMeasure", StringComparison.Ordinal) ? "MASSUNIT" :
            type.Contains("TimeMeasure", StringComparison.Ordinal) ? "TIMEUNIT" : "";
          set.Properties.Add(new IfcProperty { Name = property.Name.ToString(), Value = raw == null ? null : Scalar(raw),
            Type = type, Unit = property.Unit == null ? units.GetValueOrDefault(unitType) : UnitName(property.Unit) });
        }
        return set;
      }
      if (definition is not IIfcElementQuantity nativeQto || string.IsNullOrWhiteSpace(nativeQto.Name?.ToString())) return null;
      var quantities = new IfcPropertySet { Name = nativeQto.Name.ToString(), Kind = "qto" };
      foreach (var quantity in nativeQto.Quantities)
      {
        var (value, unitType, type) = quantity switch
        {
          IIfcQuantityLength length => ((object)(double)length.LengthValue, "LENGTHUNIT", "length"),
          IIfcQuantityArea area => ((object)(double)area.AreaValue, "AREAUNIT", "area"),
          IIfcQuantityVolume volume => ((object)(double)volume.VolumeValue, "VOLUMEUNIT", "volume"),
          IIfcQuantityCount count => ((object)(double)count.CountValue, "", "count"),
          IIfcQuantityWeight weight => ((object)(double)weight.WeightValue, "MASSUNIT", "weight"),
          IIfcQuantityTime time => ((object)(double)time.TimeValue, "TIMEUNIT", "time"),
          _ => (null, "", "")
        };
        if (value == null) continue;
        var explicitUnit = (quantity as IIfcPhysicalSimpleQuantity)?.Unit;
        var unit = explicitUnit == null ? units.GetValueOrDefault(unitType) : UnitName(explicitUnit);
        quantities.Properties.Add(new IfcProperty { Name = quantity.Name.ToString(), Value = value, Unit = unit, Type = type });
      }
      return quantities;
    }

    private static void ReplaceByName(List<IfcPropertySet> sets, IfcPropertySet value)
    {
      var index = sets.FindIndex(existing => existing.Kind == value.Kind && existing.Name == value.Name);
      if (index < 0) sets.Add(value); else sets[index] = value;
    }

    private static object Scalar(object value) => value switch
    {
      bool or string or int or long or double or decimal => value,
      IConvertible convertible => convertible.GetTypeCode() switch
      {
        TypeCode.Boolean => convertible.ToBoolean(CultureInfo.InvariantCulture),
        TypeCode.Double or TypeCode.Decimal or TypeCode.Single => convertible.ToDouble(CultureInfo.InvariantCulture),
        TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64 => convertible.ToInt64(CultureInfo.InvariantCulture),
        _ => value.ToString()
      },
      _ => value.ToString()
    };

    private static string UnitName(IIfcUnit unit) => unit switch
    {
      IIfcSIUnit si => (si.Prefix?.ToString() switch { "MILLI" => "milli", "CENTI" => "centi", "KILO" => "kilo", _ => "" }) +
        (si.Name.ToString() switch { "METRE" => "metre", "SQUARE_METRE" => "square_metre", "CUBIC_METRE" => "cubic_metre", "GRAM" => "gram", "SECOND" => "second", "RADIAN" => "radian", _ => si.Name.ToString().ToLowerInvariant() }),
      IIfcConversionBasedUnit conversion => conversion.Name.ToString(),
      _ => unit.ToString()
    };
  }
}
#endif
