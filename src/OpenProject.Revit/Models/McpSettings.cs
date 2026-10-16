using System.Collections.Generic;

namespace OpenProject.Revit.Models
{
  public sealed class McpSettings
  {
    public string Url { get; set; }
    public string ApiToken { get; set; }
    public bool ApiConnected { get; set; }
    public Dictionary<string, bool> ColumnVisibility { get; set; }
    public List<string> ColumnOrder { get; set; }
    public Dictionary<string, double> ColumnWidths { get; set; }
    public bool AutoRefreshEnabled { get; set; }
    public int AutoRefreshIntervalValue { get; set; }
    public string AutoRefreshIntervalUnit { get; set; }
    public bool DevToolsEnabled { get; set; }
    public bool StatusHighlightEnabled { get; set; }
    public bool AiAgentEnabled { get; set; }
    public bool AiAutoConnect { get; set; }
    public string AiBridgeUrl { get; set; }
    public string AiWorkstationId { get; set; }
    public string AiBridgeToken { get; set; }
    public string AiAgentId { get; set; }
  }
}
