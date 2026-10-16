using System;

namespace OpenProject.Revit.LiveSync.Telemetry
{
  public static class SyncFailure
  {
    // Preserve diagnostic identity without putting filesystem paths, headers or model values in exported logs.
    public static string Describe(Exception error)
    {
      var root = error;
      for (var i = 0; i < 4 && root.InnerException != null; i++) root = root.InnerException;
      var method = root.TargetSite;
      return $"{root.GetType().FullName} · 0x{root.HResult:X8} · {method?.DeclaringType?.FullName}.{method?.Name}";
    }
  }
}
