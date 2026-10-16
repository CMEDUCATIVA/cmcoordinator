using System.Collections.Generic;

namespace OpenProject.Revit.Models
{
  public sealed class PinChatHistory
  {
    public Dictionary<string, List<PinChatMessage>> Messages { get; set; } = new();
  }
}
