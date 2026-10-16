using System.Collections.Generic;

namespace OpenProject.Revit.LiveSync.Bim
{
  public sealed class BimParameter
  {
    public string Id { get; set; }
    public string SharedGuid { get; set; }
    public string Name { get; set; }
    public string Storage { get; set; }
    public string SpecId { get; set; }
    public string UnitId { get; set; }
    public object Value { get; set; }
    public double? SiValue { get; set; }
    public double? DisplayUnitValue { get; set; }
    public string Display { get; set; }
    public string ReferenceId { get; set; }
    public bool ReadOnly { get; set; }
    public bool HasValue { get; set; }
  }
  public sealed class BimElement
  {
    public string DocumentId { get; set; }
    public string IfcGuid { get; set; }
    public string IfcClass { get; set; }
    public string TypeId { get; set; }
    public string LevelId { get; set; }
    public string HostId { get; set; }
    public string PhaseId { get; set; }
    public string CategoryId { get; set; }
    public List<BimParameter> Parameters { get; set; } = new();
  }
  public sealed class BimContext
  {
    public int Schema { get; set; } = 2;
    public string ScopeId { get; set; }
    public string Scope { get; set; } = "visible-3d";
    public List<BimDocument> Documents { get; set; } = new();
    public Dictionary<string, BimLevel> Levels { get; set; } = new();
    public Dictionary<string, BimType> Types { get; set; } = new();
    public Dictionary<string, BimMaterial> Materials { get; set; } = new();
    public Dictionary<string, string> Phases { get; set; } = new();
  }
  public sealed class BimDocument
  {
    public string Id { get; set; }
    public string Title { get; set; }
    public string Number { get; set; }
    public double LatitudeRadians { get; set; }
    public double LongitudeRadians { get; set; }
    public double[] ProjectPosition { get; set; }
    public double[] ProjectLocationTransform { get; set; }
    public List<BimParameter> Parameters { get; set; } = new();
  }
  public sealed class BimLevel
  {
    public string Id { get; set; }
    public string DocumentId { get; set; }
    public string Name { get; set; }
    public double Elevation { get; set; }
  }
  public sealed class BimType
  {
    public string Id { get; set; }
    public string DocumentId { get; set; }
    public string Name { get; set; }
    public string Family { get; set; }
    public string IfcGuid { get; set; }
    public List<BimParameter> Parameters { get; set; } = new();
    public List<BimLayer> Layers { get; set; } = new();
  }
  public sealed class BimLayer
  {
    public string MaterialId { get; set; }
    public double Thickness { get; set; }
    public string Function { get; set; }
  }
  public sealed class BimMaterial
  {
    public string Id { get; set; }
    public string Name { get; set; }
    public string Category { get; set; }
    public string Color { get; set; }
    public double Opacity { get; set; }
    public List<BimParameter> Parameters { get; set; } = new();
  }
}
