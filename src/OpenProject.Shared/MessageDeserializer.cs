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

      return new BcfViewpointWrapper
      {
        Viewpoint = jObject.ToObject<Viewpoint_GET>(),
        Components = jObject["components"]?.ToObject<Components>()
      };
    }
  }
}
