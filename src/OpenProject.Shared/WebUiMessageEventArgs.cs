using System;

namespace OpenProject.Shared
{
  /// <summary>
  /// Immutable class wrapping event arguments for a web UI message. Contains the message type,
  /// a tracking id and the payload.
  /// </summary>
  public sealed class WebUiMessageEventArgs : EventArgs
  {
    public WebUiMessageEventArgs(string messageType, string trackingId, string messagePayload)
    {
      MessageType = messageType;
      TrackingId = trackingId;
      MessagePayload = messagePayload;
    }

    public string MessageType { get; }
    public string MessagePayload { get; }
    public string TrackingId { get; }
  }
}
