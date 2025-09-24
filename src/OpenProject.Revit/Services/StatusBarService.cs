using System;
using System.Runtime.InteropServices;

namespace OpenProject.Revit.Services
{
  /// <summary>
  /// This service is based on the idea of Revit developer Jeremy Tammik (https://thebuildingcoder.typepad.com/blog/2011/02/status-bar-text.html)
  /// </summary>
  public static class StatusBarService
  {
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int SetWindowText(IntPtr hWnd, string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass,
      string lpszWindow);

    /// <summary>
    /// Sets a text to the revit main window status bar.
    /// </summary>
    /// <param name="text">The text to be displayed in the status bar.</param>
    public static void SetStatusText(string text)
    {
      IntPtr revitHandle = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
      IntPtr statusBar = FindWindowEx(revitHandle, IntPtr.Zero, "msctls_statusbar32", "");

      if (statusBar != IntPtr.Zero)
        SetWindowText(statusBar, text);
    }

    /// <summary>
    /// Resets the status bar text.
    /// </summary>
    public static void ResetStatusBarText() => SetStatusText("Ready");
  }
}
