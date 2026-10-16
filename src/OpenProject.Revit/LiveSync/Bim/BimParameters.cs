using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace OpenProject.Revit.LiveSync.Bim
{
  internal static class BimParameters
  {
    // Parameter IDs, including shared GUIDs, avoid collisions between equal display names.
    public static List<BimParameter> Read(Element element)
    {
      var result = new List<BimParameter>();
      foreach (Parameter parameter in element.Parameters)
      {
        var item = new BimParameter { Id = parameter.Id.ToString(), Name = parameter.Definition.Name,
          Storage = parameter.StorageType.ToString(), ReadOnly = parameter.IsReadOnly, HasValue = parameter.HasValue,
          SharedGuid = parameter.IsShared ? parameter.GUID.ToString() : null,
          SpecId = parameter.Definition.GetDataType().TypeId };
        if (parameter.HasValue)
        {
          item.Display = parameter.AsValueString();
          switch (parameter.StorageType)
          {
            case StorageType.String: item.Value = parameter.AsString(); item.Display = parameter.AsString(); break;
            case StorageType.Integer: item.Value = parameter.AsInteger(); break;
            case StorageType.Double:
              var raw = parameter.AsDouble();
              if (!double.IsFinite(raw)) throw new InvalidOperationException("Un parámetro contiene un número no finito.");
              item.Value = raw;
              var spec = item.SpecId ?? "";
              if (spec.Contains(":length-")) item.SiValue = raw * 0.3048;
              else if (spec.Contains(":area-")) item.SiValue = raw * 0.3048 * 0.3048;
              else if (spec.Contains(":volume-")) item.SiValue = raw * 0.3048 * 0.3048 * 0.3048;
              else if (spec.Contains(":angle-")) item.SiValue = raw;
              if (UnitUtils.IsMeasurableSpec(parameter.Definition.GetDataType()))
              { var unit = parameter.GetUnitTypeId(); item.UnitId = unit.TypeId; item.DisplayUnitValue = UnitUtils.ConvertFromInternalUnits(raw, unit); }
              break;
            case StorageType.ElementId:
              var id = parameter.AsElementId(); item.Value = id.ToString();
              item.ReferenceId = element.Document.GetElement(id)?.UniqueId; break;
          }
        }
        result.Add(item);
      }
      return result;
    }
  }
}
