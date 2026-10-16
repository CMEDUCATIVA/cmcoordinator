using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Newtonsoft.Json;
using OpenProject.Revit.Models;
using Serilog;

namespace OpenProject.Revit.Services
{
  public static class DocumentTableStateStore
  {
    private static readonly Guid SchemaGuid = new Guid("2C0E6D78-6D61-4E39-9B9F-5C6B39C01A40");
    private const string SchemaName = "OpenProjectCdeTableState";
    private const string FieldName = "StateJson";

    public static CdeTableState Load(Document document)
    {
      if (document == null)
        return null;

      try
      {
        var schema = Schema.Lookup(SchemaGuid);
        if (schema == null)
          return null;

        var element = document.ProjectInformation;
        var entity = element.GetEntity(schema);
        if (!entity.IsValid())
          return null;

        var json = entity.Get<string>(FieldName);
        if (string.IsNullOrWhiteSpace(json))
          return null;

        return JsonConvert.DeserializeObject<CdeTableState>(json);
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Failed to deserialize CDE table state from document.");
        return null;
      }
    }

    public static bool Save(Document document, CdeTableState state)
    {
      if (document == null)
        return false;

      try
      {
        var schema = Schema.Lookup(SchemaGuid) ?? CreateSchema();
        var element = document.ProjectInformation;
        var entity = new Entity(schema);

        var json = JsonConvert.SerializeObject(state ?? new CdeTableState());
        entity.Set(FieldName, json);
        element.SetEntity(entity);
        return true;
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Failed to save CDE table state to document.");
        return false;
      }
    }

    private static Schema CreateSchema()
    {
      var builder = new SchemaBuilder(SchemaGuid);
      builder.SetSchemaName(SchemaName);
      builder.AddSimpleField(FieldName, typeof(string));
      builder.SetReadAccessLevel(AccessLevel.Public);
      builder.SetWriteAccessLevel(AccessLevel.Public);
      return builder.Finish();
    }
  }
}
