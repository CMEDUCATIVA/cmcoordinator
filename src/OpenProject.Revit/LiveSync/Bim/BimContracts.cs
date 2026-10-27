using System.Collections.Generic;
using Newtonsoft.Json;

namespace OpenProject.Revit.LiveSync.Bim
{
  // Absent optional fields are omitted from the wire: a revision repeats this record ~100.000 times.
  // ReadOnly and DisplayUnitValue have no consumer in the CDE (viewer, change history or IFC publication).
  public sealed class BimParameter
  {
    public string Id { get; set; }
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string SharedGuid { get; set; }
    public string Name { get; set; }
    public string GroupId { get; set; }
    public string GroupName { get; set; }
    public string Storage { get; set; }
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string SpecId { get; set; }
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string UnitId { get; set; }
    public object Value { get; set; }
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public double? SiValue { get; set; }
    [JsonIgnore] public double? DisplayUnitValue { get; set; }
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string Display { get; set; }
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string ReferenceId { get; set; }
    [JsonIgnore] public bool ReadOnly { get; set; }
    public bool HasValue { get; set; }
  }
  public sealed class BimElement
  {
    public string DocumentId { get; set; }
    public string IfcGuid { get; set; }
    public string IfcClass { get; set; }
    public string IfcPredefinedType { get; set; }
    // Required by buildingSMART when the predefined type is USERDEFINED.
    public string IfcObjectType { get; set; }
    // Assembly that aggregates this element (curtain wall for its panels and mullions, stair for its flights).
    public string AggregateParentId { get; set; }
    public string TypeId { get; set; }
    public string LevelId { get; set; }
    public string HostId { get; set; }
    public string PhaseId { get; set; }
    public string CategoryId { get; set; }
    public List<BimParameter> Parameters { get; set; } = new();
    public List<IfcPropertySet> IfcSets { get; set; } = new();
  }
  public sealed class IfcPropertySet
  {
    public string Name { get; set; }
    public string Kind { get; set; }
    public List<IfcProperty> Properties { get; set; } = new();
  }
  public sealed class IfcProperty
  {
    public string Name { get; set; }
    public object Value { get; set; }
    public string Unit { get; set; }
    public string Type { get; set; }
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
