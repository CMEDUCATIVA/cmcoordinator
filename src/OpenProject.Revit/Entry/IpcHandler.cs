using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using OpenProject.Shared;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using iabi.BCF.APIObjects.V21;
using Newtonsoft.Json.Converters;
using OpenProject.Revit.Extensions;
using OpenProject.Revit.Services;
using OpenProject.Shared.BcfApi;
using ZetaIpc.Runtime.Client;
using ZetaIpc.Runtime.Server;
using ZetaIpc.Runtime.Helper;

namespace OpenProject.Revit.Entry
{
  public class IpcHandler
  {
    private readonly UIApplication _uiApp;
    private Action<string> _sendData;
    private static readonly object _callbackStackLock = new();
    private static readonly Stack<Action> _callbackStack = new();

    public IpcHandler(UIApplication uiApp)
    {
      _uiApp = uiApp ?? throw new ArgumentNullException(nameof(uiApp));

      uiApp.Idling += (_, _) =>
      {
        lock (_callbackStackLock)
        {
          if (!_callbackStack.Any()) return;

          _callbackStack.Pop().Invoke();
        }
      };
    }

    public int StartLocalServerAndReturnPort()
    {
      var freePort = FreePortHelper.GetFreePort();
      var server = new IpcServer();
      server.Start(freePort);
      server.ReceivedRequest += (_, e) =>
      {
        var eventArgs = JsonConvert.DeserializeObject<WebUiMessageEventArgs>(e.Request);
        if (eventArgs == null) return;

        var localMessageType = eventArgs.MessageType;
        var localTrackingId = eventArgs.TrackingId;
        var localMessagePayload = eventArgs.MessagePayload;

        lock (_callbackStackLock)
        {
          _callbackStack.Push(() =>
          {
            switch (localMessageType)
            {
              case MessageTypes.VIEWPOINT_DATA:
              {
                try
                {
                  BcfViewpointWrapper bcfViewpoint = MessageDeserializer.DeserializeBcfViewpoint(
                    new WebUiMessageEventArgs(localMessageType, localTrackingId, localMessagePayload));
                  OpenViewpointEventHandler.ShowBcfViewpoint(bcfViewpoint);
                }
                catch (Exception exception)
                {
                  MessageHandler.ShowError(exception, "Error opening a viewpoint.");
                }

                break;
              }
              case MessageTypes.VIEWPOINT_GENERATION_REQUESTED:
                try
                {
                  AddViewpoint(localTrackingId);
                }
                catch (Exception exception)
                {
                  MessageHandler.ShowError(exception, "Error generating a viewpoint.");
                }

                break;
            }
          });
        }
      };

      return freePort;
    }

    public void StartLocalClient(int ipcWinServerPort)
    {
      var client = new IpcClient();
      client.Initialize(ipcWinServerPort);
      _sendData = message =>
      {
        try
        {
          client.Send(message);
        }
        catch (System.Net.WebException)
        {
          // We can ignore the WebException, it's raised after
          // the shutdown event due to the other side just closing
          // the open TCP connection. This is what's expected😊
        }
      };
    }

    public void SendShutdownRequestToDesktopApp()
    {
      var eventArgs = new WebUiMessageEventArgs(MessageTypes.CLOSE_DESKTOP_APPLICATION, "0", string.Empty);
      var jsonEventArgs = JsonConvert.SerializeObject(eventArgs);
      _sendData(jsonEventArgs);
    }

    public void SendOpenSettingsRequestToDesktopApp()
    {
      var eventArgs = new WebUiMessageEventArgs(MessageTypes.GO_TO_SETTINGS, "0", string.Empty);
      var jsonEventArgs = JsonConvert.SerializeObject(eventArgs);
      _sendData(jsonEventArgs);
    }

    public void SendBringBrowserToForegroundRequestToDesktopApp()
    {
      var eventArgs = new WebUiMessageEventArgs(MessageTypes.SET_BROWSER_TO_FOREGROUND, "0", string.Empty);
      var jsonEventArgs = JsonConvert.SerializeObject(eventArgs);
      _sendData(jsonEventArgs);
    }

    /// <summary>
    /// Generates a viewpoint from the active view and sends the data as an event to bridge.
    /// </summary>
    /// <param name="trackingId">The local message tracking id.</param>
    private void AddViewpoint(string trackingId)
    {
      if (_uiApp.ActiveUIDocument.ActiveView.ViewType != ViewType.ThreeD)
      {
        MessageHandler.ShowWarning(
          "Invalid view",
          "Active UI document is not a 3D view",
          "In order to capture BCF viewpoints, the OpenProject Revit Add-In requires a 3D view.");
        return;
      }

      JObject payload = GenerateJsonViewpoint();

      // TODO: remove hack of snapshot data once #39135 is deployed
      payload["snapshot"] = payload["snapshot"]?["snapshot_data"];

      var eventArgs = new WebUiMessageEventArgs(MessageTypes.VIEWPOINT_GENERATED, trackingId, payload.ToString());
      _sendData(JsonConvert.SerializeObject(eventArgs));
    }

    private JObject GenerateJsonViewpoint()
    {
      var serializerSettings = new JsonSerializerSettings
      {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
      };

      serializerSettings.Converters.Add(new StringEnumConverter(new SnakeCaseNamingStrategy(), false));

      Viewpoint_POST viewpoint = _uiApp.ActiveUIDocument.GenerateViewpoint();
      return JObject.Parse(JsonConvert.SerializeObject(viewpoint, serializerSettings));
    }
  }
}
