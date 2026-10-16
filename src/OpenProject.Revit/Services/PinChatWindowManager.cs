using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using OpenProject.Revit.Models;
using OpenProject.Revit.ViewModels;
using OpenProject.Revit.Views;
using OpenProject.Shared.Pins;
using Serilog;

namespace OpenProject.Revit.Services
{
  public static class PinChatWindowManager
  {
    private static readonly Dictionary<string, PinChatWindow> Windows = new(StringComparer.OrdinalIgnoreCase);

    public static void OpenChat(PinRecord record)
    {
      if (record == null || string.IsNullOrWhiteSpace(record.WorkPackageId))
        return;

      var id = record.WorkPackageId;
      PinsPaneManager.EnqueueUiAction(() =>
      {
        try
        {
          if (Windows.TryGetValue(id, out var existing))
          {
            existing.Activate();
            return;
          }

          var doc = PinsPaneManager.UiApp?.ActiveUIDocument?.Document;
          var history = PinChatService.LoadMergedHistory(doc);
          var settings = McpSettingsStore.Load();
          var vm = new PinChatViewModel(id, record.Name, settings, history);
          var window = new PinChatWindow(vm);
          window.Closed += (_, _) => Windows.Remove(id);
          Windows[id] = window;
          window.Show();
          Log.Information("PinChat window opened workPackageId={Id}", id);
        }
        catch (Exception ex)
        {
          Log.Error(ex, "PinChat window failed workPackageId={Id}", id);
        }
      });
    }
  }
}
