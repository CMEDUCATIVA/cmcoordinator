using System;

namespace OpenProject.Revit.Models
{
  public sealed class PinChatMessage
  {
    public string Text { get; set; }
    public DateTimeOffset Timestamp { get; set; }
  }
}
