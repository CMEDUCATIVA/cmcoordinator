using System.Collections.Generic;
using Newtonsoft.Json;
using OpenProject.Browser.Services;
using OpenProject.Shared;
using Serilog;
using ZetaIpc.Runtime.Client;
using ZetaIpc.Runtime.Server;
using OpenProject.Shared.Authentication;

namespace OpenProject.Browser.WebViewIntegration
{
  public static class IpcManager
  {
    private static IpcClient _client;
    public static event System.Action SessionCheckRequested;

    public static void StartIpcCommunication(JavaScriptBridge javaScriptBridge, PinsService pinsService, int serverPort, int clientPort)
    {
      var server = new IpcServer();
      server.Start(serverPort);
      server.ReceivedRequest += (sender, e) =>
      {
        var deserialized = JsonConvert.DeserializeObject<WebUiMessageEventArgs>(e.Request);
        if (deserialized == null) return;
        if (deserialized.MessageType == MessageTypes.CHECK_CDE_SESSION)
        { SessionCheckRequested?.Invoke(); return; }
        if (deserialized?.MessageType == MessageTypes.PINS_UPDATE)
        {
          Log.Information("IPC Received PINS_UPDATE from Revit. payloadLength={Len}", deserialized.MessagePayload?.Length ?? 0);
          var pins = JsonConvert.DeserializeObject<List<OpenProject.Shared.Pins.PinRecord>>(deserialized.MessagePayload)
                     ?? new List<OpenProject.Shared.Pins.PinRecord>();
          pinsService.ReplacePinsFromRevit(pins);
          return;
        }
        javaScriptBridge.SendMessageToOpenProject(
          deserialized.MessageType, deserialized.TrackingId, deserialized.MessagePayload);
      };

      _client = new IpcClient();
      _client.Initialize(clientPort);
      javaScriptBridge.OnWebUiMessageReceived += (s, e) => _client.Send(JsonConvert.SerializeObject(e));
    }

    public static void SendCdeSession(string nonce, long sequence, CdeSessionObservation observation)
    {
      if (_client == null) return;
      var message = new WebUiMessageEventArgs(MessageTypes.CDE_SESSION_STATUS, nonce,
        JsonConvert.SerializeObject(new { sequence, userId = observation.UserId, origin = observation.Origin,
          sessionState = observation.Status.ToString(), observedUtc = System.DateTime.UtcNow }));
      System.Threading.Tasks.Task.Run(() =>
      {
        try { _client.Send(JsonConvert.SerializeObject(message)); }
        catch (System.Exception) { Log.Warning("No se pudo entregar el estado de sesión a Revit"); }
      });
    }

    public static void SendPinsUpdate(IEnumerable<OpenProject.Shared.Pins.PinRecord> pins)
    {
      if (_client == null) return;
      var payload = JsonConvert.SerializeObject(pins);
      Log.Information("IPC Send PINS_UPDATE to Revit. payloadLength={Len}", payload.Length);
      var args = new OpenProject.Shared.WebUiMessageEventArgs(
        OpenProject.Shared.MessageTypes.PINS_UPDATE, "0", payload);
      _client.Send(JsonConvert.SerializeObject(args));
    }

    public static void SendPinFocus(string workPackageId)
    {
      if (_client == null) return;
      var payload = JsonConvert.SerializeObject(new { workPackageId });
      var args = new OpenProject.Shared.WebUiMessageEventArgs(
        OpenProject.Shared.MessageTypes.PIN_FOCUS, "0", payload);
      _client.Send(JsonConvert.SerializeObject(args));
    }
  }
}
