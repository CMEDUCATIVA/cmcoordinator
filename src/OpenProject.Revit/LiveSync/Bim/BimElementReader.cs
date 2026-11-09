using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace OpenProject.Revit.LiveSync.Bim
{
  internal static class BimElementReader
  {
    private static readonly Dictionary<BuiltInCategory, string> Classes = new()
    {
      [BuiltInCategory.OST_Walls] = "IfcWall", [BuiltInCategory.OST_Floors] = "IfcSlab",
      [BuiltInCategory.OST_StructuralColumns] = "IfcColumn", [BuiltInCategory.OST_Columns] = "IfcColumn",
      [BuiltInCategory.OST_StructuralFraming] = "IfcBeam", [BuiltInCategory.OST_StructuralFoundation] = "IfcSlab",
      [BuiltInCategory.OST_StructuralStiffener] = "IfcPlate",
      [BuiltInCategory.OST_Doors] = "IfcDoor", [BuiltInCategory.OST_Windows] = "IfcWindow",
      [BuiltInCategory.OST_Roofs] = "IfcRoof", [BuiltInCategory.OST_Stairs] = "IfcStair",
      [BuiltInCategory.OST_Railings] = "IfcRailing", [BuiltInCategory.OST_StairsRailing] = "IfcRailing",
      [BuiltInCategory.OST_RailingTopRail] = "IfcRailing", [BuiltInCategory.OST_RailingHandRail] = "IfcRailing",
      [BuiltInCategory.OST_Furniture] = "IfcFurniture", [BuiltInCategory.OST_Casework] = "IfcFurniture",
      [BuiltInCategory.OST_FurnitureSystems] = "IfcSystemFurnitureElement", [BuiltInCategory.OST_PlumbingFixtures] = "IfcSanitaryTerminal",
      [BuiltInCategory.OST_PipeCurves] = "IfcPipeSegment", [BuiltInCategory.OST_DuctCurves] = "IfcDuctSegment",
      [BuiltInCategory.OST_CableTray] = "IfcCableCarrierSegment", [BuiltInCategory.OST_Rebar] = "IfcReinforcingBar",
      [BuiltInCategory.OST_Ceilings] = "IfcCovering", [BuiltInCategory.OST_LightingFixtures] = "IfcLightFixture"
    };
    public static BimElement Read(Element element)
    {
      var categoryId = element.Category?.Id.ToString();
      var category = categoryId != null && long.TryParse(categoryId, out var value) ? (BuiltInCategory)value : default;
      var level = element.Document.GetElement(element.LevelId) ?? BeamSystemLevel(element);
      // Stairs keep their storey in "Base level"; their flights, landings and stringers inherit it in the viewer.
      if (level == null)
        foreach (var name in new[] { BuiltInParameter.FAMILY_LEVEL_PARAM, BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM, BuiltInParameter.WALL_BASE_CONSTRAINT, BuiltInParameter.STAIRS_BASE_LEVEL_PARAM })
        { var p = element.get_Parameter(name); if (p?.StorageType == StorageType.ElementId) level = element.Document.GetElement(p.AsElementId()); if (level is Level) break; }
      var host = (element as FamilyInstance)?.Host;
      var classification = ResolveClass(element, category);
      var result = new BimElement { DocumentId = element.Document.ProjectInformation.UniqueId,
        IfcGuid = element.get_Parameter(BuiltInParameter.IFC_GUID)?.AsString(),
        IfcClass = classification.Class, IfcPredefinedType = classification.Predefined, IfcObjectType = classification.ObjectType,
        AggregateParentId = AggregateParent(element, host),
        TypeId = element.Document.GetElement(element.GetTypeId())?.UniqueId, LevelId = (level as Level)?.UniqueId,
        HostId = host?.UniqueId,
        PhaseId = element.Document.GetElement(element.CreatedPhaseId)?.UniqueId,
        CategoryId = categoryId, Parameters = BimParameters.Read(element) };
      AddDirectProperties(element, category, result);
      AddDirectQuantities(element, result);
      return result;
    }

    // Order follows the Autodesk exporter of each Revit: «Export to IFC» and IfcExportAs (instance, then type), assembly
    // rules, the category mapping, then the built-in table. The mapping is the project template from Revit 2025; before,
    // an add-in cannot read the project's own table, so the table installed with that Revit is used. Classes are
    // normalised to buildingSMART occurrences; IfcExportType (instance, then type) refines the predefined type.
    private static (string Class, string Predefined, string ObjectType) ResolveClass(Element element, BuiltInCategory category)
    {
      var categoryName = category.ToString();
      var categoryClass = Classes.TryGetValue(category, out var knownClass) ? knownClass : "IfcBuildingElementProxy";
      string ifcClass = null, predefined = null, objectType = null;
      var type = element.Document.GetElement(element.GetTypeId());
      var instanceAs = ExportText(element, false, ExportField.Class);
      var typeAs = ExportText(type, true, ExportField.Class);
      if (DirectIfcSemantics.IsDontExport(instanceAs) || DirectIfcSemantics.IsDontExport(typeAs)) return (null, null, null);
      // «Export to IFC = Yes» keeps an element whose category the mapping does not export.
      var forced = false;
#if REVIT2023_OR_GREATER
      var instanceExport = element.get_Parameter(BuiltInParameter.IFC_EXPORT_ELEMENT);
      var instanceMode = instanceExport?.HasValue == true && instanceExport.StorageType == StorageType.Integer
        ? (IFCExportElement?)instanceExport.AsInteger() : null;
      if (instanceMode == IFCExportElement.No) return (null, null, null);
      var typeExport = type?.get_Parameter(BuiltInParameter.IFC_EXPORT_ELEMENT_TYPE);
      var typeMode = typeExport?.HasValue == true && typeExport.StorageType == StorageType.Integer
        ? (IFCExportElementType?)typeExport.AsInteger() : null;
      if (instanceMode != IFCExportElement.Yes && typeMode == IFCExportElementType.No) return (null, null, null);
      forced = instanceMode == IFCExportElement.Yes || typeMode == IFCExportElementType.Yes;
#endif
      (ifcClass, predefined) = DirectIfcSemantics.ParseEntity(instanceAs);
      if (ifcClass == null) (ifcClass, predefined) = DirectIfcSemantics.ParseEntity(typeAs);
      if (ifcClass == null) (ifcClass, predefined) = AssemblyClass(element, categoryName);
      if (ifcClass == null && element.Category != null)
      {
#if REVIT2025_OR_GREATER
        try
        {
          // Without a saved template Revit's exporter uses the in-session mapping, so classification does too.
          var template = IFCCategoryTemplate.GetActiveTemplate(element.Document) ?? IFCCategoryTemplate.GetOrCreateInSessionTemplate(element.Document);
          var mapping = template?.GetMappingInfoById(element.Document, element.Category.Id, CustomSubCategoryId.None);
          if (mapping != null)
          {
            if (!mapping.IFCExportFlag && !forced) return (null, null, null);
            var (mapped, suffix) = DirectIfcSemantics.ParseEntity(mapping.IFCEntityName);
            ifcClass = DirectIfcSemantics.PreferTypedClass(mapped, categoryClass);
            if (ifcClass == mapped) predefined = suffix ?? DirectIfcSemantics.PredefinedToken(mapping.IFCPredefinedType, out objectType);
          }
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException) { }
#else
        var table = IfcCategoryTable.ForRevit(element.Document.Application.VersionNumber);
        if (table != null && long.TryParse(element.Category.Id.ToString(), out var categoryId) && table.TryGet(categoryId, out var entry))
        {
          if (entry.Class == null && !forced) return (null, null, null);
          ifcClass = DirectIfcSemantics.PreferTypedClass(entry.Class, categoryClass);
          if (ifcClass == entry.Class) predefined = entry.Predefined;
        }
#endif
      }
      var exportType = ExportText(element, false, ExportField.PredefinedType);
      if (string.IsNullOrWhiteSpace(exportType)) exportType = ExportText(type, true, ExportField.PredefinedType);
      if (!string.IsNullOrWhiteSpace(exportType)) predefined = DirectIfcSemantics.PredefinedToken(exportType, out objectType);
      // A category fallback is an estimate, not exporter parity.
      ifcClass = DirectIfcSemantics.NormalizeSite(DirectIfcSemantics.OccurrenceClass(ifcClass ?? categoryClass));
      predefined ??= DirectIfcSemantics.DefaultPredefinedType(categoryName, ifcClass);
      if (predefined != "USERDEFINED") objectType = null;
      return (ifcClass, predefined, objectType);
    }

    private static (string Class, string Predefined) AssemblyClass(Element element, string categoryName)
    {
      if (DirectIfcSemantics.NormativeCategory(categoryName) is { } rule) return rule;
      if (element is Wall wall && wall.WallType?.Kind == WallKind.Curtain) return ("IfcCurtainWall", null);
      if (element is FamilyInstance { StructuralType: Autodesk.Revit.DB.Structure.StructuralType.Brace } &&
          categoryName == nameof(BuiltInCategory.OST_StructuralFraming)) return ("IfcMember", "BRACE");
      // The native IFC exporter treats truss chords/webs as members, even though
      // they share Revit's Structural Framing category with beams.
      var usage = element.get_Parameter(BuiltInParameter.INSTANCE_STRUCT_USAGE_PARAM);
      if (categoryName == nameof(BuiltInCategory.OST_StructuralFraming) &&
          usage?.StorageType == StorageType.Integer && usage.HasValue && usage.AsInteger() is 11 or 12) return ("IfcMember", null);
      return (null, null);
    }

    // A beam system sketched on a work plane has no level: Revit throws instead of returning null.
    private static Level BeamSystemLevel(Element element)
    {
      if (element is not BeamSystem system) return null;
      try { return system.Level; }
      catch (Autodesk.Revit.Exceptions.InvalidOperationException) { return null; }
    }

    // Curtain panels, mullions and doors/windows placed in a curtain wall belong to it; stair flights and landings to their stair.
    private static string AggregateParent(Element element, Element host)
    {
      if (element is Autodesk.Revit.DB.Architecture.StairsRun run) return run.GetStairs()?.UniqueId;
      if (element is Autodesk.Revit.DB.Architecture.StairsLanding landing) return landing.GetStairs()?.UniqueId;
      // Panels and mullions always belong to their host grid (curtain wall, curtain system or glazed roof).
      if (element is Panel or Mullion) return host?.UniqueId;
      // Top rails and handrails are parts of the railing that hosts them.
      if (element is Autodesk.Revit.DB.Architecture.ContinuousRail rail) return element.Document.GetElement(rail.HostRailingId)?.UniqueId;
      if (host != null && Capture.ElementGeometryReader.IsAssembly(host)) return host.UniqueId;
      // Beams laid out by a beam system belong to it (IfcElementAssembly BEAM_GRID).
      if (element is FamilyInstance beam && beam.StructuralType == Autodesk.Revit.DB.Structure.StructuralType.Beam)
        try { return BeamSystem.BeamBelongsTo(beam)?.UniqueId; }
        catch (Autodesk.Revit.Exceptions.ArgumentException) { }
        catch (Autodesk.Revit.Exceptions.InvalidOperationException) { }
      return null;
    }

    private static string Text(Parameter parameter)
    {
      if (parameter?.HasValue != true) return null;
      return parameter.StorageType == StorageType.String ? parameter.AsString() : parameter.AsValueString();
    }

    private enum ExportField { Class, PredefinedType }

    // IfcExportAs and IfcExportType: Revit's built-in parameters from 2023; before, the shared parameters the 2022
    // exporter reads by name, ignoring case (IFCExportAs; IFCExportType, then the legacy IFCType).
    private static string ExportText(Element source, bool isType, ExportField field)
    {
      if (source == null) return null;
#if REVIT2023_OR_GREATER
      var id = field == ExportField.Class
        ? isType ? BuiltInParameter.IFC_EXPORT_ELEMENT_TYPE_AS : BuiltInParameter.IFC_EXPORT_ELEMENT_AS
        : isType ? BuiltInParameter.IFC_EXPORT_PREDEFINEDTYPE_TYPE : BuiltInParameter.IFC_EXPORT_PREDEFINEDTYPE;
      return Text(source.get_Parameter(id));
#else
      var names = field == ExportField.Class ? new[] { "IFCExportAs" } : new[] { "IFCExportType", "IFCType" };
      foreach (var name in names)
        foreach (Parameter parameter in source.Parameters)
          if (string.Equals(parameter.Definition?.Name, name, StringComparison.OrdinalIgnoreCase) &&
              Text(parameter) is { } value && !string.IsNullOrWhiteSpace(value))
            return value;
      return null;
#endif
    }

    private static void AddDirectQuantities(Element element, BimElement result)
    {
      // IFC4 defines these lengths without cut-outs. Curved location paths are not claimed.
      if (element.Location is LocationCurve location && location.Curve is Line line)
      {
        var set = DirectIfcSemantics.StraightLength(result.IfcClass, line.Length);
        if (set != null) foreach (var property in set.Properties) AddComputed(result, set.Name, set.Kind, property);
      }
      // Members without a straight path (curtain wall mullions) still have the length Revit computes for them.
      if (result.IfcClass is "IfcMember" or "IfcBeam")
        AddLength(result, DirectIfcSemantics.BaseQuantitySet(result.IfcClass), "Length", DoubleValue(element, BuiltInParameter.CURVE_ELEM_LENGTH));
      if (result.IfcClass == "IfcColumn" && element.Location is LocationPoint)
      {
        double? Elevation(BuiltInParameter name) =>
          element.get_Parameter(name)?.AsElementId() is { } id && element.Document.GetElement(id) is Level level ? level.Elevation : null;
        AddComputed(result, "Qto_ColumnBaseQuantities", "qto", DirectIfcSemantics.VerticalLength(
          Elevation(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM), DoubleValue(element, BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM),
          Elevation(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM), DoubleValue(element, BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM)));
      }
      if (result.IfcClass == "IfcWall" && element.Location is LocationCurve wallPath)
      {
        AddLength(result, "Qto_WallBaseQuantities", "Length", wallPath.Curve?.Length);
        if (element.Document.GetElement(element.GetTypeId()) is WallType wallType)
          AddLength(result, "Qto_WallBaseQuantities", "Width", wallType.Width);
      }
      var segmentSet = result.IfcClass switch {
        "IfcPipeSegment" => "Qto_PipeSegmentBaseQuantities",
        "IfcDuctSegment" => "Qto_DuctSegmentBaseQuantities",
        "IfcCableCarrierSegment" => "Qto_CableCarrierSegmentBaseQuantities",
        _ => null
      };
      if (segmentSet != null)
        AddLength(result, segmentSet, "Length", element.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble());
      if (result.IfcClass is "IfcDoor" or "IfcWindow")
      {
        var type = element.Document.GetElement(element.GetTypeId());
        var set = result.IfcClass == "IfcDoor" ? "Qto_DoorBaseQuantities" : "Qto_WindowBaseQuantities";
        var width = result.IfcClass == "IfcDoor" ? BuiltInParameter.DOOR_WIDTH : BuiltInParameter.WINDOW_WIDTH;
        var height = result.IfcClass == "IfcDoor" ? BuiltInParameter.DOOR_HEIGHT : BuiltInParameter.WINDOW_HEIGHT;
        // Instance dimensions win over the type, as for any occurrence property.
        AddLength(result, set, "Width", FirstLength(element, type, width, BuiltInParameter.FAMILY_WIDTH_PARAM));
        AddLength(result, set, "Height", FirstLength(element, type, height, BuiltInParameter.FAMILY_HEIGHT_PARAM));
      }
      if (result.IfcClass == "IfcSlab")
      {
        var volume = element.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED);
        if (volume?.HasValue == true && volume.StorageType == StorageType.Double)
        {
          var set = DirectIfcSemantics.SlabNetVolume(volume.AsDouble());
          if (set != null) foreach (var property in set.Properties) AddComputed(result, set.Name, set.Kind, property);
        }
      }
      AddHostQuantities(element, result);
    }

    private static void AddDirectProperties(Element element, BuiltInCategory category, BimElement result)
    {
      var type = element.Document.GetElement(element.GetTypeId());
      // Occurrence values override type values with the same name (IfcRelDefinesByType precedence).
      if (type != null) AddDeclaredPsets(type, result);
      AddDeclaredPsets(element, result);
      AddAdditionalPropertyGroups(element, false, result);
      if (type != null) AddAdditionalPropertyGroups(type, true, result);
      var commonSet = DirectIfcSemantics.CommonPropertySet(result.IfcClass);
      if (commonSet != null)
      {
        // "Family:Type", as the Autodesk exporter writes it; without a type, the element's name.
        var reference = type != null ? DirectIfcSemantics.Reference((type as ElementType)?.FamilyName, type.Name) : element.Name;
        if (!string.IsNullOrWhiteSpace(reference))
          AddComputed(result, commonSet, "pset", new IfcProperty { Name = "Reference", Value = reference });
        if (!DirectIfcSemantics.IsTypeCommonSet(commonSet)) AddCommonProperties(element, type, category, result, commonSet);
      }
    }

    private static void AddDeclaredPsets(Element source, BimElement result)
    {
      foreach (Parameter parameter in source.Parameters)
      {
        if (!parameter.HasValue) continue;
        var fullName = parameter.Definition?.Name;
        var kind = fullName != null && fullName.StartsWith("Pset_", StringComparison.Ordinal) ? "pset"
          : fullName != null && fullName.StartsWith("Qto_", StringComparison.Ordinal) ? "qto" : null;
        if (kind == null) continue;
        var separator = fullName.IndexOf('.');
        if (separator < 6 || separator == fullName.Length - 1) continue;
        var value = DirectValue(parameter);
        if (value == null) continue;
        // The CDE rejects the whole revision for a quantity that is not a measure, so non-measures are not sent as Qto.
        if (kind == "qto" && value.Type is not ("length" or "area" or "volume"))
        {
          if (value.Value is int) value.Type = "count";
          else continue;
        }
        value.Name = fullName.Substring(separator + 1).Replace("[Type]", "");
        Add(result, fullName.Substring(0, separator), kind, value);
      }
    }

    private static void AddAdditionalPropertyGroups(Element source, bool isType, BimElement result)
    {
      foreach (Parameter parameter in source.Parameters)
      {
        if (!parameter.HasValue || parameter.Definition == null) continue;
        var parameterName = parameter.Definition.Name;
        if (string.IsNullOrWhiteSpace(parameterName) || parameterName.StartsWith("Pset_", StringComparison.Ordinal) ||
          parameterName.StartsWith("Qto_", StringComparison.Ordinal)) continue;
        var property = DirectValue(parameter);
        if (property == null)
        {
          if (parameter.StorageType != StorageType.ElementId) continue;
          var display = parameter.AsValueString();
          if (string.IsNullOrWhiteSpace(display)) continue;
          property = new IfcProperty { Value = display };
        }
        property.Name = parameterName;
        var group = RevitCompat.GroupLabel(parameter.Definition);
        Add(result, DirectIfcSemantics.AdditionalSetName(isType, group, source.Name), "pset", property);
      }
    }

    private static IfcProperty DirectValue(Parameter parameter)
    {
      switch (parameter.StorageType)
      {
        case StorageType.String:
          var text = parameter.AsString();
          return string.IsNullOrWhiteSpace(text) ? null : new IfcProperty { Value = text };
        case StorageType.Integer:
          var spec = RevitCompat.SpecId(parameter.Definition);
          return DirectIfcSemantics.IsYesNoSpec(spec) ? new IfcProperty { Value = parameter.AsInteger() != 0 }
            : new IfcProperty { Value = parameter.AsInteger() };
        case StorageType.Double:
          var raw = parameter.AsDouble();
          if (!NetCompat.IsFinite(raw)) return null;
          var dataType = RevitCompat.SpecId(parameter.Definition);
          if (dataType.Contains(":length-")) return new IfcProperty { Value = raw * 0.3048, Unit = "metre", Type = "length" };
          if (dataType.Contains(":area-")) return new IfcProperty { Value = raw * 0.09290304, Unit = "square_metre", Type = "area" };
          if (dataType.Contains(":volume-")) return new IfcProperty { Value = raw * 0.028316846592, Unit = "cubic_metre", Type = "volume" };
          // Revit stores angles in radians, which is already the IFC SI plane angle unit.
          if (dataType.Contains(":angle-")) return new IfcProperty { Value = raw, Unit = "radian", Type = "planeangle" };
          return new IfcProperty { Value = raw };
        default: return null;
      }
    }

    private static void Add(BimElement result, string name, string kind, IfcProperty property, bool overwrite = true)
    {
      if (property?.Value == null || property.Value is double value && !NetCompat.IsFinite(value)) return;
      var set = result.IfcSets.Find(candidate => candidate.Name == name && candidate.Kind == kind);
      if (set == null) { set = new IfcPropertySet { Name = name, Kind = kind }; result.IfcSets.Add(set); }
      var existing = set.Properties.FindIndex(candidate => candidate.Name == property.Name);
      if (existing < 0) set.Properties.Add(property);
      else if (overwrite) set.Properties[existing] = property;
    }

    // Values derived by Cmcoordinator never replace a Pset_/Qto_ value the author declared explicitly.
    private static void AddComputed(BimElement result, string name, string kind, IfcProperty property) =>
      Add(result, name, kind, property, overwrite: false);

    private static void AddLength(BimElement result, string set, string name, double? internalFeet)
    {
      if (internalFeet is not > 0) return;
      AddComputed(result, set, "qto", new IfcProperty { Name = name, Value = internalFeet.Value * 0.3048,
        Unit = "metre", Type = "length" });
    }

    private static double? FirstLength(Element element, Element type, params BuiltInParameter[] names)
    {
      foreach (var source in new[] { element, type })
        foreach (var name in names)
          if (source != null && DoubleValue(source, name) is > 0 and var value) return value;
      return null;
    }

    // Each class gets the quantities of its IFC4 Qto with the names and sources of the Autodesk exporter, measured on the
    // same model exported both ways (UI-61 3.2): Revit's computed area, volume and perimeter, the element's geometry and
    // the layers of its type. Quantities the exporter derives from its own extrusion analysis are not claimed.
    private static void AddHostQuantities(Element element, BimElement result)
    {
      var set = DirectIfcSemantics.BaseQuantitySet(result.IfcClass);
      if (set == null) return;
      var area = DoubleValue(element, BuiltInParameter.HOST_AREA_COMPUTED);
      var volume = DoubleValue(element, BuiltInParameter.HOST_VOLUME_COMPUTED);
      var perimeter = DoubleValue(element, BuiltInParameter.HOST_PERIMETER_COMPUTED);
      var type = element.Document.GetElement(element.GetTypeId());
      void Put(IfcProperty property) { if (property != null) AddComputed(result, set, "qto", property); }
      void Areas(params string[] names) { if (area is >= 0) foreach (var name in names) Put(DirectIfcSemantics.Area(name, area.Value)); }
      void Volumes(params string[] names) { if (volume is >= 0) foreach (var name in names) Put(DirectIfcSemantics.Volume(name, volume.Value)); }
      switch (result.IfcClass)
      {
        case "IfcWall":
          // Revit's wall area is what the exporter reports as both side areas; its gross volume is that area by the width.
          Areas("NetSideArea", "GrossSideArea");
          Volumes("NetVolume");
          if (area is >= 0 && type is WallType wallType && wallType.Width > 0)
            Put(DirectIfcSemantics.Volume("GrossVolume", area.Value * wallType.Width));
          Put(Height(element));
          AddLayers(element, type, result, set);
          break;
        case "IfcCurtainWall":
          AddLength(result, set, "Length", DoubleValue(element, BuiltInParameter.CURVE_ELEM_LENGTH));
          Put(Height(element));
          Areas("GrossSideArea", "NetSideArea");
          break;
        case "IfcSlab":
          Areas("NetArea", "GrossArea");
          Volumes("NetVolume", "GrossVolume");
          if (perimeter is >= 0) Put(DirectIfcSemantics.Length("Perimeter", perimeter.Value));
          if (type is FloorType floorType && LayersWidth(floorType) is > 0 and var depth) Put(DirectIfcSemantics.Length("Depth", depth));
          break;
        case "IfcRoof":
          Areas("NetArea", "GrossArea");
          break;
        case "IfcCovering":
          Areas("NetArea", "GrossArea");
          if (type is HostObjAttributes covering && LayersWidth(covering) is > 0 and var width) Put(DirectIfcSemantics.Length("Width", width));
          AddLayers(element, type, result, set);
          break;
        case "IfcPlate":
          Areas("NetArea", "GrossArea");
          Volumes("NetVolume");
          break;
        case "IfcMember":
          // A member's computed area is its cross-section: the exporter's CrossSectionArea.
          Areas("CrossSectionArea");
          Volumes("NetVolume");
          Put(SurfaceArea(element));
          break;
        case "IfcBeam":
        case "IfcBuildingElementProxy":
          Volumes("NetVolume");
          Put(SurfaceArea(element));
          break;
        case "IfcColumn":
          Volumes("NetVolume", "GrossVolume");
          // A column is a prism: its section is its volume over its length.
          if (volume is > 0 && Quantity(result, set, "Length") is > 0 and var metres)
            Put(DirectIfcSemantics.Area("CrossSectionArea", volume.Value / (metres / 0.3048)));
          break;
        case "IfcRailing":
          AddLength(result, set, "Length", DoubleValue(element, BuiltInParameter.CURVE_ELEM_LENGTH));
          break;
        case "IfcDoor":
        case "IfcWindow":
          if (Quantity(result, set, "Width") is > 0 and var w && Quantity(result, set, "Height") is > 0 and var h)
          {
            Put(DirectIfcSemantics.Area("Area", w * h / 0.09290304));
            // The exporter's window perimeter is that of its opening; a door's follows its own frame and is not claimed.
            if (result.IfcClass == "IfcWindow") Put(DirectIfcSemantics.Length("Perimeter", 2 * (w + h) / 0.3048));
          }
          break;
      }
    }

    // A quantity already computed for this element, in metres or square metres.
    private static double? Quantity(BimElement result, string set, string name) =>
      result.IfcSets.Find(s => s.Name == set && s.Kind == "qto")?.Properties.Find(p => p.Name == name)?.Value is double value ? value : null;

    // The vertical extent of the element, which matches the exporter's wall height (UI-61 3.2: 137 of 146 walls).
    private static IfcProperty Height(Element element)
    {
      var box = element.get_BoundingBox(null);
      return box == null || box.Max.Z - box.Min.Z <= 0 ? null : DirectIfcSemantics.Length("Height", box.Max.Z - box.Min.Z);
    }

    private static double? LayersWidth(HostObjAttributes type)
    {
      using var compound = type.GetCompoundStructure();
      return compound?.GetWidth();
    }

    // The exporter reports each layer of a compound type with its width; here, «Capa: <material>» in metres.
    private static void AddLayers(Element element, Element type, BimElement result, string set)
    {
      if (type is not HostObjAttributes host) return;
      using var compound = host.GetCompoundStructure();
      if (compound == null) return;
      var names = new HashSet<string>(StringComparer.Ordinal);
      foreach (var layer in compound.GetLayers())
      {
        if (layer.Width <= 0) continue;
        var material = (element.Document.GetElement(layer.MaterialId) as Material)?.Name;
        var name = "Capa: " + (string.IsNullOrWhiteSpace(material) ? layer.Function.ToString() : material);
        var unique = name;
        for (var n = 2; !names.Add(unique); n++) unique = name + " (" + n + ")";
        AddComputed(result, set, "qto", DirectIfcSemantics.Length(unique, layer.Width));
      }
    }

    // The area of every face of the element's solids, as the exporter's NetSurfaceArea.
    private static IfcProperty SurfaceArea(Element element)
    {
      var total = 0.0;
      void Walk(GeometryElement geometry)
      {
        foreach (var item in geometry)
          if (item is Solid solid && solid.Faces.Size > 0) foreach (Face face in solid.Faces) total += face.Area;
          else if (item is GeometryInstance instance && instance.GetInstanceGeometry() is { } inner) Walk(inner);
      }
      try
      {
        if (element.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine }) is { } geometry) Walk(geometry);
      }
      catch (Autodesk.Revit.Exceptions.ApplicationException) { return null; }
      return total > 0 ? DirectIfcSemantics.Area("NetSurfaceArea", total) : null;
    }

    // The common set as the Autodesk exporter fills it (UI-61 3.1): LoadBearing, IsExternal on every element,
    // ExtendToStructure, FireRating, the beam's Span, Slope and Roll, the railing's Height, the stair's risers and
    // treads and the ceiling's Finish.
    private static void AddCommonProperties(Element element, Element type, BuiltInCategory category, BimElement result, string set)
    {
      void Put(IfcProperty property) { if (property?.Value != null) AddComputed(result, set, "pset", property); }
      Parameter structural = null;
      if (result.IfcClass == "IfcSlab") structural = element.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL);
      else if (result.IfcClass == "IfcWall") structural = element.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT);
      if (structural?.HasValue == true && structural.StorageType == StorageType.Integer)
        Put(new IfcProperty { Name = "LoadBearing", Value = structural.AsInteger() != 0 });
      // Only Revit's structural categories carry loads by definition; architectural columns and mullions stay undeclared.
      else if (category is BuiltInCategory.OST_StructuralColumns or BuiltInCategory.OST_StructuralFraming or BuiltInCategory.OST_StructuralFoundation)
        Put(new IfcProperty { Name = "LoadBearing", Value = true });

      Put(new IfcProperty { Name = "IsExternal", Value = IsExternal(element, type, 0) });
      if (element is Wall && element.get_Parameter(BuiltInParameter.WALL_TOP_IS_ATTACHED) is { HasValue: true, StorageType: StorageType.Integer } top)
        Put(new IfcProperty { Name = "ExtendToStructure", Value = top.AsInteger() != 0 });
      var fireRating = Text(element.get_Parameter(BuiltInParameter.FIRE_RATING));
      if (string.IsNullOrWhiteSpace(fireRating)) fireRating = Text(type?.get_Parameter(BuiltInParameter.FIRE_RATING));
      if (!string.IsNullOrWhiteSpace(fireRating)) Put(new IfcProperty { Name = "FireRating", Value = fireRating });

      if (result.IfcClass == "IfcBeam")
      {
        if (element.Location is LocationCurve path && path.Curve is Line span) Put(DirectIfcSemantics.Length("Span", span.Length));
        var length = DoubleValue(element, BuiltInParameter.INSTANCE_LENGTH_PARAM);
        var start = DoubleValue(element, BuiltInParameter.STRUCTURAL_BEAM_END0_ELEVATION);
        var end = DoubleValue(element, BuiltInParameter.STRUCTURAL_BEAM_END1_ELEVATION);
        if (length is > 0 && start != null && end != null && Math.Abs(end.Value - start.Value) <= length.Value)
          Put(Angle("Slope", Math.Asin(Math.Abs(end.Value - start.Value) / length.Value)));
        if (DoubleValue(element, BuiltInParameter.STRUCTURAL_BEND_DIR_ANGLE) is { } roll) Put(Angle("Roll", roll));
      }
      if (type is Autodesk.Revit.DB.Architecture.RailingType railing && railing.TopRailHeight > 0)
        Put(DirectIfcSemantics.Length("Height", railing.TopRailHeight));
      AddStairProperties(element, Put);
      if (element is Ceiling ceiling && type is HostObjAttributes ceilingType)
      {
        using var compound = ceilingType.GetCompoundStructure();
        var finishes = compound?.GetLayers()
          .Where(layer => layer.Function is MaterialFunctionAssignment.Finish1 or MaterialFunctionAssignment.Finish2)
          .Select(layer => (ceiling.Document.GetElement(layer.MaterialId) as Material)?.Name)
          .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct().ToList();
        if (finishes?.Count > 0) Put(new IfcProperty { Name = "Finish", Value = string.Concat(finishes.Select(name => name + ";")) });
      }
    }

    private static IfcProperty Angle(string name, double radians) =>
      NetCompat.IsFinite(radians) ? new IfcProperty { Name = name, Value = radians, Unit = "radian", Type = "planeangle" } : null;

    // Risers and treads as the exporter reads them for a stair and for each of its runs.
    private static void AddStairProperties(Element element, Action<IfcProperty> put)
    {
      Autodesk.Revit.DB.Architecture.Stairs stairs;
      int risers, treads;
      double riserHeight;
      Autodesk.Revit.DB.Architecture.StairsRun run;
      if (element is Autodesk.Revit.DB.Architecture.StairsRun stairsRun)
      {
        run = stairsRun; stairs = run.GetStairs();
        risers = run.ActualRisersNumber; treads = run.ActualTreadsNumber;
        riserHeight = risers > 0 ? run.Height / risers : 0;
      }
      else if (element is Autodesk.Revit.DB.Architecture.Stairs whole)
      {
        stairs = whole; run = whole.Document.GetElement(whole.GetStairsRuns().FirstOrDefault() ?? ElementId.InvalidElementId) as Autodesk.Revit.DB.Architecture.StairsRun;
        risers = whole.ActualRisersNumber; treads = whole.ActualTreadsNumber; riserHeight = whole.ActualRiserHeight;
      }
      else return;
      if (risers > 0) put(new IfcProperty { Name = "NumberOfRiser", Value = risers });
      if (treads > 0) put(new IfcProperty { Name = "NumberOfTreads", Value = treads });
      if (riserHeight > 0) put(DirectIfcSemantics.Length("RiserHeight", riserHeight));
      if (stairs?.ActualTreadDepth is > 0 and var tread)
        foreach (var name in new[] { "TreadLength", "TreadLengthAtInnerSide", "TreadLengthAtOffset" })
          put(DirectIfcSemantics.Length(name, tread));
      if (run != null && run.Document.GetElement(run.GetTypeId()) is Autodesk.Revit.DB.Architecture.StairsRunType runType && runType.NosingLength > 0)
        put(DirectIfcSemantics.Length("NosingLength", runType.NosingLength));
    }

    // As the Autodesk exporter decides it: an «IsExternal» parameter, the type's Function (anything but Interior),
    // roofs always, and a hosted family instance as its host.
    private static bool IsExternal(Element element, Element type, int depth)
    {
      foreach (var source in new[] { element, type })
        if (source?.LookupParameter("IsExternal") is { HasValue: true, StorageType: StorageType.Integer } flag) return flag.AsInteger() != 0;
      if (type?.get_Parameter(BuiltInParameter.FUNCTION_PARAM) is { HasValue: true, StorageType: StorageType.Integer } function)
        return function.AsInteger() != (int)WallFunction.Interior;
      var categoryId = element.Category?.Id.ToString();
      if (categoryId == ((int)BuiltInCategory.OST_Roofs).ToString() || categoryId == ((int)BuiltInCategory.OST_MassExteriorWall).ToString()) return true;
      if (depth < 4 && element is FamilyInstance instance)
      {
        var host = instance.Host ?? (instance.HostFace != null ? element.Document.GetElement(instance.HostFace) : null);
        if (host != null) return IsExternal(host, host.Document.GetElement(host.GetTypeId()), depth + 1);
      }
      return false;
    }

    private static double? DoubleValue(Element element, BuiltInParameter name)
    {
      var parameter = element.get_Parameter(name);
      return parameter?.HasValue == true && parameter.StorageType == StorageType.Double &&
        NetCompat.IsFinite(parameter.AsDouble()) ? parameter.AsDouble() : null;
    }
  }
}
