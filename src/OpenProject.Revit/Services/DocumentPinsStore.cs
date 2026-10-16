using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Newtonsoft.Json;
using OpenProject.Shared.Pins;
using Serilog;

namespace OpenProject.Revit.Services
{
  public static class DocumentPinsStore
  {
    private static readonly Guid SchemaGuid = new Guid("6A34A7A9-4E4D-4B18-93C3-0EC73595C4C7");
    private const string SchemaName = "OpenProjectPins";
    private const string FieldName = "PinsJson";

    public static List<PinRecord> Load(Document document)
    {
      if (document == null)
        return new List<PinRecord>();

      try
      {
        var schema = Schema.Lookup(SchemaGuid);
        if (schema == null)
          return new List<PinRecord>();

        var element = document.ProjectInformation;
        var entity = element.GetEntity(schema);
        if (!entity.IsValid())
          return new List<PinRecord>();

        var json = entity.Get<string>(FieldName);
        if (string.IsNullOrWhiteSpace(json))
          return new List<PinRecord>();

        return JsonConvert.DeserializeObject<List<PinRecord>>(json) ?? new List<PinRecord>();
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Failed to deserialize pins from document.");
        return new List<PinRecord>();
      }
    }

    public static bool Save(Document document, List<PinRecord> pins)
    {
      if (document == null)
        return false;

      try
      {
        var schema = Schema.Lookup(SchemaGuid) ?? CreateSchema();
        var element = document.ProjectInformation;
        var entity = new Entity(schema);

        var json = JsonConvert.SerializeObject(pins ?? new List<PinRecord>());
        entity.Set(FieldName, json);
        element.SetEntity(entity);
        return true;
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Failed to save pins to document.");
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
