using System;
using System.Windows;
using Serilog;

namespace OpenProject.Browser.Services
{
  public static class MessageHandler
  {
    public static void ShowError(string message)
    {
      Log.Error(message);
      MessageBox.Show(message, "ERROR", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public static void ShowError(Exception exception, string message)
    {
      Log.Error(exception, message);
      MessageBox.Show(message, "ERROR", MessageBoxButton.OK, MessageBoxImage.Error);
    }

  }
}
