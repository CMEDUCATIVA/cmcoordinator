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
using OpenProject.Shared.Pins;
using ZetaIpc.Runtime.Client;
using ZetaIpc.Runtime.Server;
using ZetaIpc.Runtime.Helper;
using Serilog;
using System.Threading;
using System.Threading.Tasks;

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
      PinsPaneManager.SetUIApplication(uiApp);

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
              case MessageTypes.PINS_UPDATE:
                try
                {
                  Log.Information("IPC Received PINS_UPDATE from DesktopApp. payloadLength={Len}", localMessagePayload?.Length ?? 0);
                  var pins = JsonConvert.DeserializeObject<List<PinRecord>>(localMessagePayload) ?? new List<PinRecord>();
                  foreach (var pin in pins.Where(p => p != null && !string.IsNullOrWhiteSpace(p.WorkPackageId)))
                  {
                    Log.Information(
                      "IPC PINS_UPDATE pin workPackageId={WorkPackageId} ifcGuids={IfcGuids} viewpointLength={ViewpointLength} snapshotUrl={HasSnapshotUrl} revitViewId={ViewId} revitViewName='{ViewName}'",
                      pin.WorkPackageId,
                      pin.IfcGuids?.Count ?? 0,
                      pin.ViewpointJson?.Length ?? 0,
                      !string.IsNullOrWhiteSpace(pin.SnapshotUrl),
                      pin.RevitViewId?.ToString() ?? "null",
                      pin.RevitViewName ?? string.Empty);
                  }
                  PinsPaneManager.UpdatePins(pins, preserveExistingDisplayData: true);
                  EnrichPinsWithApiAsync(pins);
                }
                catch (Exception exception)
                {
                  MessageHandler.ShowError(exception, "Error updating pins.");
                }

                break;
              case MessageTypes.PIN_FOCUS:
                try
                {
                  var workPackageId = JObject.Parse(localMessagePayload)["workPackageId"]?.ToString();
                  PinsManager.FocusPin(_uiApp, workPackageId);
                }
                catch (Exception exception)
                {
                  MessageHandler.ShowError(exception, "Error focusing pin.");
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
        // Fire-and-forget to avoid blocking Revit UI if IPC is slow.
        System.Threading.Tasks.Task.Run(() =>
        {
          try
          {
            client.Send(message);
          }
          catch (System.Net.WebException)
          {
            // We can ignore the WebException, it's raised after
            // the shutdown event due to the other side just closing
            // the open TCP connection. This is what's expected??
          }
        });
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

    public void SendOpenUrlRequestToDesktopApp(string url)
    {
      if (string.IsNullOrWhiteSpace(url))
        return;
      Log.Information("IPC Send OPEN_URL to DesktopApp. url={Url}", url);
      var eventArgs = new WebUiMessageEventArgs(MessageTypes.OPEN_URL, "0", url);
      var jsonEventArgs = JsonConvert.SerializeObject(eventArgs);
      _sendData(jsonEventArgs);
    }

    public void SendPinsUpdateToDesktopApp(List<PinRecord> pins)
    {
      if (_sendData == null) return;
      var payload = JsonConvert.SerializeObject(pins ?? new List<PinRecord>());
      Log.Information("IPC Send PINS_UPDATE to DesktopApp. payloadLength={Len}", payload.Length);
      var eventArgs = new WebUiMessageEventArgs(MessageTypes.PINS_UPDATE, "0", payload);
      var jsonEventArgs = JsonConvert.SerializeObject(eventArgs);
      _sendData(jsonEventArgs);
    }

    private void EnrichPinsWithApiAsync(List<PinRecord> pins)
    {
      if (pins == null || pins.Count == 0)
        return;

      var missing = pins.Where(p => !string.IsNullOrWhiteSpace(p.WorkPackageId))
        .ToList();
      if (missing.Count == 0)
        return;

      Task.Run(async () =>
      {
        var settings = McpSettingsStore.Load();
        var updated = false;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        foreach (var pin in missing)
        {
          var info = await McpClient.GetWorkPackageInfoAsync(pin.WorkPackageId, settings, cts.Token).ConfigureAwait(false);
          if (info != null)
          {
            Log.Information("API values workPackageId={Id} assignee='{Assignee}' responsible='{Responsible}'",
              pin.WorkPackageId,
              info.Assignee ?? string.Empty,
              info.Responsible ?? string.Empty);

            var changed = false;
            changed |= ApplyFieldAllowEmpty(pin.ProjectId, info.ProjectId, v => pin.ProjectId = v);
            changed |= ApplyFieldAllowEmpty(pin.ProjectIdentifier, info.ProjectIdentifier, v => pin.ProjectIdentifier = v);
            changed |= ApplyFieldAllowEmpty(pin.ProjectName, info.ProjectName, v => pin.ProjectName = v);
            changed |= ApplyFieldAllowEmpty(pin.Name, info.Name, v => pin.Name = v);
            changed |= ApplyFieldAllowEmpty(pin.Type, info.Type, v => pin.Type = v);
            changed |= ApplyFieldAllowEmpty(pin.Priority, info.Priority, v => pin.Priority = v);
            changed |= ApplyFieldAllowEmpty(pin.Status, info.Status, v => pin.Status = v);
            changed |= ApplyFieldAllowEmpty(pin.StatusColor, info.StatusColor, v => pin.StatusColor = v);
            var assigneeChanged = ApplyFieldAllowEmpty(pin.Assignee, info.Assignee, v => pin.Assignee = v);
            var responsibleChanged = ApplyFieldAllowEmpty(pin.Responsible, info.Responsible, v => pin.Responsible = v);
            changed |= assigneeChanged;
            changed |= responsibleChanged;
            if (assigneeChanged && string.IsNullOrWhiteSpace(info.Assignee))
              Log.Information("API field cleared workPackageId={Id} field=Assignee", pin.WorkPackageId);
            if (responsibleChanged && string.IsNullOrWhiteSpace(info.Responsible))
              Log.Information("API field cleared workPackageId={Id} field=Responsible", pin.WorkPackageId);

            if (changed)
            {
              Log.Information(
                "API fields applied workPackageId={Id} name={Name} type={Type} priority={Priority} status={Status} assignee={Assignee} responsible={Responsible}",
                pin.WorkPackageId,
                pin.Name ?? string.Empty,
                pin.Type ?? string.Empty,
                pin.Priority ?? string.Empty,
                pin.Status ?? string.Empty,
                pin.Assignee ?? string.Empty,
                pin.Responsible ?? string.Empty);
              updated = true;
            }
          }
        }

        if (!updated)
          return;

        lock (_callbackStackLock)
        {
          _callbackStack.Push(() =>
          {
            try
            {
              PinsPaneManager.UpdatePins(pins);
              Log.Information("API updating DesktopApp with names. pinsCount={Count}", pins.Count);
              SendPinsUpdateToDesktopApp(pins);
            }
            catch (Exception ex)
            {
              Log.Error(ex, "Failed to apply API pin updates");
            }
          });
        }
      });
    }

    private static bool ApplyField(string current, string value, Action<string> apply)
    {
      if (string.IsNullOrWhiteSpace(value))
        return false;
      if (string.Equals(current, value, StringComparison.Ordinal))
        return false;
      apply(value);
      return true;
    }

    private static bool ApplyFieldAllowEmpty(string current, string value, Action<string> apply)
    {
      var normalized = value ?? string.Empty;
      if (string.Equals(current ?? string.Empty, normalized, StringComparison.Ordinal))
        return false;
      apply(normalized);
      return true;
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
          "In order to capture BCF viewpoints, the Cmcoordinator Revit Add-In requires a 3D view.");
        return;
      }

      var swTotal = System.Diagnostics.Stopwatch.StartNew();
      Serilog.Log.Information("Starting viewpoint generation (trackingId {TrackingId})", trackingId);

      JObject payload = GenerateJsonViewpoint();

      // TODO: remove hack of snapshot data once #39135 is deployed
      payload["snapshot"] = payload["snapshot"]?["snapshot_data"];

      var eventArgs = new WebUiMessageEventArgs(MessageTypes.VIEWPOINT_GENERATED, trackingId, payload.ToString());
      var swSend = System.Diagnostics.Stopwatch.StartNew();
      _sendData(JsonConvert.SerializeObject(eventArgs));
      swSend.Stop();
      swTotal.Stop();
      Serilog.Log.Information("Viewpoint generated and sent (trackingId {TrackingId}) in {TotalMs} ms (send {SendMs} ms)",
        trackingId, swTotal.ElapsedMilliseconds, swSend.ElapsedMilliseconds);
    }

    private JObject GenerateJsonViewpoint()
    {
      var sw = System.Diagnostics.Stopwatch.StartNew();
      var serializerSettings = new JsonSerializerSettings
      {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
      };

      serializerSettings.Converters.Add(new StringEnumConverter(new SnakeCaseNamingStrategy(), false));

      var uiDoc = _uiApp.ActiveUIDocument;
      Viewpoint_POST viewpoint = uiDoc.GenerateViewpoint();
      var json = JObject.Parse(JsonConvert.SerializeObject(viewpoint, serializerSettings));
      var activeView = uiDoc.ActiveView;
      if (activeView != null)
      {
        json["revit_view_id"] = (int)activeView.Id.GetIntValue();
        json["revit_view_name"] = activeView.Name;
      }
      sw.Stop();
      Serilog.Log.Information("GenerateJsonViewpoint took {ElapsedMs} ms", sw.ElapsedMilliseconds);
      return json;
    }
  }
}
