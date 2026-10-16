using System;
using Autodesk.Revit.DB;

namespace OpenProject.Revit.LiveSync
{
  internal static class LiveMetadata
  {
    public static LiveElement Read(Element element, string key)
    {
      var result = new LiveElement
      {
        Id = key, ElementId = element.Id.ToString(),
        Name = Limit(string.IsNullOrWhiteSpace(element.Name) ? "Elemento" : element.Name, 512),
        Category = Limit(element.Category?.Name ?? "Sin categoría", 512)
      };
      result.Bim = Bim.BimElementReader.Read(element);
      AddParameters(result, element, "");
      var type = element.Document.GetElement(element.GetTypeId());
      if (type != null) AddParameters(result, type, "Tipo · ");
      return result;
    }

    private static void AddParameters(LiveElement result, Element element, string prefix)
    {
      foreach (Parameter parameter in element.GetOrderedParameters())
      {
        if (result.Properties.Count >= 128) break;
        if (!parameter.HasValue) continue;
        try
        {
          var value = parameter.StorageType == StorageType.String
            ? parameter.AsString() : parameter.AsValueString();
          if (!string.IsNullOrWhiteSpace(value))
            result.Properties[Limit(prefix + parameter.Definition.Name, 512)] = Limit(value, 2048);
        }
        catch (Autodesk.Revit.Exceptions.InvalidOperationException) { }
      }
    }
    private static string Limit(string value, int length) => value.Length > length ? value.Substring(0, length) : value;
  }
}
