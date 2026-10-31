using System;
using System.Collections.Generic;
using System.Linq;
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
      result.Joined = JoinedTo(element);
      AddParameters(result, element, "");
      var type = element.Document.GetElement(element.GetTypeId());
      if (type != null) AddParameters(result, type, "Tipo · ");
      return result;
    }

    // Walls joined at their ends and elements with their geometry joined. An element that does not admit joins has none.
    private static List<string> JoinedTo(Element element)
    {
      var ids = new HashSet<ElementId>();
      try { foreach (var id in JoinGeometryUtils.GetJoinedElements(element.Document, element)) ids.Add(id); }
      catch (Autodesk.Revit.Exceptions.ApplicationException) { }
      if (element is Wall && element.Location is LocationCurve curve)
        for (var end = 0; end < 2; end++)
          try { foreach (Element other in curve.get_ElementsAtJoin(end)) ids.Add(other.Id); }
          catch (Autodesk.Revit.Exceptions.ApplicationException) { }
      ids.Remove(element.Id);
      return ids.Select(id => element.Document.GetElement(id)?.UniqueId).Where(id => id != null).Distinct().ToList();
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
