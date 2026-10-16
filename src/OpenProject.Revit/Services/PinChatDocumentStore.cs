using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Newtonsoft.Json;
using OpenProject.Revit.Models;
using Serilog;

namespace OpenProject.Revit.Services
{
  public static class PinChatDocumentStore
  {
    private static readonly Guid SchemaGuid = new Guid("8B760C7B-2C2E-4B3B-A63D-3C0D901DBCB7");
    private const string SchemaName = "OpenProjectPinChatHistory";
    private const string FieldName = "ChatJson";

    public static PinChatHistory Load(Document document)
    {
      if (document == null)
        return new PinChatHistory();

      try
      {
        var schema = Schema.Lookup(SchemaGuid);
        if (schema == null)
          return new PinChatHistory();

        var element = document.ProjectInformation;
        var entity = element.GetEntity(schema);
        if (!entity.IsValid())
          return new PinChatHistory();

        var json = entity.Get<string>(FieldName);
        if (string.IsNullOrWhiteSpace(json))
          return new PinChatHistory();

        return JsonConvert.DeserializeObject<PinChatHistory>(json) ?? new PinChatHistory();
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Failed to load pin chat history (document).");
        return new PinChatHistory();
      }
    }

    public static bool Save(Document document, PinChatHistory history)
    {
      if (document == null)
        return false;

      try
      {
        var schema = Schema.Lookup(SchemaGuid) ?? CreateSchema();
        var element = document.ProjectInformation;
        var entity = new Entity(schema);
        var json = JsonConvert.SerializeObject(history ?? new PinChatHistory());
        entity.Set(FieldName, json);
        element.SetEntity(entity);
        return true;
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Failed to save pin chat history (document).");
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
