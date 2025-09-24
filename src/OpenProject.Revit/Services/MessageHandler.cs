using System;
using Autodesk.Revit.UI;
using Serilog;

namespace OpenProject.Revit.Services
{
  public static class MessageHandler
  {
    public static void ShowWarning(string title, string instruction, string content)
    {
      Log.Warning($"{title}: {content}");

      using var dialog = new TaskDialog(title)
      {
        TitleAutoPrefix = true,
        MainIcon = TaskDialogIcon.TaskDialogIconWarning,
        MainInstruction = instruction,
        MainContent = content
      };

      dialog.Show();
    }

    public static void ShowError(Exception exception, string message)
    {
      if (exception != null)
        Log.Error(exception, message);
      else
        Log.Error(message);

      using var dialog = new TaskDialog("Error")
      {
        TitleAutoPrefix = true,
        MainIcon = TaskDialogIcon.TaskDialogIconError,
        MainContent = message
      };

      dialog.Show();
    }
  }
}
