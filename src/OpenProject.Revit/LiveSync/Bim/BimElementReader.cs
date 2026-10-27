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

    // Order follows the Autodesk exporter: explicit IfcExportAs (instance, then type), assembly rules,
    // the project category template, then the built-in table. Classes are normalised to buildingSMART
    // occurrences; IfcExportType (instance, then type) refines the predefined type.
    private static (string Class, string Predefined, string ObjectType) ResolveClass(Element element, BuiltInCategory category)
    {
      var categoryName = category.ToString();
      var categoryClass = Classes.TryGetValue(category, out var knownClass) ? knownClass : "IfcBuildingElementProxy";
      string ifcClass = null, predefined = null, objectType = null;
      var type = element.Document.GetElement(element.GetTypeId());
#if Version2026 || Version2027
      var instanceExport = element.get_Parameter(BuiltInParameter.IFC_EXPORT_ELEMENT);
      var instanceMode = instanceExport?.HasValue == true && instanceExport.StorageType == StorageType.Integer
        ? (IFCExportElement?)instanceExport.AsInteger() : null;
      if (instanceMode == IFCExportElement.No) return (null, null, null);
      var typeExport = type?.get_Parameter(BuiltInParameter.IFC_EXPORT_ELEMENT_TYPE);
      var typeMode = typeExport?.HasValue == true && typeExport.StorageType == StorageType.Integer
        ? (IFCExportElementType?)typeExport.AsInteger() : null;
      if (instanceMode != IFCExportElement.Yes && typeMode == IFCExportElementType.No) return (null, null, null);
      (ifcClass, predefined) = DirectIfcSemantics.ParseEntity(Text(element.get_Parameter(BuiltInParameter.IFC_EXPORT_ELEMENT_AS)));
      if (ifcClass == null) (ifcClass, predefined) = DirectIfcSemantics.ParseEntity(Text(type?.get_Parameter(BuiltInParameter.IFC_EXPORT_ELEMENT_TYPE_AS)));
#endif
      if (ifcClass == null) (ifcClass, predefined) = AssemblyClass(element, categoryName);
#if Version2026 || Version2027
      if (ifcClass == null)
        try
        {
          // Without a saved template Revit's exporter uses the in-session mapping, so classification does too.
          var template = IFCCategoryTemplate.GetActiveTemplate(element.Document) ?? IFCCategoryTemplate.GetOrCreateInSessionTemplate(element.Document);
          if (template != null && element.Category != null)
          {
            var mapping = template.GetMappingInfoById(element.Document, element.Category.Id, CustomSubCategoryId.None);
            if (mapping != null)
            {
              if (!mapping.IFCExportFlag && instanceMode != IFCExportElement.Yes && typeMode != IFCExportElementType.Yes) return (null, null, null);
              var (mapped, suffix) = DirectIfcSemantics.ParseEntity(mapping.IFCEntityName);
              ifcClass = DirectIfcSemantics.PreferTypedClass(mapped, categoryClass);
              if (ifcClass == mapped) predefined = suffix ?? DirectIfcSemantics.PredefinedToken(mapping.IFCPredefinedType, out objectType);
            }
          }
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException) { }
      var exportType = Text(element.get_Parameter(BuiltInParameter.IFC_EXPORT_PREDEFINEDTYPE));
      if (string.IsNullOrWhiteSpace(exportType)) exportType = Text(type?.get_Parameter(BuiltInParameter.IFC_EXPORT_PREDEFINEDTYPE_TYPE));
      if (!string.IsNullOrWhiteSpace(exportType)) predefined = DirectIfcSemantics.PredefinedToken(exportType, out objectType);
#endif
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

    private static void AddDirectQuantities(Element element, BimElement result)
    {
      // IFC4 defines these lengths without cut-outs. Curved location paths are not claimed.
      if (element.Location is LocationCurve location && location.Curve is Line line)
      {
        var set = DirectIfcSemantics.StraightLength(result.IfcClass, line.Length);
        if (set != null) foreach (var property in set.Properties) AddComputed(result, set.Name, set.Kind, property);
      }
      else if (result.IfcClass == "IfcColumn" && element.Location is LocationPoint)
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
        if (!string.IsNullOrWhiteSpace(type?.Name))
          AddComputed(result, commonSet, "pset", new IfcProperty { Name = "Reference", Value = type.Name });
        AddCommonBooleans(element, type, category, result, commonSet);
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
          return spec.Contains(":yesno-") ? new IfcProperty { Value = parameter.AsInteger() != 0 }
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

    private static void AddHostQuantities(Element element, BimElement result)
    {
      var set = DirectIfcSemantics.BaseQuantitySet(result.IfcClass);
      if (set == null) return;
      var area = DoubleValue(element, BuiltInParameter.HOST_AREA_COMPUTED);
      var volume = DoubleValue(element, BuiltInParameter.HOST_VOLUME_COMPUTED);
      var perimeter = DoubleValue(element, BuiltInParameter.HOST_PERIMETER_COMPUTED);
      // Revit's computed area already excludes openings, so it is only NetArea. GrossArea is not claimed.
      if (area is >= 0)
      {
        var name = result.IfcClass == "IfcWall" ? "NetSideArea" : "NetArea";
        AddComputed(result, set, "qto", DirectIfcSemantics.Area(name, area.Value));
      }
      if (volume is >= 0) AddComputed(result, set, "qto", DirectIfcSemantics.Volume("NetVolume", volume.Value));
      if (perimeter is >= 0) AddComputed(result, set, "qto", DirectIfcSemantics.Length("Perimeter", perimeter.Value));
      if (result.IfcClass == "IfcSlab" && element.Document.GetElement(element.GetTypeId()) is FloorType floorType)
      {
        var width = floorType.GetCompoundStructure()?.GetWidth();
        if (width is > 0) AddComputed(result, set, "qto", DirectIfcSemantics.Length("Depth", width.Value));
      }
    }

    private static void AddCommonBooleans(Element element, Element type, BuiltInCategory category, BimElement result, string set)
    {
      Parameter structural = null;
      if (result.IfcClass == "IfcSlab") structural = element.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL);
      else if (result.IfcClass == "IfcWall") structural = element.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT);
      if (structural?.HasValue == true && structural.StorageType == StorageType.Integer)
        AddComputed(result, set, "pset", new IfcProperty { Name = "LoadBearing", Value = structural.AsInteger() != 0 });
      // Only Revit's structural categories carry loads by definition; architectural columns and mullions stay undeclared.
      else if (category is BuiltInCategory.OST_StructuralColumns or BuiltInCategory.OST_StructuralFraming or BuiltInCategory.OST_StructuralFoundation)
        AddComputed(result, set, "pset", new IfcProperty { Name = "LoadBearing", Value = true });

      var hostType = (element as FamilyInstance)?.Host is Wall hostWall
        ? element.Document.GetElement(hostWall.GetTypeId()) : type;
      var function = hostType?.get_Parameter(BuiltInParameter.FUNCTION_PARAM);
      if (function?.HasValue == true && function.StorageType == StorageType.Integer)
        AddComputed(result, set, "pset", new IfcProperty { Name = "IsExternal", Value = function.AsInteger() == 1 });
    }

    private static double? DoubleValue(Element element, BuiltInParameter name)
    {
      var parameter = element.get_Parameter(name);
      return parameter?.HasValue == true && parameter.StorageType == StorageType.Double &&
        NetCompat.IsFinite(parameter.AsDouble()) ? parameter.AsDouble() : null;
    }
  }
}
