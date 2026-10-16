using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using OpenProject.Revit.Models;
using Serilog;

namespace OpenProject.Revit.Services
{
  public static class PinChatLocalStore
  {
    private static string GetPath()
    {
      var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
      return Path.Combine(appData, "OpenProject.Revit", "PinChatHistory.json");
    }

    public static PinChatHistory Load()
    {
      try
      {
        var path = GetPath();
        if (!File.Exists(path))
          return new PinChatHistory();

        var json = File.ReadAllText(path);
        return JsonConvert.DeserializeObject<PinChatHistory>(json) ?? new PinChatHistory();
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Failed to load pin chat history (local).");
        return new PinChatHistory();
      }
    }

    public static void Save(PinChatHistory history)
    {
      try
      {
        var path = GetPath();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
          Directory.CreateDirectory(dir);

        var json = JsonConvert.SerializeObject(history ?? new PinChatHistory());
        File.WriteAllText(path, json);
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Failed to save pin chat history (local).");
      }
    }

    public static List<PinChatMessage> GetMessages(string workPackageId)
    {
      var history = Load();
      return history.Messages.TryGetValue(workPackageId ?? string.Empty, out var list)
        ? new List<PinChatMessage>(list)
        : new List<PinChatMessage>();
    }

    public static void AddMessage(string workPackageId, PinChatMessage message)
    {
      if (string.IsNullOrWhiteSpace(workPackageId) || message == null)
        return;

      var history = Load();
      if (!history.Messages.TryGetValue(workPackageId, out var list))
      {
        list = new List<PinChatMessage>();
        history.Messages[workPackageId] = list;
      }
      list.Add(message);
      Save(history);
    }
  }
}
