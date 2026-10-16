using System.Collections.Generic;

namespace OpenProject.Revit.Models
{
  public sealed class CdeTableState
  {
    public Dictionary<string, bool> ColumnVisibility { get; set; }
    public List<string> ColumnOrder { get; set; }
    public Dictionary<string, double> ColumnWidths { get; set; }
  }
}
