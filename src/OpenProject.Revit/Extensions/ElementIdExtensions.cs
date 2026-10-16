using System;
using Autodesk.Revit.DB;

namespace OpenProject.Revit.Extensions
{
  public static class ElementIdExtensions
  {
    public static long GetLongValue(this ElementId id)
    {
      if (id == null)
        return -1;

      var t = id.GetType();
      var intProp = t.GetProperty("IntegerValue");
      if (intProp != null)
        return Convert.ToInt64(intProp.GetValue(id));

      var valueProp = t.GetProperty("Value");
      if (valueProp != null)
        return Convert.ToInt64(valueProp.GetValue(id));

      return -1;
    }

    public static int GetIntValue(this ElementId id)
    {
      var value = id.GetLongValue();
      if (value > int.MaxValue)
        return int.MaxValue;
      if (value < int.MinValue)
        return int.MinValue;
      return (int)value;
    }
  }
}
