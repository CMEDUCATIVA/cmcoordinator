using iabi.BCF.APIObjects.V21;
using Newtonsoft.Json.Linq;
using System;
using OpenProject.Shared.BcfApi;

namespace OpenProject.Shared
{
  public static class MessageDeserializer
  {
    public static BcfViewpointWrapper DeserializeBcfViewpoint(WebUiMessageEventArgs webUiMessage)
    {
      if (webUiMessage.MessageType != MessageTypes.VIEWPOINT_DATA)
        throw new InvalidOperationException("Tried to deserialize a message with the wrong data type");

      JObject jObject = JObject.Parse(webUiMessage.MessagePayload.Trim('"').Replace("\\\"", "\""));

      // Normalize snapshot if it's a data URI string.
      var snapshotToken = jObject["snapshot"];
      if (snapshotToken != null && snapshotToken.Type == JTokenType.String)
      {
        jObject["snapshot"] = new JObject
        {
          ["snapshot_data"] = snapshotToken
        };
      }

      return new BcfViewpointWrapper
      {
        Viewpoint = jObject.ToObject<Viewpoint_GET>(),
        Components = jObject["components"]?.ToObject<Components>()
      };
    }
  }
}
