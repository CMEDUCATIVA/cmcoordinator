using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using OpenProject.Revit.LiveSync.Telemetry;

namespace OpenProject.Revit.LiveSync.Bim
{
  // Inspects Revit's own IFC mapping. It never changes the CDE scene or the sync payload.
  internal static class IfcTemplateDiagnostic
  {
    public static void Inspect(Document document, IEnumerable<LiveElement> elements, string directory, ISyncTelemetrySink log)
    {
#if Version2026 || Version2027
      try
      {
        var active = IFCParameterTemplate.GetActiveTemplate(document);
        var template = active ?? IFCParameterTemplate.GetOrCreateInSessionTemplate(document);
        var report = new Dictionary<string, object>
        {
          ["capturedUtc"] = DateTime.UtcNow,
          ["documentTitle"] = document.Title,
          ["templateSource"] = active == null ? "in-session" : "active-document",
          ["templateName"] = template?.Name,
          ["groups"] = new List<object>()
        };
        if (template != null)
        {
          report["options"] = new
          {
            template.ExportIFCCommonPropertySets,
            template.ExportIFCBaseQuantities,
            template.ExportRevitElementParameters,
            template.ExportRevitMaterialParameters,
            template.ExportRevitSchedules,
            template.ExportUserDefinedPropertySets
          };
          var groups = (List<object>)report["groups"];
          foreach (var setup in Enum.GetValues<PropertySetupType>())
          {
            try
            {
              var sets = new List<object>();
              foreach (var name in template.GetPropertySetNames(setup, PropertySelectionType.All))
              {
                try
                {
                  var mappings = template.GetPropertyMappingInfos(setup, name, PropertySelectionType.All)
                    .Select(info => new
                    {
                      info.ExportFlag,
                      info.IFCPropertyName,
                      info.RevitPropertyName,
                      revitPropertyId = info.RevitPropertyId?.ToString()
                    }).ToList();
                  sets.Add(new
                  {
                    name,
                    exporting = template.IsExportingPropertySet(setup, name),
                    applicableEntities = template.GetPropertySetApplicableEntities(setup, name),
                    mappings
                  });
                }
                catch (Exception error) { sets.Add(new { name, error = error.GetType().Name + ": " + error.Message }); }
              }
              groups.Add(new { setup = setup.ToString(), sets });
            }
            catch (Exception error) { groups.Add(new { setup = setup.ToString(), error = error.GetType().Name + ": " + error.Message }); }
          }
        }
        try
        {
          var activeCategories = IFCCategoryTemplate.GetActiveTemplate(document);
          var categories = activeCategories ?? IFCCategoryTemplate.GetOrCreateInSessionTemplate(document);
          report["categoryTemplateSource"] = activeCategories == null ? "in-session" : "active-document";
          report["categoryTemplateName"] = categories?.Name;
          if (categories != null)
          {
            var mappings = new List<object>();
            foreach (var categoryId in elements.Where(e => e.Bim?.DocumentId == document.ProjectInformation.UniqueId)
              .Select(e => e.Bim.CategoryId).Where(id => long.TryParse(id, out _)).Distinct())
            {
              try
              {
                var category = document.Settings.Categories.Cast<Category>().First(c => c.Id.ToString() == categoryId);
                var info = categories.GetMappingInfoById(document, category.Id, CustomSubCategoryId.None);
                mappings.Add(new
                {
                  categoryId,
                  categoryName = category.Name,
                  info.IFCExportFlag,
                  info.IFCEntityName,
                  info.IFCPredefinedType,
                  info.IFCUserDefinedType
                });
              }
              catch (Exception error) { mappings.Add(new { categoryId, error = error.GetType().Name + ": " + error.Message }); }
            }
            report["categoryMappings"] = mappings;
          }
        }
        catch (Exception error) { report["categoryTemplateError"] = error.GetType().Name + ": " + error.Message; }
        Directory.CreateDirectory(directory);
        var documentKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(document.ProjectInformation.UniqueId)))[..16];
        var path = Path.Combine(directory, $"ifc-template-{documentKey}.json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonConvert.SerializeObject(report, Formatting.Indented), Encoding.UTF8);
        File.Move(temporary, path, true);
        log.Emit("ifc", "IFC_TEMPLATE_INSPECTED", $"Plantilla IFC leída ({report["templateSource"]}); diagnóstico local: {path}");
      }
      catch (Exception error)
      {
        log.Emit("ifc", "IFC_TEMPLATE_INSPECTION_FAILED", "No se pudo leer la plantilla IFC: " + error.GetType().Name + ": " + error.Message);
      }
#endif
    }

    public static string ExportCommon(Document document, View3D view, string directory)
    {
#if Version2026 || Version2027
      Directory.CreateDirectory(directory);
      var name = "ifc-native-live-" + Guid.NewGuid().ToString("N");
      using var options = new IFCExportOptions { FileVersion = IFCVersion.IFC4,
        ExportBaseQuantities = true, FilterViewId = view.Id };
      options.AddOption("ExportIFCCommonPropertySets", "true");
      // Preserve the same exporter-authored custom sets as a normal Revit IFC export.
      // They remain custom IFC sets in the CDE; only Pset_* is classified as buildingSMART.
      options.AddOption("ExportInternalRevitPropertySets", "true");
      options.AddOption("ExportUserDefinedPropertySets", "true");
      options.AddOption("UseFamilyAndTypeNameForReference", "true");
      options.AddOption("VisibleElementsOfCurrentView", "true");
      using var transaction = new Transaction(document, "Lectura IFC de Cmcoordinator");
      transaction.Start();
      IFCParameterTemplate template = null;
      bool common = false, quantities = false, elementParameters = false, userDefined = false, saved = false;
      bool exported;
      try
      {
        template = IFCParameterTemplate.GetActiveTemplate(document) ?? IFCParameterTemplate.GetOrCreateInSessionTemplate(document);
        if (template == null) throw new InvalidOperationException("Revit no devolvió una plantilla IFC.");
        common = template.ExportIFCCommonPropertySets;
        quantities = template.ExportIFCBaseQuantities;
        elementParameters = template.ExportRevitElementParameters;
        userDefined = template.ExportUserDefinedPropertySets;
        saved = true;
        template.ExportIFCCommonPropertySets = true;
        template.ExportIFCBaseQuantities = true;
        template.ExportRevitElementParameters = true;
        template.ExportUserDefinedPropertySets = true;
        exported = document.Export(directory, name, options);
      }
      catch
      {
        try { File.Delete(Path.Combine(directory, name + ".ifc")); } catch (IOException) { }
        throw;
      }
      finally
      {
        try
        {
          if (saved && template.IsValidObject)
          {
            template.ExportIFCCommonPropertySets = common;
            template.ExportIFCBaseQuantities = quantities;
            template.ExportRevitElementParameters = elementParameters;
            template.ExportUserDefinedPropertySets = userDefined;
          }
        }
        finally { transaction.RollBack(); }
      }
      if (!exported)
      {
        try { File.Delete(Path.Combine(directory, name + ".ifc")); } catch (IOException) { }
        throw new InvalidOperationException("El exportador IFC devolvió false.");
      }
      return Path.Combine(directory, name + ".ifc");
#else
      throw new NotSupportedException("Lectura IFC nativa disponible en Revit 2026/2027.");
#endif
    }

    public static void ExportRequested(Document document, View3D view, string directory, ISyncTelemetrySink log)
    {
#if Version2026 || Version2027
      try
      {
        var request = Path.Combine(directory, "ifc-native-export.request");
        if (!File.Exists(request)) return;
        var requested = File.ReadAllText(request).Trim();
        var enableCommon = requested == document.Title + ":common";
        if (!enableCommon && requested != document.Title) return;
        File.Delete(request);
        Directory.CreateDirectory(directory);
        var documentKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(document.ProjectInformation.UniqueId)))[..16];
        var name = enableCommon ? $"ifc-native-common-{documentKey}" : $"ifc-native-{documentKey}";
        using var options = new IFCExportOptions
        {
          FileVersion = IFCVersion.IFC4,
          ExportBaseQuantities = true,
          FilterViewId = view.Id
        };
        options.AddOption("ExportIFCCommonPropertySets", "true");
        options.AddOption("ExportInternalRevitPropertySets", "true");
        options.AddOption("ExportUserDefinedPropertySets", "true");
        options.AddOption("UseFamilyAndTypeNameForReference", "true");
        options.AddOption("VisibleElementsOfCurrentView", "true");
        using var transaction = new Transaction(document, "Diagnóstico IFC local de Cmcoordinator");
        transaction.Start();
        bool exported;
        IFCParameterTemplate template = null;
        bool originalCommon = false, originalQuantities = false, originalElementParameters = false, originalUserDefined = false;
        try
        {
          if (enableCommon)
          {
            template = IFCParameterTemplate.GetActiveTemplate(document) ?? IFCParameterTemplate.GetOrCreateInSessionTemplate(document);
            if (template == null) throw new InvalidOperationException("Revit no devolvió una plantilla IFC de sesión.");
            originalCommon = template.ExportIFCCommonPropertySets;
            originalQuantities = template.ExportIFCBaseQuantities;
            originalElementParameters = template.ExportRevitElementParameters;
            originalUserDefined = template.ExportUserDefinedPropertySets;
            template.ExportIFCCommonPropertySets = true;
            template.ExportIFCBaseQuantities = true;
            template.ExportRevitElementParameters = true;
            template.ExportUserDefinedPropertySets = true;
          }
          exported = document.Export(directory, name, options);
        }
        finally
        {
          try
          {
            if (template?.IsValidObject == true)
            {
              template.ExportIFCCommonPropertySets = originalCommon;
              template.ExportIFCBaseQuantities = originalQuantities;
              template.ExportRevitElementParameters = originalElementParameters;
              template.ExportUserDefinedPropertySets = originalUserDefined;
            }
          }
          finally { transaction.RollBack(); }
        }
        if (!exported) throw new InvalidOperationException("El exportador IFC devolvió false.");
        log.Emit("ifc", "IFC_NATIVE_EXPORTED", $"IFC4 de diagnóstico local generado ({(enableCommon ? "Psets activados temporalmente" : "configuración actual")}): {Path.Combine(directory, name + ".ifc")}");
      }
      catch (Exception error)
      {
        log.Emit("ifc", "IFC_NATIVE_EXPORT_FAILED", "No se pudo exportar IFC4 de diagnóstico: " + error.GetType().Name + ": " + error.Message);
      }
#endif
    }
  }
}
