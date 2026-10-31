using System;
using System.IO;

namespace OpenProject.Revit.LiveSync.Queue
{
  /// <summary>
  /// The folder of a queue. Copies of a model share its identity, but not its history: what a copy lacks was not deleted by
  /// the person, and what it holds is not what this queue confirmed. The first file to use a queue keeps it; any other file
  /// of the same project starts its own queue, so it starts from the CDE like a new connection and removes nothing.
  /// </summary>
  public static class QueueFolder
  {
    private const string OwnerFile = "file.txt";

    // Read-only: reading the history of a document must not give it the queue.
    public static string Find(string queue, string documentPath)
    {
      var owner = Owner(queue);
      return owner == null || owner == Normalize(documentPath) ? queue : Copy(queue, documentPath);
    }

    /// <summary>The folder this file works in. A queue without owner (also one from an earlier version) becomes this file's.</summary>
    public static string Claim(string queue, string documentPath)
    {
      if (Owner(queue) != null) return Find(queue, documentPath);
      Directory.CreateDirectory(queue);
      File.WriteAllText(Path.Combine(queue, OwnerFile), Normalize(documentPath));
      return queue;
    }

    /// <summary>«Guardar como» in the open document: the same work goes on under another name, so the queue follows it.</summary>
    public static void Relocate(string folder, string documentPath)
    {
      if (Directory.Exists(folder)) File.WriteAllText(Path.Combine(folder, OwnerFile), Normalize(documentPath));
    }

    private static string Owner(string queue)
    {
      var file = Path.Combine(queue, OwnerFile);
      return File.Exists(file) ? File.ReadAllText(file) : null;
    }

    private static string Copy(string queue, string documentPath) =>
      Path.Combine(queue, "copias", SyncEncoding.Hash(Normalize(documentPath)).Substring(0, 24));

    private static string Normalize(string documentPath) => (documentPath ?? "").Trim().Replace('/', '\\').ToLowerInvariant();
  }
}
