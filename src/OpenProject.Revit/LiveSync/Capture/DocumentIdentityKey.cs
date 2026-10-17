using System;
using OpenProject.Revit.LiveSync.Queue;
namespace OpenProject.Revit.LiveSync.Capture
{
  public static class DocumentIdentityKey
  {
    public static string Create(string documentId)
    {
      if (string.IsNullOrWhiteSpace(documentId)) throw new ArgumentException("Falta la identidad del documento.", nameof(documentId));
      return SyncEncoding.Hash("revit-document-v3|" + documentId.Trim().ToLowerInvariant());
    }
  }
}
