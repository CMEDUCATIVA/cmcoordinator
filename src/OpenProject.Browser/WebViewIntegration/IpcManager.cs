using Newtonsoft.Json;
using OpenProject.Shared;
using ZetaIpc.Runtime.Client;
using ZetaIpc.Runtime.Server;

namespace OpenProject.Browser.WebViewIntegration
{
  public static class IpcManager
  {
    public static void StartIpcCommunication(JavaScriptBridge javaScriptBridge, int serverPort, int clientPort)
    {
      var server = new IpcServer();
      server.Start(serverPort);
      server.ReceivedRequest += (sender, e) =>
      {
        var deserialized = JsonConvert.DeserializeObject<WebUiMessageEventArgs>(e.Request);
        javaScriptBridge.SendMessageToOpenProject(
          deserialized.MessageType, deserialized.TrackingId, deserialized.MessagePayload);
      };

      var client = new IpcClient();
      client.Initialize(clientPort);
      javaScriptBridge.OnWebUiMessageReceived += (s, e) => client.Send(JsonConvert.SerializeObject(e));
    }
  }
}
