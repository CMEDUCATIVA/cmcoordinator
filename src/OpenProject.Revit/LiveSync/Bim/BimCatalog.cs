using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace OpenProject.Revit.LiveSync.Bim
{
  internal static class BimCatalog
  {
    public static BimContext Read(Document root, View3D view, IEnumerable<LiveElement> elements)
    {
      var context = new BimContext { ScopeId = view.UniqueId };
      var documents = new Dictionary<string, Document>();
      AddDocument(root, documents);
      var captured = elements.ToList();
      foreach (var pair in documents)
      {
        var document = pair.Value;
        var position = document.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);
        var transform = document.ActiveProjectLocation.GetTotalTransform();
        context.Documents.Add(new BimDocument { Id = pair.Key, Title = document.Title, Number = document.ProjectInformation.Number,
          LatitudeRadians = document.SiteLocation.Latitude, LongitudeRadians = document.SiteLocation.Longitude,
          Parameters = BimParameters.Read(document.ProjectInformation),
          ProjectPosition = new[] { position.EastWest * 0.3048, position.NorthSouth * 0.3048, position.Elevation * 0.3048, position.Angle },
          ProjectLocationTransform = new[] { transform.BasisX.X, transform.BasisX.Y, transform.BasisX.Z, transform.BasisY.X, transform.BasisY.Y, transform.BasisY.Z,
            transform.BasisZ.X, transform.BasisZ.Y, transform.BasisZ.Z, transform.Origin.X * 0.3048, transform.Origin.Y * 0.3048, transform.Origin.Z * 0.3048 } });
        foreach (Level level in new FilteredElementCollector(document).OfClass(typeof(Level)))
          context.Levels[level.UniqueId] = new BimLevel { Id = level.UniqueId, DocumentId = pair.Key, Name = level.Name, Elevation = level.Elevation * 0.3048 };
        foreach (Phase phase in document.Phases) context.Phases[phase.UniqueId] = phase.Name;
        foreach (var typeId in captured.Where(e => e.Bim?.DocumentId == pair.Key).Select(e => e.Bim.TypeId).Where(id => id != null).Distinct())
        {
          if (document.GetElement(typeId) is not ElementType type) continue;
          var data = new BimType { Id = typeId, DocumentId = pair.Key, Name = type.Name, Family = type.FamilyName,
            IfcGuid = type.get_Parameter(BuiltInParameter.IFC_TYPE_GUID)?.AsString(), Parameters = BimParameters.Read(type) };
          if (type is HostObjAttributes host)
          {
            using var compound = host.GetCompoundStructure();
            if (compound != null) foreach (var layer in compound.GetLayers())
            {
              var material = document.GetElement(layer.MaterialId) as Material;
              data.Layers.Add(new BimLayer { MaterialId = material?.UniqueId, Thickness = layer.Width * 0.3048, Function = layer.Function.ToString() });
              if (material != null) AddMaterial(context, material);
            }
          }
          context.Types[typeId] = data;
        }
        foreach (var id in captured.Where(e => e.Bim?.DocumentId == pair.Key).SelectMany(e => e.Meshes).Select(m => m.MaterialId).Where(id => id != null).Distinct())
          if (document.GetElement(id) is Material material) AddMaterial(context, material);
      }
      return context;
    }
    private static void AddMaterial(BimContext context, Material material)
    {
      var color = material.Color;
      context.Materials[material.UniqueId] = new BimMaterial { Id = material.UniqueId, Name = material.Name, Category = material.MaterialCategory,
        Color = color.IsValid ? $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}" : "#8bb8c9", Opacity = 1 - material.Transparency / 100.0,
        Parameters = BimParameters.Read(material) };
    }
    private static void AddDocument(Document document, Dictionary<string, Document> result)
    {
      if (!result.TryAdd(document.ProjectInformation.UniqueId, document)) return;
      foreach (RevitLinkInstance link in new FilteredElementCollector(document).OfClass(typeof(RevitLinkInstance)))
        if (link.GetLinkDocument() is Document child) AddDocument(child, result);
    }
  }
}
