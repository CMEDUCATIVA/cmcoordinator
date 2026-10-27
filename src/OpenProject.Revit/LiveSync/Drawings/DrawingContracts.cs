using System.Collections.Generic;

namespace OpenProject.Revit.LiveSync.Drawings
{
  // A 2D view drawn as SVG. Coordinates: origin in model metres (same frame as the 3D elements),
  // right/up/direction unit vectors, bounds in view metres (u along right, v along up).
  public sealed class LiveDrawing
  {
    public string Id { get; set; }
    public string ViewId { get; set; }
    public string ElementId { get; set; }
    public string Name { get; set; }
    public string ViewType { get; set; }
    public int Scale { get; set; }
    public string LevelId { get; set; }
    public double[] Origin { get; set; }
    public double[] Right { get; set; }
    public double[] Up { get; set; }
    public double[] Direction { get; set; }
    public double[] Bounds { get; set; }
    // Keys of the elements drawn, the same keys as their 3D elements (UniqueId or link/UniqueId).
    public List<string> Elements { get; set; } = new();
    public string Svg { get; set; }

    public static string KeyFor(string viewUniqueId) => "vista-" + viewUniqueId;
  }
}
