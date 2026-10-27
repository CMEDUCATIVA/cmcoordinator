namespace CmConv.Lectura
{
  /// <summary>What a reader recovers from a .rvt, independent of the decoder that produced it.</summary>
  public sealed class RvtModel
  {
    public string Version { get; set; }
    public string Build { get; set; }
    public string Title { get; set; }
    public string DocumentGuid { get; set; }
    public List<RvtElement> Elements { get; } = new();
    /// <summary>Revit level ElementId → level, when the decoder can name it.</summary>
    public Dictionary<long, RvtLevel> Levels { get; } = new();
    public List<string> Warnings { get; } = new();
  }

  public sealed class RvtElement
  {
    public long Id { get; set; }
    /// <summary>Decoder class (rvt-rs taxonomy: Wall, CurtainWallMullion, StairsRun…).</summary>
    public string ClassName { get; set; }
    public long? CategoryId { get; set; }
    public string FamilyName { get; set; }
    public string TypeName { get; set; }
    public long? TypeId { get; set; }
    public long? LevelId { get; set; }
    /// <summary>Level the element is placed on as Revit names it (a beam's reference level), which can differ from <see cref="LevelId"/>.</summary>
    public string LevelName { get; set; }
    /// <summary>Angle of a framing member's axis above the horizontal, in degrees, when the file records the axis.</summary>
    public double? AxisInclinationDegrees { get; set; }
    /// <summary>IFC class proposed by the decoder, used only when our own table has none.</summary>
    public string IfcHint { get; set; }
    /// <summary>Assembly that aggregates this element (curtain wall of a mullion, stair of a flight).</summary>
    public long? AggregateId { get; set; }
    public string IfcGuid { get; set; }
    /// <summary>Straight wall axis length and wall type thickness, in feet (Revit internal units).</summary>
    public double? WallAxisFeet { get; set; }
    public double? WallThicknessFeet { get; set; }
    public List<RvtParameter> Parameters { get; } = new();
    public List<RvtMesh> Meshes { get; } = new();
  }

  public sealed class RvtParameter
  {
    public long Id { get; set; }
    public string Name { get; set; }
    public string Storage { get; set; }
    public object Value { get; set; }
    public double? SiValue { get; set; }
    public string SiUnit { get; set; }
    /// <summary>"instance" or "type".</summary>
    public string Scope { get; set; }
  }

  /// <summary>A triangle mesh in metres, Revit internal coordinates, Z up.</summary>
  public sealed class RvtMesh
  {
    public double[] Positions { get; set; }
    public int[] Indices { get; set; }
    public string Color { get; set; } = "#8bb8c9";
    public double Opacity { get; set; } = 1;
  }

  public sealed class RvtLevel
  {
    public long Id { get; set; }
    public string Name { get; set; }
    public double ElevationMetres { get; set; }
    public string IfcGuid { get; set; }
  }

  public enum MeshDetail
  {
    /// <summary>Decoded bodies; family instances as their bounding box. Light and fast.</summary>
    Boxes,
    /// <summary>The display meshes Revit saved in the file. Real shapes, heavy.</summary>
    Saved,
    /// <summary>Decoded bodies where they are real; saved meshes, simplified, where the decoded body is only a box.</summary>
    Mixed
  }

  /// <summary>Pluggable decoder contract: rvt-rs today, any other engine (or a mock) tomorrow.</summary>
  public interface IRvtReader
  {
    string Name { get; }
    RvtModel Read(string path, MeshDetail detail, CancellationToken token);
  }
}
