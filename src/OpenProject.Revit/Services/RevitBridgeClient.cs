using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using OpenProject.Revit.Models;
using Serilog;

namespace OpenProject.Revit.Services
{
  public static class RevitBridgeClient
  {
    private static readonly object Sync = new();
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(120);
    private static readonly BridgeExternalEventHandler Handler = new();

    private static bool _initialized;
    private static bool _enabled;
    private static bool _autoConnect;
    private static string _bridgeUrl;
    private static string _workstationId;
    private static string _bridgeToken;
    private static string _agentId;

    private static ExternalEvent _externalEvent;
    private static ClientWebSocket _socket;
    private static CancellationTokenSource _cts;
    private static Task _runTask;
    private static readonly SemaphoreSlim SendLock = new(1, 1);

    public static void Initialize()
    {
      lock (Sync)
      {
        if (_initialized)
          return;

        _externalEvent = ExternalEvent.Create(Handler);
        _initialized = true;
      }
    }

    public static void ApplySettings(McpSettings settings)
    {
      if (settings == null)
        return;

      lock (Sync)
      {
        _enabled = settings.AiAgentEnabled;
        _autoConnect = settings.AiAutoConnect;
        _bridgeUrl = (settings.AiBridgeUrl ?? string.Empty).Trim();
        _workstationId = (settings.AiWorkstationId ?? string.Empty).Trim();
        _bridgeToken = settings.AiBridgeToken ?? string.Empty;
        _agentId = (settings.AiAgentId ?? string.Empty).Trim();
      }

      if (!_enabled || !_autoConnect || string.IsNullOrWhiteSpace(_bridgeUrl) || string.IsNullOrWhiteSpace(_workstationId))
      {
        Stop();
        return;
      }

      Start();
    }

    public static void Start()
    {
      if (!_initialized)
        return;

      lock (Sync)
      {
        if (_runTask is { IsCompleted: false })
          return;

        _cts = new CancellationTokenSource();
        _runTask = Task.Run(() => RunAsync(_cts.Token));
      }
    }

    public static void ReconnectNow()
    {
      Stop();
      Start();
    }

    public static void Stop()
    {
      CancellationTokenSource cts = null;
      Task runTask = null;
      ClientWebSocket socket = null;

      lock (Sync)
      {
        cts = _cts;
        _cts = null;
        runTask = _runTask;
        _runTask = null;
        socket = _socket;
        _socket = null;
      }

      try
      {
        cts?.Cancel();
      }
      catch
      {
        // ignore
      }

      try
      {
        if (socket != null && socket.State == WebSocketState.Open)
          socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", CancellationToken.None).GetAwaiter().GetResult();
      }
      catch
      {
        // ignore
      }
      finally
      {
        socket?.Dispose();
        cts?.Dispose();
      }

      try
      {
        runTask?.Wait(TimeSpan.FromSeconds(2));
      }
      catch
      {
        // ignore
      }
    }

    private static async Task RunAsync(CancellationToken ct)
    {
      while (!ct.IsCancellationRequested)
      {
        var connectUri = BuildBridgeUri();
        if (connectUri == null)
        {
          await Task.Delay(ReconnectDelay, ct).ConfigureAwait(false);
          continue;
        }

        ClientWebSocket ws = null;
        try
        {
          ws = new ClientWebSocket();
          ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
          await ws.ConnectAsync(connectUri, ct).ConfigureAwait(false);
          lock (Sync)
            _socket = ws;

          Log.Information("Revit bridge connected url={Url} workstationId={WorkstationId}", connectUri, _workstationId);
          await SendJsonAsync(new JObject
          {
            ["type"] = "hello",
            ["workstation_id"] = _workstationId,
            ["agent_id"] = _agentId,
            ["plugin"] = "OpenProject.Revit"
          }, ct).ConfigureAwait(false);

          await ReceiveLoopAsync(ws, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
          break;
        }
        catch (Exception ex)
        {
          Log.Warning(ex, "Revit bridge connection failed.");
        }
        finally
        {
          lock (Sync)
          {
            if (ReferenceEquals(_socket, ws))
              _socket = null;
          }
          ws?.Dispose();
        }

        try
        {
          await Task.Delay(ReconnectDelay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
          break;
        }
      }
    }

    private static Uri BuildBridgeUri()
    {
      if (string.IsNullOrWhiteSpace(_bridgeUrl) || string.IsNullOrWhiteSpace(_workstationId))
        return null;

      try
      {
        var separator = _bridgeUrl.Contains("?") ? "&" : "?";
        var url = $"{_bridgeUrl}{separator}workstation_id={Uri.EscapeDataString(_workstationId)}";
        if (!string.IsNullOrWhiteSpace(_bridgeToken))
          url += $"&token={Uri.EscapeDataString(_bridgeToken)}";
        return new Uri(url, UriKind.Absolute);
      }
      catch (Exception ex)
      {
        Log.Warning(ex, "Invalid Revit bridge URL: {Url}", _bridgeUrl);
        return null;
      }
    }

    private static async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
      while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
      {
        var payload = await ReceiveTextAsync(ws, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(payload))
          break;

        try
        {
          await HandleIncomingAsync(payload, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
          Log.Warning(ex, "Failed to process Revit bridge payload.");
        }
      }
    }

    private static async Task<string> ReceiveTextAsync(ClientWebSocket ws, CancellationToken ct)
    {
      var buffer = new byte[8192];
      var message = new StringBuilder();
      while (true)
      {
        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
        if (result.MessageType == WebSocketMessageType.Close)
          return null;

        if (result.MessageType != WebSocketMessageType.Text)
          continue;

        message.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        if (result.EndOfMessage)
          return message.ToString();
      }
    }

    private static async Task HandleIncomingAsync(string payload, CancellationToken ct)
    {
      var json = JObject.Parse(payload);
      var type = json["type"]?.ToString() ?? string.Empty;
      if (string.Equals(type, "ping", StringComparison.OrdinalIgnoreCase))
      {
        await SendJsonAsync(new JObject { ["type"] = "pong" }, ct).ConfigureAwait(false);
        return;
      }

      if (!string.Equals(type, "command", StringComparison.OrdinalIgnoreCase))
        return;

      var commandId = json["command_id"]?.ToString();
      var method = json["method"]?.ToString();
      var @params = json["params"] as JObject ?? new JObject();
      if (string.IsNullOrWhiteSpace(commandId) || string.IsNullOrWhiteSpace(method))
        return;

      var request = new BridgeCommandRequest(commandId, method, @params);
      Handler.Enqueue(request);
      _externalEvent?.Raise();

      BridgeCommandResponse response;
      var completed = await Task.WhenAny(request.Completion.Task, Task.Delay(CommandTimeout, ct)).ConfigureAwait(false);
      if (completed == request.Completion.Task)
      {
        response = await request.Completion.Task.ConfigureAwait(false);
      }
      else
      {
        response = BridgeCommandResponse.Fail("revit_command_timeout");
      }

      var reply = new JObject
      {
        ["type"] = "response",
        ["command_id"] = commandId,
        ["ok"] = response.Ok
      };
      if (response.Ok)
        reply["result"] = response.Result ?? new JObject();
      else
        reply["error"] = response.Error ?? "command_failed";

      await SendJsonAsync(reply, ct).ConfigureAwait(false);
    }

    private static async Task SendJsonAsync(JObject payload, CancellationToken ct)
    {
      var socket = _socket;
      if (socket == null || socket.State != WebSocketState.Open)
        return;

      var bytes = Encoding.UTF8.GetBytes(payload.ToString());
      await SendLock.WaitAsync(ct).ConfigureAwait(false);
      try
      {
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
      }
      finally
      {
        SendLock.Release();
      }
    }

    private sealed class BridgeExternalEventHandler : IExternalEventHandler
    {
      private readonly ConcurrentQueue<BridgeCommandRequest> _queue = new();

      public void Enqueue(BridgeCommandRequest request)
      {
        _queue.Enqueue(request);
      }

      public void Execute(UIApplication app)
      {
        while (_queue.TryDequeue(out var request))
        {
          try
          {
            var result = ExecuteCommand(app, request.Method, request.Params);
            request.Completion.TrySetResult(BridgeCommandResponse.Success(result));
          }
          catch (Exception ex)
          {
            Log.Error(ex, "Revit bridge command failed method={Method}", request.Method);
            request.Completion.TrySetResult(BridgeCommandResponse.Fail(ex.Message));
          }
        }
      }

      public string GetName()
      {
        return nameof(BridgeExternalEventHandler);
      }

      private static JToken ExecuteCommand(UIApplication app, string method, JObject @params)
      {
        var normalizedMethod = (method ?? string.Empty).Trim();
        switch (method)
        {
          case "Revit_SayHello":
          case "say_hello":
            return new JObject
            {
              ["message"] = "Hello from OpenProject.Revit bridge",
              ["revit_version"] = app?.Application?.VersionNumber ?? string.Empty,
              ["user"] = Environment.UserName
            };

          case "Revit_GetCurrentViewInfo":
          case "get_current_view_info":
          {
            var uidoc = app?.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var view = uidoc.ActiveView ?? throw new InvalidOperationException("No active Revit view.");
            return new JObject
            {
              ["id"] = GetElementIdValue(view.Id),
              ["name"] = view.Name ?? string.Empty,
              ["view_type"] = view.ViewType.ToString(),
              ["scale"] = view.Scale,
              ["is_template"] = view.IsTemplate
            };
          }

          case "Revit_GetSelectedElements":
          case "get_selected_elements":
          {
            var uidoc = app?.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document;
            var list = new JArray();
            foreach (var id in uidoc.Selection.GetElementIds())
            {
              var element = doc.GetElement(id);
              if (element == null)
                continue;

              list.Add(new JObject
              {
                ["id"] = GetElementIdValue(id),
                ["name"] = element.Name ?? string.Empty,
                ["category"] = element.Category?.Name ?? string.Empty
              });
            }

            return new JObject
            {
              ["count"] = list.Count,
              ["elements"] = list
            };
          }

          case "Revit_GetCurrentViewElements":
          case "get_current_view_elements":
          {
            var uidoc = app?.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document;
            var limit = Math.Max(1, @params?["limit"]?.Value<int?>() ?? 200);
            var viewId = uidoc.ActiveView.Id;
            var collector = new FilteredElementCollector(doc, viewId)
              .WhereElementIsNotElementType()
              .Where(e => e != null)
              .Take(limit);

            var elements = new JArray(
              collector.Select(e => new JObject
              {
                ["id"] = GetElementIdValue(e.Id),
                ["name"] = e.Name ?? string.Empty,
                ["category"] = e.Category?.Name ?? string.Empty
              }));

            return new JObject
            {
              ["count"] = elements.Count,
              ["elements"] = elements
            };
          }

          case "Revit_GetAvailableFamilyTypes":
          case "get_available_family_types":
          {
            var uidoc = app?.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document;
            var limit = Math.Max(1, @params?["limit"]?.Value<int?>() ?? 500);
            var categoryFilter = (@params?["category"]?.ToString() ?? @params?["category_name"]?.ToString() ?? string.Empty).Trim();

            var symbols = new FilteredElementCollector(doc)
              .OfClass(typeof(FamilySymbol))
              .Cast<FamilySymbol>()
              .Where(s => s != null);

            if (!string.IsNullOrWhiteSpace(categoryFilter))
            {
              symbols = symbols.Where(s =>
                string.Equals(s.Category?.Name ?? string.Empty, categoryFilter, StringComparison.OrdinalIgnoreCase));
            }

            var array = new JArray(symbols
              .OrderBy(s => s.FamilyName ?? string.Empty)
              .ThenBy(s => s.Name ?? string.Empty)
              .Take(limit)
              .Select(s => new JObject
              {
                ["id"] = GetElementIdValue(s.Id),
                ["family_name"] = s.FamilyName ?? string.Empty,
                ["type_name"] = s.Name ?? string.Empty,
                ["category"] = s.Category?.Name ?? string.Empty
              }));

            return new JObject
            {
              ["count"] = array.Count,
              ["types"] = array
            };
          }

          case "Revit_AnalyzeModelStatistics":
          case "analyze_model_statistics":
          {
            var uidoc = app?.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document;
            var topCategories = Math.Max(1, @params?["top_categories"]?.Value<int?>() ?? 25);

            var modelElements = new FilteredElementCollector(doc)
              .WhereElementIsNotElementType()
              .ToElements();

            var grouped = modelElements
              .Where(e => e?.Category != null)
              .GroupBy(e => e.Category.Name ?? "<NoCategory>")
              .OrderByDescending(g => g.Count())
              .Take(topCategories);

            var categories = new JArray(grouped.Select(g => new JObject
            {
              ["category"] = g.Key,
              ["count"] = g.Count()
            }));

            var roomsCount = new FilteredElementCollector(doc)
              .OfCategory(BuiltInCategory.OST_Rooms)
              .WhereElementIsNotElementType()
              .GetElementCount();

            var levelsCount = new FilteredElementCollector(doc)
              .OfClass(typeof(Level))
              .GetElementCount();

            var viewsCount = new FilteredElementCollector(doc)
              .OfClass(typeof(View))
              .Cast<View>()
              .Count(v => v != null && !v.IsTemplate);

            return new JObject
            {
              ["document_title"] = doc.Title ?? string.Empty,
              ["total_elements"] = modelElements.Count,
              ["views_count"] = viewsCount,
              ["levels_count"] = levelsCount,
              ["rooms_count"] = roomsCount,
              ["top_categories"] = categories
            };
          }

          case "Revit_ExportRoomData":
          case "export_room_data":
          {
            var uidoc = app?.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document;
            var limit = Math.Max(1, @params?["limit"]?.Value<int?>() ?? 200);

            var rooms = new FilteredElementCollector(doc)
              .OfCategory(BuiltInCategory.OST_Rooms)
              .WhereElementIsNotElementType()
              .OfType<Room>()
              .Take(limit);

            var data = new JArray(rooms.Select(room =>
            {
              var level = doc.GetElement(room.LevelId) as Level;
              var areaSqFt = room.Area;
              return new JObject
              {
                ["id"] = GetElementIdValue(room.Id),
                ["number"] = room.Number ?? string.Empty,
                ["name"] = room.Name ?? string.Empty,
                ["level"] = level?.Name ?? string.Empty,
                ["area_sqft"] = areaSqFt,
                ["area_sqm"] = areaSqFt * 0.09290304d
              };
            }));

            return new JObject
            {
              ["count"] = data.Count,
              ["rooms"] = data
            };
          }

          case "Revit_GetMaterialQuantities":
          case "get_material_quantities":
          {
            var uidoc = app?.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document;
            var maxElements = Math.Max(1, @params?["max_elements"]?.Value<int?>() ?? 1000);
            var topMaterials = Math.Max(1, @params?["top_materials"]?.Value<int?>() ?? 100);

            var aggregates = new Dictionary<long, (double area, double volume, int references)>();
            var modelElements = new FilteredElementCollector(doc)
              .WhereElementIsNotElementType()
              .Take(maxElements);

            foreach (var element in modelElements)
            {
              if (element == null)
                continue;

              ICollection<ElementId> materialIds;
              try
              {
                materialIds = element.GetMaterialIds(false);
              }
              catch
              {
                continue;
              }

              foreach (var materialId in materialIds)
              {
                if (materialId == null || materialId == ElementId.InvalidElementId)
                  continue;

                double area = 0d;
                double volume = 0d;
                try
                {
                  area = element.GetMaterialArea(materialId, false);
                }
                catch
                {
                  // ignore per-element calculation issues
                }
                try
                {
                  volume = element.GetMaterialVolume(materialId);
                }
                catch
                {
                  // ignore per-element calculation issues
                }

                if (!aggregates.TryGetValue(GetElementIdValue(materialId), out var current))
                  current = (0d, 0d, 0);

                aggregates[GetElementIdValue(materialId)] = (
                  current.area + Math.Max(0d, area),
                  current.volume + Math.Max(0d, volume),
                  current.references + 1
                );
              }
            }

            var materials = new JArray(
              aggregates
                .OrderByDescending(kv => kv.Value.volume)
                .ThenByDescending(kv => kv.Value.area)
                .Take(topMaterials)
                .Select(kv =>
                {
                  var material = doc.GetElement(CreateElementId(kv.Key)) as Material;
                  return new JObject
                  {
                    ["id"] = kv.Key,
                    ["name"] = material?.Name ?? string.Empty,
                    ["volume_cuft"] = kv.Value.volume,
                    ["volume_m3"] = kv.Value.volume * 0.028316846592d,
                    ["area_sqft"] = kv.Value.area,
                    ["area_sqm"] = kv.Value.area * 0.09290304d,
                    ["references"] = kv.Value.references
                  };
                })
            );

            return new JObject
            {
              ["count"] = materials.Count,
              ["materials"] = materials
            };
          }

          case "Revit_DeleteElement":
          case "delete_element":
          {
            var uidoc = app?.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document;
            var id = @params?["element_id"]?.Value<int?>()
                     ?? @params?["elementId"]?.Value<int?>()
                     ?? @params?["id"]?.Value<int?>()
                     ?? throw new ArgumentException("Missing element_id.");

            using var tx = new Transaction(doc, "Revit bridge delete element");
            if (tx.Start() != TransactionStatus.Started)
              throw new InvalidOperationException("Could not start transaction.");
            var deletedIds = doc.Delete(CreateElementId(id));
            tx.Commit();

            return new JObject
            {
              ["deleted_count"] = deletedIds?.Count ?? 0
            };
          }

          case "Revit_CreateLevel":
          case "create_level":
          {
            var uidoc = app?.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document;
            var levelName = @params?["level_name"]?.ToString()
                            ?? @params?["name"]?.ToString()
                            ?? "New Level";
            var elevation = @params?["elevation"]?.Value<double?>() ?? 0d;
            var units = (@params?["units"]?.ToString() ?? "feet").Trim().ToLowerInvariant();
            var elevationFeet = units.StartsWith("m") ? elevation * 3.28084d : elevation;

            Level level;
            using (var tx = new Transaction(doc, "Revit bridge create level"))
            {
              if (tx.Start() != TransactionStatus.Started)
                throw new InvalidOperationException("Could not start transaction.");
              level = Level.Create(doc, elevationFeet);
              if (!string.IsNullOrWhiteSpace(levelName))
                level.Name = levelName;
              tx.Commit();
            }

            return new JObject
            {
              ["id"] = GetElementIdValue(level.Id),
              ["name"] = level.Name ?? levelName,
              ["elevation_feet"] = elevationFeet
            };
          }

          case "Revit_CreateGrid":
          case "create_grid":
            return CreateGrid(app, @params);

          case "Revit_TagRooms":
          case "tag_rooms":
            return TagRooms(app, @params);

          case "Revit_TagWalls":
          case "tag_walls":
            return TagWalls(app, @params);

          case "Revit_AiElementFilter":
          case "ai_element_filter":
            return AiElementFilter(app, @params);

          case "Revit_GetElementById":
          case "get_element_by_id":
            return GetElementById(app, @params);

          case "Revit_OperateElement":
          case "operate_element":
            return OperateElement(app, @params);

          case "Revit_CreateRoom":
          case "create_room":
            return CreateRoom(app, @params);

          case "Revit_CreateDimensions":
          case "create_dimensions":
            return CreateDimensions(app, @params);

          case "Revit_CreatePointBasedElement":
          case "create_point_based_element":
            return CreatePointBasedElement(app, @params);

          case "Revit_CreateLineBasedElement":
          case "create_line_based_element":
            return CreateLineBasedElement(app, @params);

          case "Revit_CreateSurfaceBasedElement":
          case "create_surface_based_element":
            return CreateSurfaceBasedElement(app, @params);

          case "Revit_ColorSplash":
          case "color_splash":
            return ColorSplash(app, @params);

          case "Revit_CreateStructuralFramingSystem":
          case "create_structural_framing_system":
            return CreateStructuralFramingSystem(app, @params);

          case "Revit_CreateWall":
          case "create_wall":
            return CreateWall(app, @params);

          case "Revit_CreateFloor":
          case "create_floor":
            return CreateFloor(app, @params);

          case "Revit_CreateRoof":
          case "create_roof":
            return CreateRoof(app, @params);

          case "Revit_GetRoofTypes":
          case "get_roof_types":
            return GetRoofTypes(app, @params);

          case "Revit_CreateStair":
          case "create_stair":
            return CreateStair(app, @params);

          case "Revit_GetStairRunsLandings":
          case "get_stair_runs_landings":
            return GetStairRunsLandings(app, @params);

          case "Revit_CreateStairDimensions":
          case "create_stair_dimensions":
            return CreateStairDimensions(app, @params);

          case "Revit_SendCodeToRevit":
          case "send_code_to_revit":
            return SendCodeToRevit(app, @params);

          default:
            throw new NotSupportedException($"Unsupported bridge method: {normalizedMethod}");
        }
      }

      private static JToken CreateGrid(UIApplication app, JObject @params)
      {
        var uidoc = app?.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
        var doc = uidoc.Document;
        var data = @params?["data"] as JArray ?? throw new ArgumentException("Missing data array for create_grid.");
        if (data.Count == 0)
          throw new ArgumentException("create_grid requires at least one entry.");

        var spacing = @params?["spacing"]?.Value<double?>() ?? 30d;
        spacing = Math.Max(1d, spacing);
        var halfLen = @params?["half_length"]?.Value<double?>() ?? 100d;
        halfLen = Math.Max(10d, halfLen);

        var created = new JArray();
        using var tx = new Transaction(doc, "Revit bridge create grid");
        if (tx.Start() != TransactionStatus.Started)
          throw new InvalidOperationException("Could not start transaction.");

        for (var i = 0; i < data.Count; i++)
        {
          var item = data[i] as JObject ?? new JObject();
          var name = item["name"]?.ToString() ?? $"IA-{i + 1}";
          var type = (item["type"]?.ToString() ?? "Vertical").Trim();
          var axis = type.StartsWith("h", StringComparison.OrdinalIgnoreCase) ? "horizontal" : "vertical";

          Line line;
          if (axis == "horizontal")
          {
            var y = i * spacing;
            line = Line.CreateBound(new XYZ(-halfLen, y, 0), new XYZ(halfLen, y, 0));
          }
          else
          {
            var x = i * spacing;
            line = Line.CreateBound(new XYZ(x, -halfLen, 0), new XYZ(x, halfLen, 0));
          }

          var grid = Grid.Create(doc, line);
          if (!string.IsNullOrWhiteSpace(name))
          {
            try
            {
              grid.Name = name;
            }
            catch
            {
              // duplicate names are common; keep Revit-assigned value
            }
          }

          created.Add(new JObject
          {
            ["id"] = GetElementIdValue(grid.Id),
            ["name"] = grid.Name ?? string.Empty,
            ["axis"] = axis
          });
        }

        tx.Commit();
        return new JObject
        {
          ["count"] = created.Count,
          ["grids"] = created
        };
      }

      private static JToken TagRooms(UIApplication app, JObject @params)
      {
        var uidoc = app?.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
        var doc = uidoc.Document;
        var view = uidoc.ActiveView ?? throw new InvalidOperationException("No active Revit view.");
        if (view.IsTemplate)
          throw new InvalidOperationException("Active view is a template.");

        var rooms = new FilteredElementCollector(doc, view.Id)
          .OfCategory(BuiltInCategory.OST_Rooms)
          .WhereElementIsNotElementType()
          .OfType<Room>()
          .ToList();

        var tagged = 0;
        var skipped = 0;
        using var tx = new Transaction(doc, "Revit bridge tag rooms");
        if (tx.Start() != TransactionStatus.Started)
          throw new InvalidOperationException("Could not start transaction.");
        foreach (var room in rooms)
        {
          try
          {
            if (room.Location is not LocationPoint lp)
            {
              skipped++;
              continue;
            }
            doc.Create.NewRoomTag(new LinkElementId(room.Id), new UV(lp.Point.X, lp.Point.Y), view.Id);
            tagged++;
          }
          catch
          {
            skipped++;
          }
        }
        tx.Commit();

        return new JObject
        {
          ["rooms_in_view"] = rooms.Count,
          ["tagged_count"] = tagged,
          ["skipped_count"] = skipped
        };
      }

      private static JToken TagWalls(UIApplication app, JObject @params)
      {
        var uidoc = app?.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
        var doc = uidoc.Document;
        var view = uidoc.ActiveView ?? throw new InvalidOperationException("No active Revit view.");
        if (view.IsTemplate)
          throw new InvalidOperationException("Active view is a template.");
        if (view.ViewType == ViewType.ThreeD)
          throw new InvalidOperationException("Wall tagging is not supported in 3D views.");

        var walls = new FilteredElementCollector(doc, view.Id)
          .OfClass(typeof(Wall))
          .Cast<Wall>()
          .ToList();

        var tagged = 0;
        var skipped = 0;
        var skippedByReason = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void MarkSkip(string reason)
        {
          skipped++;
          skippedByReason[reason] = skippedByReason.TryGetValue(reason, out var current) ? current + 1 : 1;
        }
        using var tx = new Transaction(doc, "Revit bridge tag walls");
        if (tx.Start() != TransactionStatus.Started)
          throw new InvalidOperationException("Could not start transaction.");
        foreach (var wall in walls)
        {
          try
          {
            var reference = new Reference(wall);
            XYZ point;
            if (wall.Location is LocationCurve lc)
              point = lc.Curve.Evaluate(0.5, true);
            else
              point = wall.get_BoundingBox(view)?.Min ?? XYZ.Zero;

            var created = TryCreateWallTag(doc, view, reference, point);
            if (created)
              tagged++;
            else
              MarkSkip("tag_create_failed");
          }
          catch (Autodesk.Revit.Exceptions.ArgumentException)
          {
            MarkSkip("invalid_reference_or_view");
          }
          catch (Autodesk.Revit.Exceptions.InvalidOperationException)
          {
            MarkSkip("tag_not_allowed_in_view");
          }
          catch
          {
            MarkSkip("unknown");
          }
        }
        tx.Commit();

        return new JObject
        {
          ["walls_in_view"] = walls.Count,
          ["tagged_count"] = tagged,
          ["skipped_count"] = skipped,
          ["skipped_reasons"] = new JObject(skippedByReason.Select(kv => new JProperty(kv.Key, kv.Value)))
        };
      }

      private static bool TryCreateWallTag(Document doc, View view, Reference reference, XYZ point)
      {
        // Primary orientation.
        try
        {
          _ = IndependentTag.Create(
            doc,
            view.Id,
            reference,
            false,
            TagMode.TM_ADDBY_CATEGORY,
            TagOrientation.Horizontal,
            point
          );
          return true;
        }
        catch
        {
          // fall through
        }

        // Fallback orientation.
        try
        {
          _ = IndependentTag.Create(
            doc,
            view.Id,
            reference,
            false,
            TagMode.TM_ADDBY_CATEGORY,
            TagOrientation.AnyModelDirection,
            point
          );
          return true;
        }
        catch
        {
          return false;
        }
      }

      private static JToken AiElementFilter(UIApplication app, JObject @params)
      {
        var uidoc = app?.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
        var doc = uidoc.Document;
        var view = uidoc.ActiveView ?? throw new InvalidOperationException("No active Revit view.");

        var data = @params?["data"] as JObject ?? @params ?? new JObject();
        var category = (data["category"]?.ToString() ?? string.Empty).Trim();
        var query = (data["query"]?.ToString() ?? data["name_contains"]?.ToString() ?? string.Empty).Trim();
        var limit = Math.Max(1, data["limit"]?.Value<int?>() ?? 200);

        static string NormalizeCategory(string value)
        {
          var s = (value ?? string.Empty).Trim().ToLowerInvariant();
          return s
            .Replace("á", "a")
            .Replace("é", "e")
            .Replace("í", "i")
            .Replace("ó", "o")
            .Replace("ú", "u")
            .Replace("ü", "u")
            .Replace("ñ", "n");
        }

        static bool InAliasSet(string value, params string[] aliases)
        {
          var v = NormalizeCategory(value);
          return aliases.Any(a => v == a);
        }

        var categoryIds = new HashSet<long>();
        if (!string.IsNullOrWhiteSpace(category))
        {
          if (InAliasSet(category, "walls", "wall", "muros", "muro", "paredes", "pared"))
            categoryIds.Add((long)BuiltInCategory.OST_Walls);
          if (InAliasSet(category, "floors", "floor", "pisos", "piso", "losas", "losa", "slabs", "slab"))
            categoryIds.Add((long)BuiltInCategory.OST_Floors);
          if (InAliasSet(category, "roofs", "roof", "cubiertas", "cubierta", "techos", "techo", "tejados", "tejado"))
            categoryIds.Add((long)BuiltInCategory.OST_Roofs);
        }

        var elements = new FilteredElementCollector(doc, view.Id)
          .WhereElementIsNotElementType()
          .Where(e => e != null);

        if (!string.IsNullOrWhiteSpace(category))
        {
          elements = elements.Where(e =>
          {
            var catName = e.Category?.Name ?? string.Empty;
            var catId = GetElementIdValue(e.Category?.Id);
            var byId = categoryIds.Contains(catId);
            var byNameExact = string.Equals(catName, category, StringComparison.OrdinalIgnoreCase);
            var byNameNormalized = string.Equals(NormalizeCategory(catName), NormalizeCategory(category), StringComparison.OrdinalIgnoreCase);
            return byId || byNameExact || byNameNormalized;
          });
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
          elements = elements.Where(e =>
            (e.Name ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        var list = new JArray(elements.Take(limit).Select(e => new JObject
        {
          ["id"] = GetElementIdValue(e.Id),
          ["name"] = e.Name ?? string.Empty,
          ["category"] = e.Category?.Name ?? string.Empty
        }));

        return new JObject
        {
          ["count"] = list.Count,
          ["elements"] = list
        };
      }

      private static JToken OperateElement(UIApplication app, JObject @params)
      {
        var uidoc = app?.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
        var doc = uidoc.Document;
        var data = @params?["data"] as JObject ?? @params ?? new JObject();
        var operation = (data["operation"]?.ToString() ?? data["action"]?.ToString() ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(operation))
          throw new ArgumentException("Missing operation/action.");

        var ids = new List<ElementId>();
        if (data["element_ids"] is JArray idArray)
        {
          foreach (var token in idArray)
          {
            if (token?.Value<long?>() is long v)
              ids.Add(CreateElementId(v));
          }
        }
        else if (data["element_id"]?.Value<long?>() is long oneId)
        {
          ids.Add(CreateElementId(oneId));
        }

        if (ids.Count == 0)
          throw new ArgumentException("Missing element_ids/element_id.");

        using var tx = new Transaction(doc, "Revit bridge operate element");
        if (tx.Start() != TransactionStatus.Started)
          throw new InvalidOperationException("Could not start transaction.");

        switch (operation)
        {
          case "delete":
          {
            var deleted = doc.Delete(ids);
            tx.Commit();
            return new JObject
            {
              ["operation"] = operation,
              ["deleted_count"] = deleted?.Count ?? 0
            };
          }
          case "pin":
          case "unpin":
          {
            var pinValue = operation == "pin";
            var changed = 0;
            foreach (var id in ids)
            {
              var element = doc.GetElement(id);
              if (element == null)
                continue;
              element.Pinned = pinValue;
              changed++;
            }
            tx.Commit();
            return new JObject
            {
              ["operation"] = operation,
              ["updated_count"] = changed
            };
          }
          default:
            tx.RollBack();
            throw new NotSupportedException($"Unsupported operation: {operation}");
        }
      }

      private static JToken GetElementById(UIApplication app, JObject @params)
      {
        var uidoc = app?.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
        var doc = uidoc.Document;
        var data = @params?["data"] as JObject ?? @params ?? new JObject();

        var requestedId = data["element_id"]?.Value<long?>()
                         ?? data["id"]?.Value<long?>()
                         ?? data["elementId"]?.Value<long?>();
        if (!requestedId.HasValue)
          throw new ArgumentException("Missing element_id.");

        var element = doc.GetElement(CreateElementId(requestedId.Value));
        if (element == null)
        {
          return new JObject
          {
            ["found"] = false,
            ["element_id"] = requestedId.Value
          };
        }

        var locationType = "None";
        if (element.Location is LocationPoint)
          locationType = "Point";
        else if (element.Location is LocationCurve)
          locationType = "Curve";

        return new JObject
        {
          ["found"] = true,
          ["element"] = new JObject
          {
            ["id"] = GetElementIdValue(element.Id),
            ["name"] = element.Name ?? string.Empty,
            ["category"] = element.Category?.Name ?? string.Empty,
            ["class_name"] = element.GetType().Name,
            ["is_pinned"] = element.Pinned,
            ["location_type"] = locationType
          }
        };
      }

      private static JToken CreateRoom(UIApplication app, JObject @params)
      {
        var uidoc = app?.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
        var doc = uidoc.Document;
        var data = @params?["data"] as JArray ?? throw new ArgumentException("Missing data array for create_room.");
        if (data.Count == 0)
          throw new ArgumentException("create_room requires at least one payload item.");

        var item = data[0] as JObject ?? new JObject();
        var levelName = item["level"]?.ToString() ?? item["level_name"]?.ToString();
        var levelId = item["level_id"]?.Value<long?>();
        var levelRequested = levelId.HasValue || !string.IsNullOrWhiteSpace(levelName);

        var level = levelId.HasValue
          ? doc.GetElement(CreateElementId(levelId.Value)) as Level
          : null;
        if (level == null && !string.IsNullOrWhiteSpace(levelName))
        {
          level = new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .FirstOrDefault(l => string.Equals(l.Name ?? string.Empty, levelName, StringComparison.OrdinalIgnoreCase));
        }
        level ??= new FilteredElementCollector(doc)
          .OfClass(typeof(Level))
          .Cast<Level>()
          .OrderBy(l => l.Elevation)
          .FirstOrDefault();
        if (levelRequested && level == null)
          throw new InvalidOperationException("Requested level was not found.");
        if (levelRequested && !string.IsNullOrWhiteSpace(levelName) &&
            !string.Equals(level?.Name ?? string.Empty, levelName, StringComparison.OrdinalIgnoreCase))
          throw new InvalidOperationException($"Requested level '{levelName}' was not found.");
        if (level == null)
          throw new InvalidOperationException("No level available to create room.");

        var units = (item["units"]?.ToString() ?? "feet").Trim().ToLowerInvariant();
        var factor = units.StartsWith("m") ? 3.28084d : 1d;
        var x = (item["x"]?.Value<double?>() ?? 0d) * factor;
        var y = (item["y"]?.Value<double?>() ?? 0d) * factor;
        var roomName = item["name"]?.ToString();
        var roomNumber = item["number"]?.ToString();

        Room room;
        using (var tx = new Transaction(doc, "Revit bridge create room"))
        {
          if (tx.Start() != TransactionStatus.Started)
            throw new InvalidOperationException("Could not start transaction.");
          room = doc.Create.NewRoom(level, new UV(x, y));
          if (room == null)
            throw new InvalidOperationException("Room creation failed (location may be outside a valid room-bounding region).");
          if (!string.IsNullOrWhiteSpace(roomName))
            room.Name = roomName;
          if (!string.IsNullOrWhiteSpace(roomNumber))
            room.Number = roomNumber;
          tx.Commit();
        }

        return new JObject
        {
          ["id"] = GetElementIdValue(room.Id),
          ["name"] = room.Name ?? string.Empty,
          ["number"] = room.Number ?? string.Empty,
          ["level"] = level.Name ?? string.Empty
        };
      }

      private static JToken CreateDimensions(UIApplication app, JObject @params)
      {
        var uidoc = app?.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
        var doc = uidoc.Document;
        var view = uidoc.ActiveView ?? throw new InvalidOperationException("No active Revit view.");
        if (view.ViewType == ViewType.ThreeD)
          throw new InvalidOperationException("Dimension creation is not supported in 3D views.");

        var selected = uidoc.Selection.GetElementIds().ToList();
        var usedFallback = false;
        if (selected.Count < 2)
        {
          // Fallback: pick two linear walls visible in current view.
          selected = new FilteredElementCollector(doc, view.Id)
            .OfClass(typeof(Wall))
            .Cast<Element>()
            .Where(e => e?.Location is LocationCurve)
            .Take(2)
            .Select(e => e.Id)
            .ToList();
          usedFallback = true;
        }
        if (selected.Count < 2)
          throw new InvalidOperationException("Select at least two linear elements before creating dimensions.");

        var references = new List<(Reference reference, XYZ midpoint)>();
        foreach (var id in selected)
        {
          var element = doc.GetElement(id);
          if (element?.Location is not LocationCurve lc)
            continue;

          var curve = lc.Curve;
          if (curve == null)
            continue;
          references.Add((new Reference(element), curve.Evaluate(0.5, true)));
        }

        if (references.Count < 2)
          throw new InvalidOperationException("Selected elements do not expose usable linear references.");

        var first = references[0];
        var second = references[1];
        var line = Line.CreateBound(first.midpoint, second.midpoint);
        var refArray = new ReferenceArray();
        refArray.Append(first.reference);
        refArray.Append(second.reference);

        Dimension dim;
        using (var tx = new Transaction(doc, "Revit bridge create dimensions"))
        {
          if (tx.Start() != TransactionStatus.Started)
            throw new InvalidOperationException("Could not start transaction.");
          dim = doc.Create.NewDimension(view, line, refArray);
          tx.Commit();
        }

        return new JObject
        {
          ["id"] = (dim?.Id == null ? -1 : GetElementIdValue(dim.Id)),
          ["referenced_elements"] = 2,
          ["used_fallback"] = usedFallback
        };
      }

      private static JToken CreatePointBasedElement(UIApplication app, JObject @params)
      {
        var uidoc = app?.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
        var doc = uidoc.Document;
        var data = @params?["data"] as JArray ?? throw new ArgumentException("Missing data array for create_point_based_element.");
        if (data.Count == 0)
          throw new ArgumentException("create_point_based_element requires at least one payload item.");

        var created = new JArray();
        var skippedByReason = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var levelCache = new Dictionary<string, Level>(StringComparer.OrdinalIgnoreCase);

        using var tx = new Transaction(doc, "Revit bridge create point-based elements");
        if (tx.Start() != TransactionStatus.Started)
          throw new InvalidOperationException("Could not start transaction.");

        foreach (var token in data)
        {
          var item = token as JObject ?? new JObject();
          try
          {
            var symbol = ResolveFamilySymbol(doc, item, BuiltInCategory.OST_GenericModel);
            if (symbol == null)
            {
              MarkSkip(skippedByReason, "family_symbol_not_found");
              continue;
            }

            if (!symbol.IsActive)
              symbol.Activate();

            var level = ResolveLevelForPayload(doc, uidoc.ActiveView, item, levelCache);
            var point = ParsePoint(item);
            var instance = doc.Create.NewFamilyInstance(point, symbol, level, StructuralType.NonStructural);
            created.Add(new JObject
            {
              ["id"] = GetElementIdValue(instance.Id),
              ["family_name"] = symbol.FamilyName ?? string.Empty,
              ["type_name"] = symbol.Name ?? string.Empty
            });
          }
          catch (Autodesk.Revit.Exceptions.ArgumentException)
          {
            MarkSkip(skippedByReason, "invalid_payload_or_symbol");
          }
          catch (Autodesk.Revit.Exceptions.InvalidOperationException)
          {
            MarkSkip(skippedByReason, "placement_not_allowed");
          }
        }

        tx.Commit();
        return new JObject
        {
          ["created_count"] = created.Count,
          ["skipped_count"] = skippedByReason.Values.Sum(),
          ["skipped_reasons"] = new JObject(skippedByReason.Select(kv => new JProperty(kv.Key, kv.Value))),
          ["elements"] = created
        };
      }

      private static JToken CreateLineBasedElement(UIApplication app, JObject @params)
      {
        var uidoc = app?.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
        var doc = uidoc.Document;
        var view = uidoc.ActiveView ?? throw new InvalidOperationException("No active Revit view.");
        var data = @params?["data"] as JArray ?? throw new ArgumentException("Missing data array for create_line_based_element.");
        if (data.Count == 0)
          throw new ArgumentException("create_line_based_element requires at least one payload item.");

        var created = new JArray();
        var skippedByReason = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        using var tx = new Transaction(doc, "Revit bridge create line-based elements");
        if (tx.Start() != TransactionStatus.Started)
          throw new InvalidOperationException("Could not start transaction.");

        var sketchPlane = view.SketchPlane ?? EnsureSketchPlane(doc, view);

        foreach (var token in data)
        {
          var item = token as JObject ?? new JObject();
          try
          {
            var start = ParsePointToken(item["start"], "start", item);
            var end = ParsePointToken(item["end"], "end", item);
            if (start.DistanceTo(end) < 1e-6)
            {
              MarkSkip(skippedByReason, "zero_length_line");
              continue;
            }

            var line = Line.CreateBound(start, end);
            var curveElement = doc.Create.NewModelCurve(line, sketchPlane);
            created.Add(new JObject
            {
              ["id"] = GetElementIdValue(curveElement.Id),
              ["type"] = "ModelCurve"
            });
          }
          catch (Autodesk.Revit.Exceptions.ArgumentException)
          {
            MarkSkip(skippedByReason, "invalid_line_geometry");
          }
          catch (Autodesk.Revit.Exceptions.InvalidOperationException)
          {
            MarkSkip(skippedByReason, "curve_creation_failed");
          }
        }

        tx.Commit();
        return new JObject
        {
          ["created_count"] = created.Count,
          ["skipped_count"] = skippedByReason.Values.Sum(),
          ["skipped_reasons"] = new JObject(skippedByReason.Select(kv => new JProperty(kv.Key, kv.Value))),
          ["elements"] = created
        };
      }

      private static JToken CreateSurfaceBasedElement(UIApplication app, JObject @params)
      {
        var uidoc = app?.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
        var doc = uidoc.Document;
        var data = @params?["data"] as JArray ?? throw new ArgumentException("Missing data array for create_surface_based_element.");
        if (data.Count == 0)
          throw new ArgumentException("create_surface_based_element requires at least one payload item.");

        var created = new JArray();
        var skippedByReason = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        using var tx = new Transaction(doc, "Revit bridge create surface-based elements");
        if (tx.Start() != TransactionStatus.Started)
          throw new InvalidOperationException("Could not start transaction.");

        foreach (var token in data)
        {
          var item = token as JObject ?? new JObject();
          try
          {
            var hostId = item["host_element_id"]?.Value<long?>();
            if (!hostId.HasValue)
            {
              MarkSkip(skippedByReason, "host_element_id_required");
              continue;
            }

            var host = doc.GetElement(CreateElementId(hostId.Value));
            if (host == null)
            {
              MarkSkip(skippedByReason, "host_not_found");
              continue;
            }

            var symbol = ResolveFamilySymbol(doc, item, BuiltInCategory.OST_GenericModel);
            if (symbol == null)
            {
              MarkSkip(skippedByReason, "family_symbol_not_found");
              continue;
            }
            if (!symbol.IsActive)
              symbol.Activate();

            if (!TryGetHostFaceReferenceAndPoint(host, out var faceRef, out var placementPoint))
            {
              MarkSkip(skippedByReason, "host_face_not_found");
              continue;
            }

            var instance = doc.Create.NewFamilyInstance(
              faceRef,
              placementPoint,
              XYZ.BasisZ,
              symbol
            );

            created.Add(new JObject
            {
              ["id"] = GetElementIdValue(instance.Id),
              ["host_id"] = GetElementIdValue(host.Id),
              ["family_name"] = symbol.FamilyName ?? string.Empty,
              ["type_name"] = symbol.Name ?? string.Empty
            });
          }
          catch (Autodesk.Revit.Exceptions.ArgumentException)
          {
            MarkSkip(skippedByReason, "invalid_surface_placement");
          }
          catch (Autodesk.Revit.Exceptions.InvalidOperationException)
          {
            MarkSkip(skippedByReason, "surface_placement_failed");
          }
        }

        tx.Commit();
        return new JObject
        {
          ["created_count"] = created.Count,
          ["skipped_count"] = skippedByReason.Values.Sum(),
          ["skipped_reasons"] = new JObject(skippedByReason.Select(kv => new JProperty(kv.Key, kv.Value))),
          ["elements"] = created
        };
      }

      private static JToken ColorSplash(UIApplication app, JObject @params)
      {
        var uidoc = app?.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
        var doc = uidoc.Document;
        var view = uidoc.ActiveView ?? throw new InvalidOperationException("No active Revit view.");

        var data = @params?["data"] as JObject ?? @params ?? new JObject();
        var category = (data["category"]?.ToString() ?? string.Empty).Trim();
        var query = (data["query"]?.ToString() ?? data["name_contains"]?.ToString() ?? string.Empty).Trim();
        var limit = Math.Max(1, data["limit"]?.Value<int?>() ?? 1000);

        var color = ParseColor(data);
        var overrides = new OverrideGraphicSettings()
          .SetProjectionLineColor(color)
          .SetSurfaceForegroundPatternColor(color)
          .SetSurfaceTransparency(Math.Max(0, Math.Min(90, data["transparency"]?.Value<int?>() ?? 0)));

        var elements = new FilteredElementCollector(doc, view.Id)
          .WhereElementIsNotElementType()
          .Where(e => e != null);

        if (!string.IsNullOrWhiteSpace(category))
        {
          elements = elements.Where(e =>
            string.Equals(e.Category?.Name ?? string.Empty, category, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(query))
        {
          elements = elements.Where(e =>
            (e.Name ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        var selected = elements.Take(limit).ToList();
        var updated = 0;

        using var tx = new Transaction(doc, "Revit bridge color splash");
        if (tx.Start() != TransactionStatus.Started)
          throw new InvalidOperationException("Could not start transaction.");

        foreach (var element in selected)
        {
          try
          {
            view.SetElementOverrides(element.Id, overrides);
            updated++;
          }
          catch
          {
            // keep going
          }
        }
        tx.Commit();

        return new JObject
        {
          ["colored_count"] = updated,
          ["candidate_count"] = selected.Count,
          ["color_rgb"] = new JObject
          {
            ["r"] = color.Red,
            ["g"] = color.Green,
            ["b"] = color.Blue
          }
        };
      }

      private static JToken CreateStructuralFramingSystem(UIApplication app, JObject @params)
      {
        var uidoc = app?.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
        var doc = uidoc.Document;
        var data = @params?["data"] as JArray ?? throw new ArgumentException("Missing data array for create_structural_framing_system.");
        if (data.Count == 0)
          throw new ArgumentException("create_structural_framing_system requires at least one payload item.");

        var created = new JArray();
        var skippedByReason = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var levelCache = new Dictionary<string, Level>(StringComparer.OrdinalIgnoreCase);

        using var tx = new Transaction(doc, "Revit bridge create structural framing");
        if (tx.Start() != TransactionStatus.Started)
          throw new InvalidOperationException("Could not start transaction.");

        foreach (var token in data)
        {
          var item = token as JObject ?? new JObject();
          try
          {
            var symbol = ResolveFamilySymbol(doc, item, BuiltInCategory.OST_StructuralFraming)
                         ?? ResolveFirstSymbolByCategory(doc, BuiltInCategory.OST_StructuralFraming);
            if (symbol == null)
            {
              MarkSkip(skippedByReason, "framing_symbol_not_found");
              continue;
            }
            if (!symbol.IsActive)
              symbol.Activate();

            var start = ParsePointToken(item["start"], "start", item);
            var end = ParsePointToken(item["end"], "end", item);
            if (start.DistanceTo(end) < 1e-6)
            {
              MarkSkip(skippedByReason, "zero_length_beam");
              continue;
            }

            var level = ResolveLevelForPayload(doc, uidoc.ActiveView, item, levelCache);
            var curve = Line.CreateBound(start, end);
            var beam = doc.Create.NewFamilyInstance(curve, symbol, level, StructuralType.Beam);
            created.Add(new JObject
            {
              ["id"] = GetElementIdValue(beam.Id),
              ["family_name"] = symbol.FamilyName ?? string.Empty,
              ["type_name"] = symbol.Name ?? string.Empty
            });
          }
          catch (Autodesk.Revit.Exceptions.ArgumentException)
          {
            MarkSkip(skippedByReason, "invalid_beam_payload");
          }
          catch (Autodesk.Revit.Exceptions.InvalidOperationException)
          {
            MarkSkip(skippedByReason, "beam_creation_failed");
          }
        }

        tx.Commit();
        return new JObject
        {
          ["created_count"] = created.Count,
          ["skipped_count"] = skippedByReason.Values.Sum(),
          ["skipped_reasons"] = new JObject(skippedByReason.Select(kv => new JProperty(kv.Key, kv.Value))),
          ["elements"] = created
        };
      }

      private static JToken CreateWall(UIApplication app, JObject @params)
      {
        var uidoc = app?.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
        var doc = uidoc.Document;
        var payload = @params ?? new JObject();
        var wallItems = payload["data"] as JArray;
        var singleItem = payload["data"] as JObject ?? payload;
        if (wallItems == null || wallItems.Count == 0)
          wallItems = new JArray(singleItem);

        if (wallItems.Count == 0)
          throw new ArgumentException("create_wall requires one wall payload or a non-empty data array.");

        var defaultLevel = ResolveLevelByNameOrId(
          doc,
          payload["level_id"]?.Value<long?>(),
          payload["level"]?.ToString() ?? payload["level_name"]?.ToString());
        defaultLevel ??= new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).FirstOrDefault()
                        ?? throw new InvalidOperationException("No level available.");

        var allWallTypes = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().ToList();
        if (allWallTypes.Count == 0)
          throw new InvalidOperationException("No wall type available.");

        WallType ResolveWallType(string requested)
        {
          var clean = (requested ?? string.Empty).Trim();
          if (!string.IsNullOrWhiteSpace(clean))
          {
            var exact = allWallTypes.FirstOrDefault(wt =>
              string.Equals(wt.Name ?? string.Empty, clean, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
              return exact;

            var contains = allWallTypes.FirstOrDefault(wt =>
              (wt.Name ?? string.Empty).IndexOf(clean, StringComparison.OrdinalIgnoreCase) >= 0);
            if (contains != null)
              return contains;
          }

          return allWallTypes.First();
        }

        var created = new JArray();
        var skippedByReason = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        using (var tx = new Transaction(doc, "Revit bridge create wall"))
        {
          if (tx.Start() != TransactionStatus.Started)
            throw new InvalidOperationException("Could not start transaction.");
          var wallFailureOptions = tx.GetFailureHandlingOptions();
          wallFailureOptions.SetFailuresPreprocessor(new AcceptAllFailuresPreprocessor());
          tx.SetFailureHandlingOptions(wallFailureOptions);

          foreach (var itemToken in wallItems)
          {
            if (itemToken is not JObject item)
            {
              MarkSkip(skippedByReason, "invalid_wall_payload");
              continue;
            }

            try
            {
              var level = ResolveLevelByNameOrId(
                doc,
                item["level_id"]?.Value<long?>() ?? payload["level_id"]?.Value<long?>(),
                item["level"]?.ToString() ?? item["level_name"]?.ToString()
                ?? payload["level"]?.ToString() ?? payload["level_name"]?.ToString())
                ?? defaultLevel;

              var start = ParsePointToken(item["start"] ?? item["start_point"], "start", item);
              var end = ParsePointToken(item["end"] ?? item["end_point"], "end", item);
              if (start.DistanceTo(end) < 1e-6)
              {
                MarkSkip(skippedByReason, "zero_length_wall");
                continue;
              }

              var units = item["units"]?.ToString() ?? payload["units"]?.ToString() ?? "ft";
              var heightFeet = ConvertLengthToFeet(
                item["height"]?.Value<double?>() ?? payload["height"]?.Value<double?>() ?? 10d,
                units);
              var baseOffsetFeet = ConvertLengthToFeet(
                item["base_offset"]?.Value<double?>() ?? payload["base_offset"]?.Value<double?>() ?? 0d,
                units);
              var structural = item["structural"]?.Value<bool?>() ?? payload["structural"]?.Value<bool?>() ?? false;
              var flip = item["flip"]?.Value<bool?>() ?? payload["flip"]?.Value<bool?>() ?? false;

              var wallTypeName = (item["wall_type"]?.ToString()
                                  ?? item["wall_type_name"]?.ToString()
                                  ?? item["type"]?.ToString()
                                  ?? payload["wall_type"]?.ToString()
                                  ?? payload["wall_type_name"]?.ToString()
                                  ?? payload["type"]?.ToString()
                                  ?? string.Empty).Trim();
              var wallType = ResolveWallType(wallTypeName);

              var wall = Wall.Create(doc, Line.CreateBound(start, end), wallType.Id, level.Id, heightFeet, baseOffsetFeet, flip, structural);
              created.Add(new JObject
              {
                ["id"] = GetElementIdValue(wall.Id),
                ["type"] = wallType.Name ?? string.Empty,
                ["level"] = level.Name ?? string.Empty,
                ["height_feet"] = heightFeet
              });
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
              MarkSkip(skippedByReason, "invalid_wall_payload");
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException)
            {
              MarkSkip(skippedByReason, "wall_creation_failed");
            }
          }

          tx.Commit();
        }

        if (wallItems.Count == 1 && created.Count == 1 && skippedByReason.Count == 0)
          return (JObject)created[0];

        return new JObject
        {
          ["created_count"] = created.Count,
          ["skipped_count"] = skippedByReason.Values.Sum(),
          ["skipped_reasons"] = new JObject(skippedByReason.Select(kv => new JProperty(kv.Key, kv.Value))),
          ["elements"] = created
        };
      }

      private static JToken CreateFloor(UIApplication app, JObject @params)
      {
        var uidoc = app?.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
        var doc = uidoc.Document;
        var data = @params?["data"] as JObject ?? @params ?? new JObject();

        var level = ResolveLevelByNameOrId(
          doc,
          data["level_id"]?.Value<long?>(),
          data["level"]?.ToString() ?? data["level_name"]?.ToString());
        level ??= new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).FirstOrDefault()
                  ?? throw new InvalidOperationException("No level available.");

        var points = data["points"] as JArray;
        if (points == null || points.Count < 3)
          throw new ArgumentException("points is required with at least 3 points.");

        var loop = new CurveLoop();
        XYZ first = null;
        XYZ prev = null;
        for (var i = 0; i < points.Count; i++)
        {
          var pt = ParsePointToken(points[i], "point", data);
          if (i == 0)
            first = pt;
          if (prev != null && prev.DistanceTo(pt) > 1e-6)
            loop.Append(Line.CreateBound(prev, pt));
          prev = pt;
        }
        if (first == null || prev == null || prev.DistanceTo(first) < 1e-6)
          throw new InvalidOperationException("Invalid points for floor boundary.");
        loop.Append(Line.CreateBound(prev, first));

        var floorTypeName = (data["floor_type"]?.ToString() ?? data["floor_type_name"]?.ToString() ?? string.Empty).Trim();
        var floorType = new FilteredElementCollector(doc)
          .OfClass(typeof(FloorType))
          .Cast<FloorType>()
          .FirstOrDefault(ft => !string.IsNullOrWhiteSpace(floorTypeName) &&
                                string.Equals(ft.Name ?? string.Empty, floorTypeName, StringComparison.OrdinalIgnoreCase))
          ?? new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().FirstOrDefault()
          ?? throw new InvalidOperationException("No floor type available.");

        Floor floor;
        using (var tx = new Transaction(doc, "Revit bridge create floor"))
        {
          if (tx.Start() != TransactionStatus.Started)
            throw new InvalidOperationException("Could not start transaction.");
          var floorFailureOptions = tx.GetFailureHandlingOptions();
          floorFailureOptions.SetFailuresPreprocessor(new AcceptAllFailuresPreprocessor());
          tx.SetFailureHandlingOptions(floorFailureOptions);

          floor = Floor.Create(doc, new List<CurveLoop> { loop }, floorType.Id, level.Id);
          tx.Commit();
        }

        return new JObject
        {
          ["id"] = GetElementIdValue(floor.Id),
          ["type"] = floorType.Name ?? string.Empty,
          ["level"] = level.Name ?? string.Empty
        };
      }

      private static JToken CreateRoof(UIApplication app, JObject @params)
      {
        var uidoc = app?.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
        var doc = uidoc.Document;
        var data = @params?["data"] as JObject ?? @params ?? new JObject();

        var levelId = data["level_id"]?.Value<long?>();
        var levelNameInput = data["level"]?.ToString() ?? data["level_name"]?.ToString();
        var level = ResolveLevelByNameOrId(
          doc,
          levelId,
          levelNameInput);
        var levelWasRequested = levelId.HasValue || !string.IsNullOrWhiteSpace(levelNameInput);
        if (levelWasRequested && level == null)
          throw new InvalidOperationException($"Level not found: '{levelNameInput ?? levelId?.ToString() ?? "unknown"}'.");
        level ??= new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderByDescending(l => l.Elevation).FirstOrDefault()
          ?? throw new InvalidOperationException("No level available.");

        var points = data["points"] as JArray;
        if (points == null || points.Count < 3)
          throw new ArgumentException("points is required with at least 3 points.");

        var roofTypeId = data["roof_type_id"]?.Value<long?>();
        var roofTypeName = (data["roof_type"]?.ToString() ?? data["roof_type_name"]?.ToString() ?? string.Empty).Trim();
        var roofTypes = new FilteredElementCollector(doc).OfClass(typeof(RoofType)).Cast<RoofType>().ToList();
        if (roofTypes.Count == 0)
          throw new InvalidOperationException("No roof type available.");

        // Accept "Type", "Family : Type", exact and contains matches.
        var requestedType = roofTypeName.Contains(":")
          ? roofTypeName.Split(':').Last().Trim()
          : roofTypeName;
        RoofType FindMatch()
        {
          if (string.IsNullOrWhiteSpace(roofTypeName) && string.IsNullOrWhiteSpace(requestedType))
            return null;

          return roofTypes.FirstOrDefault(rt =>
                   string.Equals(rt.Name ?? string.Empty, requestedType ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                 ?? roofTypes.FirstOrDefault(rt =>
                   string.Equals($"{rt.FamilyName ?? string.Empty} : {rt.Name ?? string.Empty}", roofTypeName, StringComparison.OrdinalIgnoreCase))
                 ?? roofTypes.FirstOrDefault(rt =>
                   (rt.Name ?? string.Empty).IndexOf(requestedType ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0)
                 ?? roofTypes.FirstOrDefault(rt =>
                   ($"{rt.FamilyName ?? string.Empty} : {rt.Name ?? string.Empty}")
                   .IndexOf(roofTypeName ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        var roofType = roofTypeId.HasValue
          ? roofTypes.FirstOrDefault(rt => GetElementIdValue(rt.Id) == roofTypeId.Value)
          : FindMatch();
        if (roofTypeId.HasValue && roofType == null)
          throw new InvalidOperationException($"Roof type not found by id: {roofTypeId.Value}.");
        if (!string.IsNullOrWhiteSpace(roofTypeName) && roofType == null)
        {
          var available = string.Join(", ", roofTypes.Take(8).Select(rt => $"{rt.FamilyName} : {rt.Name}"));
          throw new InvalidOperationException($"Roof type not found: '{roofTypeName}'. Available examples: {available}");
        }
        roofType ??= roofTypes.FirstOrDefault(rt =>
                      (rt.FamilyName ?? string.Empty).IndexOf("basic", StringComparison.OrdinalIgnoreCase) >= 0
                      || (rt.Name ?? string.Empty).IndexOf("basic", StringComparison.OrdinalIgnoreCase) >= 0)
                    ?? roofTypes.FirstOrDefault(rt =>
                      (rt.FamilyName ?? string.Empty).IndexOf("glazing", StringComparison.OrdinalIgnoreCase) < 0
                      && (rt.Name ?? string.Empty).IndexOf("glazing", StringComparison.OrdinalIgnoreCase) < 0)
                    ?? roofTypes.First();

        var profile = new CurveArray();
        var boundaryPoints = new List<XYZ>();
        var loop = new CurveLoop();
        XYZ first = null;
        XYZ prev = null;
        for (var i = 0; i < points.Count; i++)
        {
          var pt = ParsePointToken(points[i], "point", data);
          // Footprint roof requires profile curves on the level plane.
          pt = new XYZ(pt.X, pt.Y, level.Elevation);
          boundaryPoints.Add(pt);
          if (i == 0)
            first = pt;
          if (prev != null && prev.DistanceTo(pt) > 1e-6)
          {
            profile.Append(Line.CreateBound(prev, pt));
            loop.Append(Line.CreateBound(prev, pt));
          }
          prev = pt;
        }
        if (first == null || prev == null || prev.DistanceTo(first) < 1e-6)
          throw new InvalidOperationException("Invalid points for roof boundary.");
        profile.Append(Line.CreateBound(prev, first));
        loop.Append(Line.CreateBound(prev, first));

        FootPrintRoof roof;
        using (var tx = new Transaction(doc, "Revit bridge create roof"))
        {
          if (tx.Start() != TransactionStatus.Started)
            throw new InvalidOperationException("Could not start transaction.");
          var roofFailureOptions = tx.GetFailureHandlingOptions();
          roofFailureOptions.SetFailuresPreprocessor(new AcceptAllFailuresPreprocessor());
          tx.SetFailureHandlingOptions(roofFailureOptions);

          try
          {
            // Build footprint inside transaction using the same pattern as Revit SDK NewRoof sample.
            var profileTx = new CurveArray();
            for (var i = 1; i < boundaryPoints.Count; i++)
              profileTx.Append(Line.CreateBound(boundaryPoints[i - 1], boundaryPoints[i]));
            profileTx.Append(Line.CreateBound(boundaryPoints[boundaryPoints.Count - 1], boundaryPoints[0]));

            var creationDoc = doc.Create;
            var mapping = new ModelCurveArray();
            roof = creationDoc.NewFootPrintRoof(profileTx, level, roofType, out mapping);
          }
          catch (Exception ex)
          {
            try
            {
              var loops = new List<CurveLoop> { loop };
              var tried = new List<string>();

              bool IsCurveLoopEnumerable(System.Type t)
              {
                if (!typeof(System.Collections.IEnumerable).IsAssignableFrom(t))
                  return false;
                if (!t.IsGenericType)
                  return true;
                var ga = t.GetGenericArguments();
                return ga.Length == 1 && typeof(CurveLoop).IsAssignableFrom(ga[0]);
              }

              bool IsCurveCollectionParam(System.Type t)
              {
                return t == typeof(CurveArray) ||
                       t == typeof(CurveArrArray) ||
                       IsCurveLoopEnumerable(t);
              }

              object[] BuildArgs(System.Reflection.MethodInfo m)
              {
                var args = new object[m.GetParameters().Length];
                var unknownElementIdSlot = 0;
                for (var i = 0; i < args.Length; i++)
                {
                  var p = m.GetParameters()[i];
                  var pn = (p.Name ?? string.Empty).ToLowerInvariant();
                  if (p.ParameterType == typeof(Document))
                    args[i] = doc;
                  else if (p.ParameterType == typeof(CurveArray))
                    args[i] = profile;
                  else if (p.ParameterType == typeof(CurveArrArray))
                  {
                    var arr = new CurveArrArray();
                    arr.Append(profile);
                    args[i] = arr;
                  }
                  else if (p.ParameterType == typeof(CurveLoop))
                    args[i] = loop;
                  else if (IsCurveLoopEnumerable(p.ParameterType))
                    args[i] = loops;
                  else if (p.ParameterType == typeof(ElementId))
                  {
                    if (pn.Contains("level"))
                      args[i] = level.Id;
                    else if (pn.Contains("roof") || pn.Contains("type"))
                      args[i] = roofType.Id;
                    else
                      args[i] = unknownElementIdSlot++ == 0 ? roofType.Id : level.Id;
                  }
                  else if (p.ParameterType == typeof(bool))
                    args[i] = false;
                  else if (p.ParameterType == typeof(double))
                    args[i] = 0d;
                  else if (p.HasDefaultValue)
                    args[i] = p.DefaultValue;
                  else
                    args[i] = null;
                }
                return args;
              }

              FootPrintRoof TryCreateVia(System.Type ownerType)
              {
                var createMethods = ownerType
                  .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                  .Where(m => string.Equals(m.Name, "Create", StringComparison.Ordinal))
                  .Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(Document)))
                  .Where(m => m.GetParameters().Any(p => IsCurveCollectionParam(p.ParameterType)))
                  // Revit API varies by version: some Create overloads use one ElementId, others two.
                  .Where(m => m.GetParameters().Count(p => p.ParameterType == typeof(ElementId)) >= 1)
                  .ToList();

                foreach (var method in createMethods)
                {
                  try
                  {
                    var created = method.Invoke(null, BuildArgs(method));
                    if (created is FootPrintRoof fp)
                      return fp;
                    if (created is RoofBase rb)
                    {
                      var fromDoc = doc.GetElement(rb.Id) as FootPrintRoof;
                      if (fromDoc != null)
                        return fromDoc;
                    }
                    if (created is ElementId eid)
                    {
                      var fromDoc = doc.GetElement(eid) as FootPrintRoof;
                      if (fromDoc != null)
                        return fromDoc;
                    }
                    tried.Add($"{ownerType.Name}.{method} -> returned {created?.GetType().Name ?? "null"}");
                  }
                  catch (Exception invokeEx)
                  {
                    tried.Add($"{ownerType.Name}.{method} -> {invokeEx.Message}");
                  }
                }

                return null;
              }

              roof = TryCreateVia(typeof(FootPrintRoof))
                ?? TryCreateVia(typeof(RoofBase))
                ?? throw new InvalidOperationException(
                  $"No compatible static Create overload succeeded. Attempts: {string.Join(" || ", tried.Take(6))}");
            }
            catch (Exception ex2)
            {
              var activeView = uidoc.ActiveView;
              var diag = $"doc_title='{doc.Title}', is_family_doc={doc.IsFamilyDocument}, active_view='{activeView?.Name ?? "<null>"}', active_view_type='{activeView?.ViewType.ToString() ?? "<null>"}', active_view_sketch_plane_id={(activeView?.SketchPlane != null ? GetElementIdValue(activeView.SketchPlane.Id).ToString() : "<null>")}, level_id={GetElementIdValue(level.Id)}, level_elevation={level.Elevation.ToString(System.Globalization.CultureInfo.InvariantCulture)}, roof_type_id={GetElementIdValue(roofType.Id)}";
              throw new InvalidOperationException(
                $"create_roof failed (level='{level.Name}', roof_type='{roofType.Name}', points={points.Count}). {diag}. Primary: {ex}. Fallback: {ex2}",
                ex);
            }
          }
          tx.Commit();
        }

        return new JObject
        {
          ["id"] = GetElementIdValue(roof.Id),
          ["type"] = roofType.Name ?? string.Empty,
          ["level"] = level.Name ?? string.Empty
        };
      }

      private static JToken GetRoofTypes(UIApplication app, JObject @params)
      {
        var uidoc = app?.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
        var doc = uidoc.Document;
        var limit = Math.Max(1, @params?["limit"]?.Value<int?>() ?? 200);
        var filter = (@params?["query"]?.ToString() ?? string.Empty).Trim();

        var types = new FilteredElementCollector(doc)
          .OfClass(typeof(RoofType))
          .Cast<RoofType>()
          .Where(rt => rt != null);

        if (!string.IsNullOrWhiteSpace(filter))
        {
          types = types.Where(rt =>
            (rt.Name ?? string.Empty).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
            (rt.FamilyName ?? string.Empty).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
            ($"{rt.FamilyName ?? string.Empty} : {rt.Name ?? string.Empty}")
              .IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        var array = new JArray(types
          .OrderBy(rt => rt.FamilyName ?? string.Empty)
          .ThenBy(rt => rt.Name ?? string.Empty)
          .Take(limit)
          .Select(rt => new JObject
          {
            ["id"] = GetElementIdValue(rt.Id),
            ["family_name"] = rt.FamilyName ?? string.Empty,
            ["type_name"] = rt.Name ?? string.Empty,
            ["display_name"] = $"{rt.FamilyName ?? string.Empty} : {rt.Name ?? string.Empty}"
          }));

        return new JObject
        {
          ["count"] = array.Count,
          ["types"] = array
        };
      }

      private static JToken CreateStair(UIApplication app, JObject @params)
      {
        var uidoc = app?.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
        var doc = uidoc.Document;
        var data = @params?["data"] as JObject ?? @params ?? new JObject();

        var baseLevel = ResolveLevelByNameOrId(
          doc,
          data["base_level_id"]?.Value<long?>() ?? data["level_id"]?.Value<long?>(),
          data["base_level"]?.ToString() ?? data["base_level_name"]?.ToString() ?? data["level"]?.ToString());
        var topLevel = ResolveLevelByNameOrId(
          doc,
          data["top_level_id"]?.Value<long?>(),
          data["top_level"]?.ToString() ?? data["top_level_name"]?.ToString());

        if (baseLevel == null || topLevel == null)
          throw new InvalidOperationException("base_level and top_level are required.");
        if (baseLevel.Id == topLevel.Id || topLevel.Elevation <= baseLevel.Elevation)
          throw new InvalidOperationException("top_level must be above base_level.");

        var start = ParsePointToken(data["start"], "start", data);
        var end = ParsePointToken(data["end"], "end", data);
        if (start.DistanceTo(end) < 1e-6)
          throw new InvalidOperationException("start and end cannot be the same point.");

        var startObj = data["start"] as JObject;
        var endObj = data["end"] as JObject;
        var inferredUnits =
          data["width_units"]?.ToString() ??
          data["units"]?.ToString() ??
          startObj?["units"]?.ToString() ??
          endObj?["units"]?.ToString() ??
          "ft";

        var widthInput = data["width"]?.Value<double?>() ?? (IsMetricUnit(inferredUnits) ? 1.2d : 4.0d);
        var widthFeet = ConvertLengthToFeet(widthInput, inferredUnits);
        widthFeet = Math.Max(0.5d, widthFeet);

        var heightFeet = topLevel.Elevation - baseLevel.Elevation;
        var horizontalRunFeet = Math.Sqrt(
          Math.Pow(end.X - start.X, 2) +
          Math.Pow(end.Y - start.Y, 2));
        var minRecommendedRunFeet = heightFeet * 1.3d;
        var allowShortRun = data["allow_short_run"]?.Value<bool?>() ?? false;
        if (!allowShortRun && horizontalRunFeet < minRecommendedRunFeet)
        {
          throw new InvalidOperationException(
            $"stair run is too short for the requested level change. " +
            $"Current horizontal run: {Math.Round(horizontalRunFeet, 2)} ft. " +
            $"Recommended minimum: {Math.Round(minRecommendedRunFeet, 2)} ft. " +
            $"Increase start/end distance or set allow_short_run=true.");
        }

        var stairsId = ElementId.InvalidElementId;
        var runId = ElementId.InvalidElementId;

        using (var editScope = new StairsEditScope(doc, "Revit bridge create stair"))
        {
          stairsId = editScope.Start(baseLevel.Id, topLevel.Id);

          using (var tx = new Transaction(doc, "Revit bridge stair run"))
          {
            if (tx.Start() != TransactionStatus.Started)
              throw new InvalidOperationException("Could not start transaction.");

            var path = Line.CreateBound(start, end);
            var run = StairsRun.CreateStraightRun(doc, stairsId, path, StairsRunJustification.Center);
            runId = run.Id;

            var widthParam = run.get_Parameter(BuiltInParameter.STAIRS_RUN_ACTUAL_RUN_WIDTH);
            if (widthParam != null && !widthParam.IsReadOnly)
              widthParam.Set(widthFeet);

            tx.Commit();
          }

          editScope.Commit(new AcceptAllFailuresPreprocessor());
        }

        return new JObject
        {
          ["stairs_id"] = GetElementIdValue(stairsId),
          ["run_id"] = GetElementIdValue(runId),
          ["base_level"] = baseLevel.Name ?? string.Empty,
          ["top_level"] = topLevel.Name ?? string.Empty
        };
      }

      private static JToken GetStairRunsLandings(UIApplication app, JObject @params)
      {
        var uidoc = app?.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
        var doc = uidoc.Document;
        var data = @params?["data"] as JObject ?? @params ?? new JObject();

        var stairIdRaw = data["stair_id"]?.Value<long?>() ?? data["element_id"]?.Value<long?>();
        if (!stairIdRaw.HasValue)
          throw new ArgumentException("stair_id is required.");

        var stairs = doc.GetElement(CreateElementId(stairIdRaw.Value)) as Stairs;
        if (stairs == null)
        {
          return new JObject
          {
            ["found"] = false,
            ["stair_id"] = stairIdRaw.Value
          };
        }

        var runs = new JArray();
        foreach (var runId in stairs.GetStairsRuns())
        {
          var run = doc.GetElement(runId);
          runs.Add(new JObject
          {
            ["id"] = GetElementIdValue(runId),
            ["name"] = run?.Name ?? string.Empty
          });
        }

        var landings = new JArray();
        foreach (var landingId in stairs.GetStairsLandings())
        {
          var landing = doc.GetElement(landingId);
          landings.Add(new JObject
          {
            ["id"] = GetElementIdValue(landingId),
            ["name"] = landing?.Name ?? string.Empty
          });
        }

        return new JObject
        {
          ["found"] = true,
          ["stair_id"] = GetElementIdValue(stairs.Id),
          ["runs_count"] = runs.Count,
          ["landings_count"] = landings.Count,
          ["runs"] = runs,
          ["landings"] = landings
        };
      }

      private static JToken CreateStairDimensions(UIApplication app, JObject @params)
      {
        var uidoc = app?.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
        var doc = uidoc.Document;
        var view = uidoc.ActiveView ?? throw new InvalidOperationException("No active Revit view.");

        var data = @params?["data"] as JObject ?? @params ?? new JObject();
        var stairIdRaw = data["stair_id"]?.Value<long?>() ?? data["element_id"]?.Value<long?>();
        if (!stairIdRaw.HasValue)
          throw new ArgumentException("stair_id is required.");

        var stairs = doc.GetElement(CreateElementId(stairIdRaw.Value)) as Stairs;
        if (stairs == null)
          throw new InvalidOperationException("Stair not found.");

        var runIds = stairs.GetStairsRuns().ToList();
        if (runIds.Count < 2)
        {
          return new JObject
          {
            ["created"] = false,
            ["reason"] = "not_enough_runs_for_dimension",
            ["runs_count"] = runIds.Count
          };
        }

        var runA = doc.GetElement(runIds[0]);
        var runB = doc.GetElement(runIds[1]);
        if (runA?.Location is not LocationCurve lcA || runB?.Location is not LocationCurve lcB)
        {
          return new JObject
          {
            ["created"] = false,
            ["reason"] = "runs_do_not_expose_location_curve"
          };
        }

        var p1 = lcA.Curve.Evaluate(0.5, true);
        var p2 = lcB.Curve.Evaluate(0.5, true);
        var line = Line.CreateBound(p1, p2);
        var refs = new ReferenceArray();
        refs.Append(new Reference(runA));
        refs.Append(new Reference(runB));

        Dimension dim;
        using (var tx = new Transaction(doc, "Revit bridge stair dimensions"))
        {
          if (tx.Start() != TransactionStatus.Started)
            throw new InvalidOperationException("Could not start transaction.");
          dim = doc.Create.NewDimension(view, line, refs);
          tx.Commit();
        }

        return new JObject
        {
          ["created"] = dim != null,
          ["dimension_id"] = (dim?.Id == null ? -1 : GetElementIdValue(dim.Id))
        };
      }

      private static JToken SendCodeToRevit(UIApplication app, JObject @params)
      {
        _ = app?.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
        var code = (@params?["code"]?.ToString() ?? string.Empty).Trim();
        var parameters = @params?["parameters"] as JArray ?? new JArray();

        if (string.IsNullOrWhiteSpace(code))
          throw new ArgumentException("code is required for send_code_to_revit.");

        // Dynamic C# execution is intentionally disabled for safety in the bridge runtime.
        return new JObject
        {
          ["executed"] = false,
          ["reason"] = "Dynamic code execution is disabled in this Revit bridge build.",
          ["code_length"] = code.Length,
          ["parameters_count"] = parameters.Count
        };
      }

      private static FamilySymbol ResolveFamilySymbol(Document doc, JObject item, BuiltInCategory fallbackCategory)
      {
        var familyName = (item["family_name"]?.ToString() ?? item["family"]?.ToString() ?? string.Empty).Trim();
        var typeName = (item["type_name"]?.ToString() ?? item["type"]?.ToString() ?? string.Empty).Trim();
        var categoryName = (item["category"]?.ToString() ?? string.Empty).Trim();

        var symbols = new FilteredElementCollector(doc)
          .OfClass(typeof(FamilySymbol))
          .Cast<FamilySymbol>()
          .Where(s => s != null);

        if (!string.IsNullOrWhiteSpace(categoryName))
        {
          symbols = symbols.Where(s =>
            string.Equals(s.Category?.Name ?? string.Empty, categoryName, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
          symbols = symbols.Where(s => GetElementIdValue(s.Category?.Id) == (long)fallbackCategory);
        }

        if (!string.IsNullOrWhiteSpace(familyName))
          symbols = symbols.Where(s => string.Equals(s.FamilyName ?? string.Empty, familyName, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(typeName))
          symbols = symbols.Where(s => string.Equals(s.Name ?? string.Empty, typeName, StringComparison.OrdinalIgnoreCase));

        var symbol = symbols.FirstOrDefault();
        if (symbol != null)
          return symbol;

        if (!string.IsNullOrWhiteSpace(familyName) || !string.IsNullOrWhiteSpace(typeName))
        {
          // Fallback: relax category filter if explicit family/type was requested.
          var fallback = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .Where(s => s != null);
          if (!string.IsNullOrWhiteSpace(familyName))
            fallback = fallback.Where(s => string.Equals(s.FamilyName ?? string.Empty, familyName, StringComparison.OrdinalIgnoreCase));
          if (!string.IsNullOrWhiteSpace(typeName))
            fallback = fallback.Where(s => string.Equals(s.Name ?? string.Empty, typeName, StringComparison.OrdinalIgnoreCase));
          return fallback.FirstOrDefault();
        }

        return null;
      }

      private static FamilySymbol ResolveFirstSymbolByCategory(Document doc, BuiltInCategory category)
      {
        return new FilteredElementCollector(doc)
          .OfClass(typeof(FamilySymbol))
          .Cast<FamilySymbol>()
          .FirstOrDefault(s => GetElementIdValue(s?.Category?.Id) == (long)category);
      }

      private static Level ResolveLevelForPayload(Document doc, View view, JObject item, IDictionary<string, Level> cache)
      {
        var levelId = item["level_id"]?.Value<long?>();
        if (levelId.HasValue)
        {
          var byId = doc.GetElement(CreateElementId(levelId.Value)) as Level;
          if (byId != null)
            return byId;
        }

        var levelName = (item["level"]?.ToString() ?? item["level_name"]?.ToString() ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(levelName))
        {
          if (cache.TryGetValue(levelName, out var cached))
            return cached;

          var byName = new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .FirstOrDefault(l => string.Equals(l.Name ?? string.Empty, levelName, StringComparison.OrdinalIgnoreCase));
          if (byName != null)
          {
            cache[levelName] = byName;
            return byName;
          }
        }

        if (view is ViewPlan vp && vp.GenLevel != null)
          return vp.GenLevel;

        return new FilteredElementCollector(doc)
          .OfClass(typeof(Level))
          .Cast<Level>()
          .OrderBy(l => l.Elevation)
          .FirstOrDefault() ?? throw new InvalidOperationException("No level available.");
      }

      private static ElementId CreateElementId(long id)
      {
        var t = typeof(ElementId);
        var ctorLong = t.GetConstructor(new[] { typeof(long) });
        if (ctorLong != null)
          return (ElementId)ctorLong.Invoke(new object[] { id });

        var ctorInt = t.GetConstructor(new[] { typeof(int) });
        if (ctorInt != null)
          return (ElementId)ctorInt.Invoke(new object[] { checked((int)id) });

        throw new MissingMethodException("ElementId constructor (long/int) not found.");
      }

      private static long GetElementIdValue(ElementId id)
      {
        if (id == null)
          return -1;

        var t = id.GetType();
        var intProp = t.GetProperty("IntegerValue");
        if (intProp != null)
          return Convert.ToInt64(intProp.GetValue(id));

        var valueProp = t.GetProperty("Value");
        if (valueProp != null)
          return Convert.ToInt64(valueProp.GetValue(id));

        return -1;
      }

      private static XYZ ParsePoint(JObject item)
      {
        return ParsePoint(null, "x", "y", "z", item);
      }

      private static XYZ ParsePointToken(JToken token, string prefix, JObject fallback)
      {
        var units = fallback?["units"]?.ToString() ?? "ft";
        var factor = GetLengthUnitFactorToFeet(units);

        if (token is JArray arr && arr.Count >= 2)
        {
          var x = arr[0]?.Value<double?>() ?? 0d;
          var y = arr[1]?.Value<double?>() ?? 0d;
          var z = arr.Count >= 3 ? (arr[2]?.Value<double?>() ?? 0d) : 0d;
          return new XYZ(x * factor, y * factor, z * factor);
        }

        if (token is JObject nested)
        {
          // Support indexed object point format: { "0": x, "1": y, "2": z }.
          var xIndexed = nested["0"]?.Value<double?>();
          var yIndexed = nested["1"]?.Value<double?>();
          if (xIndexed.HasValue && yIndexed.HasValue)
          {
            var zIndexed = nested["2"]?.Value<double?>() ?? 0d;
            return new XYZ(xIndexed.Value * factor, yIndexed.Value * factor, zIndexed * factor);
          }

          // Parse nested x/y/z with fallback units if nested does not include "units".
          var nestedUnits = nested["units"]?.ToString();
          var nestedFactor = GetLengthUnitFactorToFeet(string.IsNullOrWhiteSpace(nestedUnits) ? units : nestedUnits);
          var x = nested["x"]?.Value<double?>() ?? 0d;
          var y = nested["y"]?.Value<double?>() ?? 0d;
          var z = nested["z"]?.Value<double?>() ?? 0d;
          return new XYZ(x * nestedFactor, y * nestedFactor, z * nestedFactor);
        }

        var pointAlias = fallback?[$"{prefix}_point"];
        if (pointAlias is JArray pointArr && pointArr.Count >= 2)
        {
          var x = pointArr[0]?.Value<double?>() ?? 0d;
          var y = pointArr[1]?.Value<double?>() ?? 0d;
          var z = pointArr.Count >= 3 ? (pointArr[2]?.Value<double?>() ?? 0d) : 0d;
          return new XYZ(x * factor, y * factor, z * factor);
        }

        return ParsePoint(null, $"{prefix}_x", $"{prefix}_y", $"{prefix}_z", fallback);
      }

      private static XYZ ParsePoint(JObject nested, string xKey, string yKey, string zKey, JObject fallback)
      {
        var source = nested ?? fallback ?? new JObject();
        var units = source["units"]?.ToString() ?? fallback?["units"]?.ToString() ?? "ft";
        var factor = GetLengthUnitFactorToFeet(units);

        var x = source[xKey]?.Value<double?>() ?? 0d;
        var y = source[yKey]?.Value<double?>() ?? 0d;
        var z = source[zKey]?.Value<double?>() ?? 0d;
        return new XYZ(x * factor, y * factor, z * factor);
      }

      private static double ConvertLengthToFeet(double value, string units)
      {
        return value * GetLengthUnitFactorToFeet(units);
      }

      private static bool IsMetricUnit(string units)
      {
        var u = (units ?? string.Empty).Trim().ToLowerInvariant();
        return u is "m" or "meter" or "meters" or "metre" or "metres" or
               "cm" or "centimeter" or "centimeters" or "centimetre" or "centimetres" or
               "mm" or "millimeter" or "millimeters" or "millimetre" or "millimetres";
      }

      private static double GetLengthUnitFactorToFeet(string units)
      {
        var u = (units ?? string.Empty).Trim().ToLowerInvariant();
        return u switch
        {
          "m" or "meter" or "meters" or "metre" or "metres" => 3.28084d,
          "cm" or "centimeter" or "centimeters" or "centimetre" or "centimetres" => 0.0328084d,
          "mm" or "millimeter" or "millimeters" or "millimetre" or "millimetres" => 0.00328084d,
          "in" or "inch" or "inches" => 1d / 12d,
          "ft" or "foot" or "feet" or "" => 1d,
          _ => 1d
        };
      }

      private static SketchPlane EnsureSketchPlane(Document doc, View view, double? targetElevationFeet = null)
      {
        // For footprint roofs, Revit is sensitive to the sketch plane matching the target level plane.
        if (targetElevationFeet.HasValue)
        {
          var normal = view.ViewDirection;
          if (normal == null || normal.GetLength() < 1e-9)
            normal = XYZ.BasisZ;
          var origin = new XYZ(0d, 0d, targetElevationFeet.Value);
          var plane = Plane.CreateByNormalAndOrigin(normal, origin);
          var sketch = SketchPlane.Create(doc, plane);
          view.SketchPlane = sketch;
          return sketch;
        }

        if (view.SketchPlane != null)
          return view.SketchPlane;

        var defaultPlane = Plane.CreateByNormalAndOrigin(view.ViewDirection, view.Origin);
        var defaultSketch = SketchPlane.Create(doc, defaultPlane);
        view.SketchPlane = defaultSketch;
        return defaultSketch;
      }

      private static bool TryGetHostFaceReferenceAndPoint(Element host, out Reference faceReference, out XYZ point)
      {
        faceReference = null;
        point = XYZ.Zero;

        var opts = new Options
        {
          ComputeReferences = true,
          DetailLevel = ViewDetailLevel.Medium
        };
        var geometry = host.get_Geometry(opts);
        if (geometry == null)
          return false;

        foreach (var obj in geometry)
        {
          Solid solid = null;
          if (obj is Solid s && s.Faces.Size > 0)
            solid = s;
          else if (obj is GeometryInstance gi)
            solid = gi.GetInstanceGeometry().OfType<Solid>().FirstOrDefault(sol => sol.Faces.Size > 0);

          if (solid == null)
            continue;

          var face = solid.Faces
            .Cast<Face>()
            .OrderByDescending(f => f.Area)
            .FirstOrDefault();

          if (face == null || face.Reference == null)
            continue;

          var box = face.GetBoundingBox();
          var uv = (box.Min + box.Max) * 0.5;
          faceReference = face.Reference;
          point = face.Evaluate(uv);
          return true;
        }

        return false;
      }

      private static Color ParseColor(JObject data)
      {
        var hex = (data["color"]?.ToString() ?? data["hex"]?.ToString() ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(hex))
        {
          var parsed = hex.StartsWith("#", StringComparison.Ordinal) ? hex.Substring(1) : hex;
          if (parsed.Length == 6 &&
              byte.TryParse(parsed.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var hr) &&
              byte.TryParse(parsed.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var hg) &&
              byte.TryParse(parsed.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var hb))
          {
            return new Color(hr, hg, hb);
          }
        }

        var r = (byte)Math.Max(0, Math.Min(255, data["r"]?.Value<int?>() ?? 255));
        var g = (byte)Math.Max(0, Math.Min(255, data["g"]?.Value<int?>() ?? 0));
        var b = (byte)Math.Max(0, Math.Min(255, data["b"]?.Value<int?>() ?? 0));
        return new Color(r, g, b);
      }

      private static void MarkSkip(IDictionary<string, int> skippedByReason, string reason)
      {
        if (string.IsNullOrWhiteSpace(reason))
          reason = "unknown";
        skippedByReason[reason] = skippedByReason.TryGetValue(reason, out var current) ? current + 1 : 1;
      }

      private static Level ResolveLevelByNameOrId(Document doc, long? levelId, string levelName)
      {
        if (levelId.HasValue)
        {
          var byId = doc.GetElement(CreateElementId(levelId.Value)) as Level;
          if (byId != null)
            return byId;
        }

        var clean = (levelName ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(clean))
        {
          var byName = new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .FirstOrDefault(l => string.Equals(l.Name ?? string.Empty, clean, StringComparison.OrdinalIgnoreCase));
          if (byName != null)
            return byName;
        }

        return null;
      }

      private sealed class AcceptAllFailuresPreprocessor : IFailuresPreprocessor
      {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
          var messages = failuresAccessor.GetFailureMessages();
          foreach (var message in messages)
            failuresAccessor.DeleteWarning(message);
          return FailureProcessingResult.Continue;
        }
      }
    }

    private sealed class BridgeCommandRequest
    {
      public BridgeCommandRequest(string commandId, string method, JObject @params)
      {
        CommandId = commandId;
        Method = method;
        Params = @params ?? new JObject();
        Completion = new TaskCompletionSource<BridgeCommandResponse>();
      }

      public string CommandId { get; }
      public string Method { get; }
      public JObject Params { get; }
      public TaskCompletionSource<BridgeCommandResponse> Completion { get; }
    }

    private sealed class BridgeCommandResponse
    {
      public bool Ok { get; private set; }
      public JToken Result { get; private set; }
      public string Error { get; private set; }

      public static BridgeCommandResponse Success(JToken result)
      {
        return new BridgeCommandResponse { Ok = true, Result = result };
      }

      public static BridgeCommandResponse Fail(string error)
      {
        return new BridgeCommandResponse { Ok = false, Error = error };
      }
    }
  }
}
