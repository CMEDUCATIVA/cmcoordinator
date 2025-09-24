using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace OpenProject.Browser.WebViewIntegration
{
  /// <summary>
  /// This class is used to set the focus to the Revit desktop application,
  /// to ensure that after an action is called the Revit UI thread processes
  /// the new data.
  /// </summary>
  public static class RevitMainWindowHandler
  {
    public static void SetFocusToRevit()
    {
      Process revitProcess = Process
        .GetProcesses()
        .FirstOrDefault(p => p.ProcessName == "Revit");
      if (revitProcess != null) SetForegroundWindow(revitProcess.MainWindowHandle);
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
  }
}
