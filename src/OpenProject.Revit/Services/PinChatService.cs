using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using OpenProject.Revit.Models;
using Serilog;

namespace OpenProject.Revit.Services
{
  public static class PinChatService
  {
    public static PinChatHistory LoadMergedHistory(Document doc)
    {
      var local = PinChatLocalStore.Load();
      var docHistory = PinChatDocumentStore.Load(doc);
      return Merge(local, docHistory);
    }

    public static void AppendMessage(string workPackageId, PinChatMessage message)
    {
      if (message == null || string.IsNullOrWhiteSpace(workPackageId))
        return;

      PinChatLocalStore.AddMessage(workPackageId, message);

      var doc = PinsPaneManager.UiApp?.ActiveUIDocument?.Document;
      if (doc == null)
        return;

      PinsPaneManager.EnqueueUiAction(() =>
      {
        try
        {
          var history = PinChatDocumentStore.Load(doc);
          if (!history.Messages.TryGetValue(workPackageId, out var list))
          {
            list = new List<PinChatMessage>();
            history.Messages[workPackageId] = list;
          }
          list.Add(message);

          using var tx = new Transaction(doc, "Save pin chat history");
          if (tx.Start() == TransactionStatus.Started)
          {
            PinChatDocumentStore.Save(doc, history);
            tx.Commit();
          }
        }
        catch (Exception ex)
        {
          Log.Error(ex, "Failed to append chat message to document store.");
        }
      });
    }

    private static PinChatHistory Merge(PinChatHistory a, PinChatHistory b)
    {
      var result = new PinChatHistory();
      if (a?.Messages != null)
        Copy(result, a);
      if (b?.Messages != null)
        Copy(result, b);

      foreach (var key in result.Messages.Keys.ToList())
      {
        var dedup = result.Messages[key]
          .OrderBy(m => m.Timestamp)
          .GroupBy(m => new { m.Timestamp, m.Text })
          .Select(g => g.First())
          .ToList();
        result.Messages[key] = dedup;
      }

      return result;
    }

    private static void Copy(PinChatHistory dest, PinChatHistory source)
    {
      foreach (var pair in source.Messages)
      {
        if (!dest.Messages.TryGetValue(pair.Key, out var list))
        {
          list = new List<PinChatMessage>();
          dest.Messages[pair.Key] = list;
        }
        list.AddRange(pair.Value ?? new List<PinChatMessage>());
      }
    }
  }
}
