using System;
using System.Diagnostics;
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

    /// <summary>
    /// Displays an update dialog, which indicates that a newer add-in version is available for download.
    /// User can decide whether to download update or not.
    /// </summary>
    /// <param name="oldVersion">The string representation of the currently installed version.</param>
    /// <param name="newVersion">The string representation of the new version.</param>
    /// <param name="releaseDate">The release date of the new version.</param>
    /// <param name="downloadUrl">The URL where the new version can be downloaded from.</param>
    public static void ShowUpdateDialog(string oldVersion, string newVersion, DateTime releaseDate, string downloadUrl)
    {
      Log.Information("New version {new} available. Currently installed: {old}", newVersion, oldVersion);
      var messageBoxText = $"A new release '{newVersion}' was released at {releaseDate.ToLongDateString()}. " +
                           $"'{oldVersion}' is currently installed.\nDo you want to download the new version?";
      MessageBoxResult messageBoxResult = MessageBox.Show(
        messageBoxText,
        "New version available",
        MessageBoxButton.YesNo,
        MessageBoxImage.Information
      );

      if (messageBoxResult != MessageBoxResult.Yes) return;

      Process.Start(new ProcessStartInfo("cmd", $"/c start {downloadUrl}") { CreateNoWindow = true });
      Log.Information("Download of new version {new} started.", newVersion);
    }
  }
}
