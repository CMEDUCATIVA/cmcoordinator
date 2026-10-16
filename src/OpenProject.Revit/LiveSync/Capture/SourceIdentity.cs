using System;
using Autodesk.Revit.DB;
using OpenProject.Revit.LiveSync.Queue;

namespace OpenProject.Revit.LiveSync.Capture
{
  internal static class SourceIdentity
  {
    public static string Read(Document document)
    {
      string location;
      if (document.IsModelInCloud)
      {
        var path = document.GetCloudModelPath();
        location = $"cloud:{path.GetProjectGUID()}:{path.GetModelGUID()}";
      }
      else location = string.IsNullOrEmpty(document.PathName)
        ? $"unsaved:{Environment.ProcessId}:{document.GetHashCode()}" : document.PathName.ToLowerInvariant();
      return SyncEncoding.Hash(document.ProjectInformation.UniqueId + "|" + location + "|local-v2");
    }
  }
}
