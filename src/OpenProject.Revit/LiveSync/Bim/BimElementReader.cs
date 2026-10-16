using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace OpenProject.Revit.LiveSync.Bim
{
  internal static class BimElementReader
  {
    private static readonly Dictionary<BuiltInCategory, string> Classes = new()
    {
      [BuiltInCategory.OST_Walls] = "IfcWall", [BuiltInCategory.OST_Floors] = "IfcSlab",
      [BuiltInCategory.OST_StructuralColumns] = "IfcColumn", [BuiltInCategory.OST_Columns] = "IfcColumn",
      [BuiltInCategory.OST_StructuralFraming] = "IfcBeam", [BuiltInCategory.OST_StructuralFoundation] = "IfcFooting",
      [BuiltInCategory.OST_Doors] = "IfcDoor", [BuiltInCategory.OST_Windows] = "IfcWindow",
      [BuiltInCategory.OST_Roofs] = "IfcRoof", [BuiltInCategory.OST_Stairs] = "IfcStair",
      [BuiltInCategory.OST_Railings] = "IfcRailing", [BuiltInCategory.OST_Furniture] = "IfcFurniture",
      [BuiltInCategory.OST_PipeCurves] = "IfcPipeSegment", [BuiltInCategory.OST_DuctCurves] = "IfcDuctSegment",
      [BuiltInCategory.OST_CableTray] = "IfcCableCarrierSegment", [BuiltInCategory.OST_Rebar] = "IfcReinforcingBar"
    };
    public static BimElement Read(Element element)
    {
      var categoryId = element.Category?.Id.ToString();
      var category = categoryId != null && long.TryParse(categoryId, out var value) ? (BuiltInCategory)value : default;
      var level = element.Document.GetElement(element.LevelId);
      if (level == null)
        foreach (var name in new[] { BuiltInParameter.FAMILY_LEVEL_PARAM, BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM, BuiltInParameter.WALL_BASE_CONSTRAINT })
        { var p = element.get_Parameter(name); if (p?.StorageType == StorageType.ElementId) level = element.Document.GetElement(p.AsElementId()); if (level is Level) break; }
      return new BimElement { DocumentId = element.Document.ProjectInformation.UniqueId,
        IfcGuid = element.get_Parameter(BuiltInParameter.IFC_GUID)?.AsString(),
        IfcClass = Classes.TryGetValue(category, out var ifcClass) ? ifcClass : "IfcBuildingElementProxy",
        TypeId = element.Document.GetElement(element.GetTypeId())?.UniqueId, LevelId = (level as Level)?.UniqueId,
        HostId = (element as FamilyInstance)?.Host?.UniqueId,
        PhaseId = element.Document.GetElement(element.CreatedPhaseId)?.UniqueId,
        CategoryId = categoryId, Parameters = BimParameters.Read(element) };
    }
  }
}
