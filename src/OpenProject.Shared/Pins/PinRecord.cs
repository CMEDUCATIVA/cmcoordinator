using System.Collections.Generic;

namespace OpenProject.Shared.Pins
{
  public sealed class PinRecord
  {
    public string WorkPackageId { get; set; }
    public string ProjectId { get; set; }
    public string ProjectIdentifier { get; set; }
    public string ProjectName { get; set; }
    public string Name { get; set; }
    public string Type { get; set; }
    public string Priority { get; set; }
    public string Status { get; set; }
    public string StatusColor { get; set; }
    public string Assignee { get; set; }
    public string Responsible { get; set; }
    public string Url { get; set; }
    public string SnapshotUrl { get; set; }
    public string ViewpointJson { get; set; }
    public long? RevitViewId { get; set; }
    public string RevitViewName { get; set; }
    public List<string> IfcGuids { get; set; } = new List<string>();
    public bool Visible { get; set; } = true;
  }
}
