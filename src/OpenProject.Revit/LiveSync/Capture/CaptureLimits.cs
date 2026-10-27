namespace OpenProject.Revit.LiveSync.Capture
{
  /// <summary>
  /// Safety limits of one capture. Revisions travel in resumable parts and the viewer draws them in
  /// batched groups, so these only guard against pathological views, not ordinary large models.
  /// </summary>
  internal static class CaptureLimits
  {
    public const int ViewElements = 200_000;
    public const long ViewVertices = 15_000_000;
    public const int ElementVertices = 2_000_000;
    // Stays below the CDE limit of 20.000 elements per part, whatever the element size.
    public const int PartElements = 10_000;
  }
}
