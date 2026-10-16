using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;
using OpenProject.Revit.Entry;
using OpenProject.Revit.Models;
using OpenProject.Revit.Views;
using OpenProject.Revit.ViewModels;
using OpenProject.Shared.Pins;
using OpenProject.Shared;
using OpenProject.Shared.BcfApi;
using OpenProject.Revit.Extensions;
using Serilog;
using Newtonsoft.Json;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;

namespace OpenProject.Revit.Services
{
  public static class PinsPaneManager
  {
    private static PinsPaneViewModel _viewModel;
    private static PinsPaneView _view;
    private static readonly object _uiQueueLock = new();
    private static readonly Queue<Action> _uiQueue = new();
    private static bool _idlingHooked;
    private static bool _statusHighlightEnabled;
    private static List<PinRecord> _lastPins;
    private static readonly Dictionary<Autodesk.Revit.DB.ElementId, HashSet<Autodesk.Revit.DB.ElementId>> _highlightedByView = new();
    private static readonly string LocalPinsPath = Path.Combine(ConfigurationConstant.OpenProjectApplicationData, "Pins.json");

    public static UIApplication UiApp { get; private set; }

    public static DockablePaneId PaneId { get; } =
      new DockablePaneId(new Guid("C5E3C1C1-7A9D-4E6B-8A74-6BFA64A9B5D0"));

    public static void Initialize(PinsPaneViewModel viewModel)
    {
      _viewModel = viewModel;
    }

    public static void RegisterView(PinsPaneView view)
    {
      _view = view;
    }

    public static void ApplyColumnVisibility(McpSettings settings)
    {
      _view?.ApplyColumnVisibility(settings);
    }

    public static void ApplyAutoRefresh(McpSettings settings)
    {
      try
      {
        _view?.ApplyAutoRefresh(settings);
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Failed to apply auto refresh settings.");
      }
    }

    public static Task<bool> RefreshPinsFromApiAsync()
    {
      if (_viewModel == null)
        return Task.FromResult(false);

      var pins = _viewModel.Items.Select(i => i.Record).ToList();
      if (pins.Count == 0)
      {
        var persistedPins = LoadPersistedPins();
        if (persistedPins.Count > 0)
        {
          pins = persistedPins;
          EnqueueUiAction(() => UpdatePins(persistedPins, preserveExistingDisplayData: true));
          Log.Information("API refresh loaded persisted pins from local store. pinsCount={Count}", pins.Count);
        }
      }

      if (pins.Count == 0)
      {
        Log.Information("API refresh skipped (no pins).");
        return Task.FromResult(false);
      }

      Log.Information("API refresh started pinsCount={Count}", pins.Count);
      return Task.Run(async () =>
      {
        var settings = McpSettingsStore.Load();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var updated = false;

        foreach (var pin in pins)
        {
          if (string.IsNullOrWhiteSpace(pin.WorkPackageId))
            continue;

          var info = await McpClient.GetWorkPackageInfoAsync(pin.WorkPackageId, settings, cts.Token).ConfigureAwait(false);
          if (info == null)
            continue;

          Log.Information("API refresh values workPackageId={Id} assignee='{Assignee}' responsible='{Responsible}'",
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
            Log.Information("API refresh cleared field workPackageId={Id} field=Assignee", pin.WorkPackageId);
          if (responsibleChanged && string.IsNullOrWhiteSpace(info.Responsible))
            Log.Information("API refresh cleared field workPackageId={Id} field=Responsible", pin.WorkPackageId);

          if (changed)
          {
            Log.Information("API refresh applied workPackageId={Id}", pin.WorkPackageId);
            updated = true;
          }
        }

        if (!updated)
        {
          Log.Information("API refresh finished (no changes).");
          return false;
        }

        EnqueueUiAction(() => UpdatePins(pins));
        Log.Information("API refresh finished (updated).");
        return true;
      });
    }

    private static List<PinRecord> LoadPersistedPins()
    {
      var documentPins = LoadPersistedPinsFromDocument();
      if (documentPins.Count > 0)
      {
        Log.Information("Loaded persisted pins from document. count={Count}", documentPins.Count);
        SavePinsToLocalStore(documentPins);
        return documentPins;
      }

      return LoadPersistedPinsFromLocalStore();
    }

    private static List<PinRecord> LoadPersistedPinsFromDocument()
    {
      try
      {
        var doc = UiApp?.ActiveUIDocument?.Document;
        if (doc == null)
          return new List<PinRecord>();

        return DocumentPinsStore.Load(doc);
      }
      catch (Exception ex)
      {
        Log.Warning(ex, "Failed to load persisted pins from document.");
        return new List<PinRecord>();
      }
    }

    private static List<PinRecord> LoadPersistedPinsFromLocalStore()
    {
      try
      {
        if (!File.Exists(LocalPinsPath))
          return new List<PinRecord>();

        var json = File.ReadAllText(LocalPinsPath);
        var pins = JsonConvert.DeserializeObject<List<PinRecord>>(json) ?? new List<PinRecord>();
        Log.Information("Loaded persisted pins path={Path} count={Count} bytes={Bytes}", LocalPinsPath, pins.Count, json.Length);
        return pins;
      }
      catch (Exception ex)
      {
        Log.Warning(ex, "Failed to load persisted pins from local store.");
        return new List<PinRecord>();
      }
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

    public static void SetUIApplication(UIApplication app)
    {
      UiApp = app;
      if (UiApp != null && !_idlingHooked)
      {
        UiApp.ViewActivated += (_, _) =>
        {
          ApplyColumnStateFromDocument();
          LoadPinsForCurrentDocument();
        };
        UiApp.Idling += (_, _) =>
        {
          Action next = null;
          lock (_uiQueueLock)
          {
            if (_uiQueue.Count > 0)
              next = _uiQueue.Dequeue();
          }
          next?.Invoke();
        };
        _idlingHooked = true;
      }

      ApplyColumnStateFromDocument();
      var settings = McpSettingsStore.Load();
      _view?.ApplyAutoRefresh(settings);
      LoadPinsForCurrentDocument();
    }

    public static void EnqueueUiAction(Action action)
    {
      if (action == null)
        return;
      lock (_uiQueueLock)
      {
        _uiQueue.Enqueue(action);
      }
    }

    public static void SaveColumnState(McpSettings settings)
    {
      if (settings == null)
        return;

      EnqueueUiAction(() =>
      {
        var doc = UiApp?.ActiveUIDocument?.Document;
        if (doc == null)
          return;

        try
        {
          var state = new CdeTableState
          {
            ColumnVisibility = settings.ColumnVisibility,
            ColumnOrder = settings.ColumnOrder,
            ColumnWidths = settings.ColumnWidths
          };

          using var tx = new Autodesk.Revit.DB.Transaction(doc, "MCP Save CDE Table State");
          tx.Start();
          var saved = DocumentTableStateStore.Save(doc, state);
          tx.Commit();
          if (saved)
            Log.Information("CDE table state saved to document.");
        }
        catch (Exception ex)
        {
          Log.Error(ex, "Failed to save CDE table state to document.");
        }
      });
    }

    private static void ApplyColumnStateFromDocument()
    {
      var doc = UiApp?.ActiveUIDocument?.Document;
      if (doc == null)
        return;

      try
      {
        var state = DocumentTableStateStore.Load(doc);
        if (state == null)
          return;

        var settings = McpSettingsStore.Load();
        settings.ColumnVisibility = state.ColumnVisibility ?? settings.ColumnVisibility;
        settings.ColumnOrder = state.ColumnOrder ?? settings.ColumnOrder;
        settings.ColumnWidths = state.ColumnWidths ?? settings.ColumnWidths;
        _view?.ApplyColumnVisibility(settings);
        Log.Information("CDE table state applied from document.");
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Failed to apply CDE table state from document.");
      }
    }

    public static void UpdatePins(List<PinRecord> pins, bool preserveExistingDisplayData = false)
    {
      if (preserveExistingDisplayData && pins != null && _lastPins != null && _lastPins.Count > 0)
        pins = MergeDisplayFieldsFromLastPins(pins, _lastPins);

      Log.Information("PinsPaneManager.UpdatePins pinsCount={Count} viewModelReady={HasVm} uiAppReady={HasUi}",
        pins?.Count ?? 0, _viewModel != null, UiApp != null);
      foreach (var pin in (pins ?? new List<PinRecord>()).Where(p => p != null && !string.IsNullOrWhiteSpace(p.WorkPackageId)))
      {
        Log.Information(
          "PinsPaneManager.UpdatePins pin workPackageId={WorkPackageId} ifcGuids={IfcGuids} viewpointLength={ViewpointLength} snapshotUrl={HasSnapshotUrl} revitViewId={ViewId} revitViewName='{ViewName}'",
          pin.WorkPackageId,
          pin.IfcGuids?.Count ?? 0,
          pin.ViewpointJson?.Length ?? 0,
          !string.IsNullOrWhiteSpace(pin.SnapshotUrl),
          pin.RevitViewId?.ToString() ?? "null",
          pin.RevitViewName ?? string.Empty);
      }
      _lastPins = pins?.ToList();
      _viewModel?.LoadPins(pins);
      PersistPins(pins);
      if (UiApp != null)
      {
        PinsManager.UpdatePins(UiApp, pins);
      }
      if (_statusHighlightEnabled)
        ApplyStatusHighlightAsync();
    }

    private static List<PinRecord> MergeDisplayFieldsFromLastPins(List<PinRecord> incomingPins, List<PinRecord> existingPins)
    {
      var existingById = existingPins
        .Where(p => p != null && !string.IsNullOrWhiteSpace(p.WorkPackageId))
        .GroupBy(p => p.WorkPackageId)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

      foreach (var pin in incomingPins)
      {
        if (pin == null || string.IsNullOrWhiteSpace(pin.WorkPackageId))
          continue;

        if (!existingById.TryGetValue(pin.WorkPackageId, out var existing))
          continue;

        pin.Name = PreferExisting(existing.Name, pin.Name);
        pin.ProjectId = PreferExisting(existing.ProjectId, pin.ProjectId);
        pin.ProjectIdentifier = PreferExisting(existing.ProjectIdentifier, pin.ProjectIdentifier);
        pin.ProjectName = PreferExisting(existing.ProjectName, pin.ProjectName);
        pin.Type = PreferExisting(existing.Type, pin.Type);
        pin.Priority = PreferExisting(existing.Priority, pin.Priority);
        pin.Status = PreferExisting(existing.Status, pin.Status);
        pin.StatusColor = PreferExisting(existing.StatusColor, pin.StatusColor);
        pin.Assignee = PreferExisting(existing.Assignee, pin.Assignee);
        pin.Responsible = PreferExisting(existing.Responsible, pin.Responsible);
      }

      return incomingPins;
    }

    private static string PreferExisting(string existing, string incoming)
    {
      return !string.IsNullOrWhiteSpace(existing) ? existing : incoming;
    }

    public static void PushCurrentPinsToDesktopApp()
    {
      try
      {
        var pins = _lastPins;
        if ((pins == null || pins.Count == 0) && UiApp?.ActiveUIDocument?.Document != null)
          pins = LoadPersistedPins();

        if (pins == null)
          pins = new List<PinRecord>();

        RibbonButtonClickHandler.IpcHandler?.SendPinsUpdateToDesktopApp(pins);
        Log.Information("Pushed current pins to DesktopApp. count={Count}", pins.Count);
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Failed to push current pins to DesktopApp.");
      }
    }

    private static void LoadPinsForCurrentDocument()
    {
      try
      {
        var pins = LoadPersistedPins();
        if (pins.Count == 0)
          return;

        UpdatePins(pins, preserveExistingDisplayData: true);
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Failed to load pins for current document.");
      }
    }

    private static void PersistPins(List<PinRecord> pins)
    {
      SavePinsToLocalStore(pins ?? new List<PinRecord>());
      SavePinsToDocument(pins ?? new List<PinRecord>());
    }

    private static void SavePinsToLocalStore(List<PinRecord> pins)
    {
      try
      {
        var dir = Path.GetDirectoryName(LocalPinsPath);
        if (!Directory.Exists(dir))
          Directory.CreateDirectory(dir);

        var json = JsonConvert.SerializeObject(pins ?? new List<PinRecord>(), Formatting.Indented);
        File.WriteAllText(LocalPinsPath, json);
        Log.Information("Saved pins to local store path={Path} count={Count} bytes={Bytes}",
          LocalPinsPath, pins?.Count ?? 0, json.Length);
        foreach (var pin in (pins ?? new List<PinRecord>()).Where(p => p != null && !string.IsNullOrWhiteSpace(p.WorkPackageId)))
        {
          Log.Information(
            "Saved pins to local store pin workPackageId={WorkPackageId} ifcGuids={IfcGuids} viewpointLength={ViewpointLength} snapshotUrl={HasSnapshotUrl} revitViewId={ViewId} revitViewName='{ViewName}'",
            pin.WorkPackageId,
            pin.IfcGuids?.Count ?? 0,
            pin.ViewpointJson?.Length ?? 0,
            !string.IsNullOrWhiteSpace(pin.SnapshotUrl),
            pin.RevitViewId?.ToString() ?? "null",
            pin.RevitViewName ?? string.Empty);
        }
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Failed to save pins to local store.");
      }
    }

    private static void SavePinsToDocument(List<PinRecord> pins)
    {
      var doc = UiApp?.ActiveUIDocument?.Document;
      if (doc == null)
        return;

      try
      {
        using var tx = new Autodesk.Revit.DB.Transaction(doc, "OpenProject Save Pins");
        tx.Start();
        var saved = DocumentPinsStore.Save(doc, pins ?? new List<PinRecord>());
        tx.Commit();
        if (saved)
        {
          Log.Information("Saved pins to document. count={Count}", pins?.Count ?? 0);
          foreach (var pin in (pins ?? new List<PinRecord>()).Where(p => p != null && !string.IsNullOrWhiteSpace(p.WorkPackageId)))
          {
            Log.Information(
              "Saved pins to document pin workPackageId={WorkPackageId} ifcGuids={IfcGuids} viewpointLength={ViewpointLength} snapshotUrl={HasSnapshotUrl} revitViewId={ViewId} revitViewName='{ViewName}'",
              pin.WorkPackageId,
              pin.IfcGuids?.Count ?? 0,
              pin.ViewpointJson?.Length ?? 0,
              !string.IsNullOrWhiteSpace(pin.SnapshotUrl),
              pin.RevitViewId?.ToString() ?? "null",
              pin.RevitViewName ?? string.Empty);
          }
        }
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Failed to save pins to document.");
      }
    }

    public static void SetStatusHighlightEnabled(bool enabled)
    {
      _statusHighlightEnabled = enabled;
      Log.Information("Status highlight toggle {State}.", enabled ? "enabled" : "disabled");
      if (enabled)
        ApplyStatusHighlightAsync();
      else
        ClearStatusHighlightAsync();
    }

    private static void ApplyStatusHighlightAsync()
    {
      EnqueueUiAction(() =>
      {
        try
        {
          ClearStatusHighlightInternal();

          if (!_statusHighlightEnabled)
            return;

          var doc = UiApp?.ActiveUIDocument?.Document;
          if (doc == null)
            return;

          var pins = _lastPins;
          if (pins == null || pins.Count == 0)
            return;

          var pinSelections = pins
            .Where(p => p.IfcGuids != null && p.IfcGuids.Count > 0 && !string.IsNullOrWhiteSpace(p.StatusColor))
            .ToList();
          if (pinSelections.Count == 0)
          {
            Log.Information("Status highlight: no pins with selection and status color.");
            return;
          }

          var allElements = new Autodesk.Revit.DB.FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .ToElements()
            .Select(e => e.Id);
          var ifcMapRaw = doc.GetIfcGuidElementIdMap(allElements);
          var ifcMap = new Dictionary<string, Autodesk.Revit.DB.ElementId>(StringComparer.OrdinalIgnoreCase);
          foreach (var pair in ifcMapRaw)
          {
            if (!ifcMap.ContainsKey(pair.Key))
              ifcMap[pair.Key] = pair.Value;
          }

          var views = new Autodesk.Revit.DB.FilteredElementCollector(doc)
            .OfClass(typeof(Autodesk.Revit.DB.View))
            .Cast<Autodesk.Revit.DB.View>()
            .Where(IsHighlightableView)
            .ToList();
          var viewIds = views.Select(v => v.Id).ToList();

          var solidFillId = GetSolidFillPatternId(doc);
          Log.Information("Status highlight: applying to views={ViewCount} pinsWithSelection={PinCount}", viewIds.Count, pinSelections.Count);

          var uiDoc = UiApp?.ActiveUIDocument;
          if (uiDoc != null)
          {
            try
            {
              uiDoc.Selection.SetElementIds(new List<Autodesk.Revit.DB.ElementId>());
            }
            catch
            {
              // ignore selection clear failures
            }
          }
          var activeView = doc.ActiveView;
          if (activeView != null)
          {
            var included = views.Any(v => v.Id == activeView.Id);
            Log.Information("Status highlight: activeView='{ViewName}' type={ViewType} id={ViewId} included={Included}",
              activeView.Name, activeView.ViewType, activeView.Id.GetIntValue(), included);
          }

          using var tx = new Autodesk.Revit.DB.Transaction(doc, "Apply status highlight");
          if (tx.Start() != Autodesk.Revit.DB.TransactionStatus.Started)
          {
            Log.Warning("Status highlight: transaction not started.");
            return;
          }

          foreach (var pin in pinSelections)
          {
            Log.Information("Status highlight: workPackageId={Id} ifcGuids={Count}",
              pin.WorkPackageId, pin.IfcGuids.Count);
            var color = ParseRevitColor(pin.StatusColor);
            if (color == null)
            {
              Log.Warning("Status highlight: failed to parse color workPackageId={Id} color='{Color}'",
                pin.WorkPackageId, pin.StatusColor ?? string.Empty);
              continue;
            }
            Log.Information("Status highlight: parsed color workPackageId={Id} rgb={R},{G},{B}",
              pin.WorkPackageId, color.Red, color.Green, color.Blue);

            var ogs = new Autodesk.Revit.DB.OverrideGraphicSettings();
            ogs.SetProjectionLineColor(color);
            ogs.SetCutLineColor(color);
            ogs.SetSurfaceTransparency(0);
            if (solidFillId != Autodesk.Revit.DB.ElementId.InvalidElementId)
            {
              ogs.SetSurfaceForegroundPatternId(solidFillId);
              ogs.SetSurfaceForegroundPatternColor(color);
              ogs.SetSurfaceBackgroundPatternId(solidFillId);
              ogs.SetSurfaceBackgroundPatternColor(color);
              ogs.SetCutForegroundPatternId(solidFillId);
              ogs.SetCutForegroundPatternColor(color);
              ogs.SetCutBackgroundPatternId(solidFillId);
              ogs.SetCutBackgroundPatternColor(color);
            }

            var missingGuids = 0;
            var matchedGuids = 0;
            foreach (var guid in pin.IfcGuids)
            {
              if (!ifcMap.TryGetValue(guid, out var elementId))
              {
                missingGuids++;
                Log.Warning("Status highlight: ifcGuid not resolved workPackageId={Id} ifcGuid={IfcGuid}",
                  pin.WorkPackageId, guid);
                continue;
              }
              matchedGuids++;

              var element = doc.GetElement(elementId);
              var categoryName = element?.Category?.Name ?? "<no-category>";
              var elementName = element?.Name ?? "<no-name>";
              var typeName = element?.GetType().Name ?? "<no-type>";
              var uniqueId = element?.UniqueId ?? "<no-unique-id>";
              var parentId = element is Autodesk.Revit.DB.FamilyInstance familyInstance && familyInstance.SuperComponent != null
                ? familyInstance.SuperComponent.Id.GetIntValue().ToString()
                : "<none>";
              var bbox = activeView != null ? element?.get_BoundingBox(activeView) : null;
              var bboxMin = bbox?.Min != null ? $"({bbox.Min.X:F3},{bbox.Min.Y:F3},{bbox.Min.Z:F3})" : "<null>";
              var bboxMax = bbox?.Max != null ? $"({bbox.Max.X:F3},{bbox.Max.Y:F3},{bbox.Max.Z:F3})" : "<null>";

              Log.Information(
                "Status highlight: resolved ifcGuid workPackageId={Id} ifcGuid={IfcGuid} elementId={ElementId} uniqueId={UniqueId} category={Category} name={Name} type={Type} superComponentId={SuperComponentId} bboxMin={BboxMin} bboxMax={BboxMax}",
                pin.WorkPackageId,
                guid,
                elementId.GetIntValue(),
                uniqueId,
                categoryName,
                elementName,
                typeName,
                parentId,
                bboxMin,
                bboxMax);

              var appliedViews = 0;
              var failedViews = 0;
              foreach (var viewId in viewIds)
              {
                try
                {
                  var view = doc.GetElement(viewId) as Autodesk.Revit.DB.View;
                  if (view == null)
                    continue;

                  if (activeView != null && view.Id == activeView.Id)
                  {
                    EnsureViewSupportsSurfaceColor(view);
                  }

                  view.SetElementOverrides(elementId, ogs);
                  if (!_highlightedByView.TryGetValue(viewId, out var set))
                  {
                    set = new HashSet<Autodesk.Revit.DB.ElementId>();
                    _highlightedByView[viewId] = set;
                  }
                  set.Add(elementId);
                  appliedViews++;
                }
                catch (Exception ex)
                {
                  failedViews++;
                  Log.Warning(ex, "Status highlight: failed override elementId={ElementId} viewId={ViewId}",
                    elementId.GetIntValue(), viewId.GetIntValue());
                }
              }

              if (appliedViews > 0)
                Log.Information("Status highlight: applied elementId={ElementId} views={Views}",
                  elementId.GetIntValue(), appliedViews);
              else
                Log.Warning("Status highlight: overrides not applied for elementId={ElementId} failedViews={FailedViews}",
                  elementId.GetIntValue(), failedViews);
            }

            Log.Information("Status highlight: workPackageId={Id} matchedGuids={Matched} missingGuids={Missing}",
              pin.WorkPackageId, matchedGuids, missingGuids);
            Log.Information("Status highlight: workPackageId={Id} guidList=[{GuidList}]",
              pin.WorkPackageId, string.Join(", ", pin.IfcGuids));
            if (missingGuids > 0)
              Log.Warning("Status highlight: missing elements for {Missing} IFC GUID(s) workPackageId={Id}",
                missingGuids, pin.WorkPackageId);
          }

          tx.Commit();
        }
        catch (Exception ex)
        {
          Log.Error(ex, "Failed to apply status highlight.");
        }
      });
    }

    private static void ClearStatusHighlightAsync()
    {
      EnqueueUiAction(() =>
      {
        try
        {
          ClearStatusHighlightInternal();
        }
        catch (Exception ex)
        {
          Log.Error(ex, "Failed to clear status highlight.");
        }
      });
    }

    private static void ClearStatusHighlightInternal()
    {
      var doc = UiApp?.ActiveUIDocument?.Document;
      if (doc == null)
        return;

      if (_highlightedByView.Count == 0)
        return;

      using var tx = new Autodesk.Revit.DB.Transaction(doc, "Clear status highlight");
      if (tx.Start() != Autodesk.Revit.DB.TransactionStatus.Started)
        return;

      var reset = new Autodesk.Revit.DB.OverrideGraphicSettings();
      foreach (var pair in _highlightedByView.ToList())
      {
        var view = doc.GetElement(pair.Key) as Autodesk.Revit.DB.View;
        if (view == null)
          continue;

        foreach (var elementId in pair.Value)
        {
          try
          {
            view.SetElementOverrides(elementId, reset);
          }
          catch
          {
            // ignore
          }
        }
      }

      tx.Commit();
      _highlightedByView.Clear();
    }

    private static Autodesk.Revit.DB.ElementId GetSolidFillPatternId(Autodesk.Revit.DB.Document doc)
    {
      if (doc == null)
        return Autodesk.Revit.DB.ElementId.InvalidElementId;

      try
      {
        var patterns = new Autodesk.Revit.DB.FilteredElementCollector(doc)
          .OfClass(typeof(Autodesk.Revit.DB.FillPatternElement))
          .Cast<Autodesk.Revit.DB.FillPatternElement>()
          .ToList();

        var modelSolid = patterns.FirstOrDefault(p =>
        {
          var fp = p.GetFillPattern();
          return fp != null
            && fp.IsSolidFill
            && fp.Target == Autodesk.Revit.DB.FillPatternTarget.Model;
        });
        if (modelSolid != null)
          return modelSolid.Id;

        var anySolid = patterns.FirstOrDefault(p => p.GetFillPattern()?.IsSolidFill == true);
        if (anySolid != null)
          return anySolid.Id;
      }
      catch (Exception ex)
      {
        Log.Warning(ex, "Status highlight: failed to resolve solid fill pattern.");
      }

      return Autodesk.Revit.DB.ElementId.InvalidElementId;
    }

    private static void EnsureViewSupportsSurfaceColor(Autodesk.Revit.DB.View view)
    {
      if (view == null)
        return;

      var style = view.DisplayStyle;
      if (style == Autodesk.Revit.DB.DisplayStyle.Wireframe)
      {
        view.DisplayStyle = Autodesk.Revit.DB.DisplayStyle.ShadingWithEdges;
        Log.Information("Status highlight: active view display style set to {Style}.", view.DisplayStyle);
      }
      else
      {
        Log.Information("Status highlight: active view display style is {Style}.", view.DisplayStyle);
      }
    }

    private static bool IsHighlightableView(Autodesk.Revit.DB.View view)
    {
      if (view == null || view.IsTemplate)
        return false;

      switch (view.ViewType)
      {
        case Autodesk.Revit.DB.ViewType.FloorPlan:
        case Autodesk.Revit.DB.ViewType.CeilingPlan:
        case Autodesk.Revit.DB.ViewType.AreaPlan:
        case Autodesk.Revit.DB.ViewType.EngineeringPlan:
        case Autodesk.Revit.DB.ViewType.Section:
        case Autodesk.Revit.DB.ViewType.Elevation:
        case Autodesk.Revit.DB.ViewType.Detail:
        case Autodesk.Revit.DB.ViewType.ThreeD:
        case Autodesk.Revit.DB.ViewType.Walkthrough:
          return true;
        default:
          return false;
      }
    }

    private static Autodesk.Revit.DB.Color ParseRevitColor(string value)
    {
      if (string.IsNullOrWhiteSpace(value))
        return null;

      var trimmed = value.Trim();
      if (trimmed.StartsWith("#", StringComparison.Ordinal))
      {
        var hex = trimmed.TrimStart('#');
        if (hex.Length == 8)
          hex = hex.Substring(2);
        if (hex.Length == 6
            && byte.TryParse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
            && byte.TryParse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
            && byte.TryParse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
          return new Autodesk.Revit.DB.Color(r, g, b);
      }

      if (trimmed.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
      {
        var start = trimmed.IndexOf('(');
        var end = trimmed.IndexOf(')');
        if (start > -1 && end > start)
        {
          var parts = trimmed.Substring(start + 1, end - start - 1)
            .Split(',')
            .Select(p => p.Trim())
            .ToArray();
          if (parts.Length >= 3
              && byte.TryParse(parts[0], out var r)
              && byte.TryParse(parts[1], out var g)
              && byte.TryParse(parts[2], out var b))
            return new Autodesk.Revit.DB.Color(r, g, b);
        }
      }

      return null;
    }

    public static void OpenViewpointFromPin(PinRecord record)
    {
      if (record == null)
        return;

      if (!string.IsNullOrWhiteSpace(record.ViewpointJson))
      {
        Log.Information("OpenViewpointFromPin: applying viewpoint JSON.");
        OpenViewpointFromJson(record.ViewpointJson);
        return;
      }

      if (record.RevitViewId == null && string.IsNullOrWhiteSpace(record.RevitViewName))
      {
        OpenViewpointFromJson(record.ViewpointJson);
        return;
      }

      var viewIdValue = record.RevitViewId;
      var viewNameValue = record.RevitViewName;
      if (viewIdValue == null && string.IsNullOrWhiteSpace(viewNameValue))
      {
        OpenViewpointFromJson(record.ViewpointJson);
        return;
      }

      EnqueueUiAction(() =>
      {
        try
        {
          if (UiApp?.ActiveUIDocument?.Document == null)
            return;

          var doc = UiApp.ActiveUIDocument.Document;
          Autodesk.Revit.DB.View view = null;
          if (viewIdValue != null)
          {
            view = doc.GetElement(CreateElementId(viewIdValue.Value)) as Autodesk.Revit.DB.View;
            if (view != null)
            {
              Log.Information("OpenViewpointFromPin resolved view by id {ViewId} -> '{ViewName}'.",
                view.Id.GetIntValue(), view.Name);
            }
          }

          if (view == null && !string.IsNullOrWhiteSpace(viewNameValue))
          {
            view = new Autodesk.Revit.DB.FilteredElementCollector(doc)
              .OfClass(typeof(Autodesk.Revit.DB.View))
              .Cast<Autodesk.Revit.DB.View>()
              .FirstOrDefault(v => string.Equals(v.Name, viewNameValue, StringComparison.OrdinalIgnoreCase));
            if (view != null)
            {
              Log.Information("OpenViewpointFromPin resolved view by name '{ViewName}' -> id {ViewId}.",
                view.Name, view.Id.GetIntValue());
            }
          }

          if (view == null)
          {
            Log.Warning("OpenViewpointFromPin: stored view not found. Falling back to viewpoint JSON.");
            OpenViewpointFromJson(record.ViewpointJson);
            return;
          }

          if (view.IsTemplate)
          {
            Log.Warning("OpenViewpointFromPin: view '{ViewName}' is a template. Skipping activation.", view.Name);
            return;
          }

          Log.Information("OpenViewpointFromPin activating view '{ViewName}' (id {ViewId}).",
            view.Name, view.Id.GetIntValue());
          OpenViewpointEventHandler.ActivateView(view.Id);

        }
        catch (Exception ex)
        {
          Log.Error(ex, "OpenViewpointFromPin failed.");
        }
      });
    }

    public static void OpenViewpointFromJson(string viewpointJson)
    {
      if (string.IsNullOrWhiteSpace(viewpointJson))
      {
        Log.Information("OpenViewpointFromJson skipped (empty payload).");
        return;
      }

      var normalizedJson = NormalizeJsonPayload(viewpointJson);
      if (string.IsNullOrWhiteSpace(normalizedJson))
      {
        Log.Warning("OpenViewpointFromJson skipped (normalized payload empty).");
        return;
      }
      LogViewpointSummary(normalizedJson);

      EnqueueUiAction(() =>
      {
        try
        {
          var hasSelection = false;
          try
          {
            var selectionToken = Newtonsoft.Json.Linq.JObject.Parse(normalizedJson)?["components"]?["selection"];
            if (selectionToken is Newtonsoft.Json.Linq.JArray selectionArray)
              hasSelection = selectionArray.Count > 0;
          }
          catch
          {
            // Ignore selection parsing errors; fall back to default behavior below.
          }

          Autodesk.Revit.DB.View resolvedView = null;
          if (UiApp?.ActiveUIDocument?.Document != null)
          {
            var doc = UiApp.ActiveUIDocument.Document;
            var jObject = Newtonsoft.Json.Linq.JObject.Parse(normalizedJson);
            var viewIdToken = jObject["revit_view_id"];
            var viewNameToken = jObject["revit_view_name"];

            Autodesk.Revit.DB.View view = null;
            if (viewIdToken != null && long.TryParse(viewIdToken.ToString(), out var viewIdValue))
            {
              view = doc.GetElement(CreateElementId(viewIdValue)) as Autodesk.Revit.DB.View;
              if (view != null)
              {
                Log.Information("OpenViewpointFromJson resolved view by id {ViewId} -> '{ViewName}'.",
                  view.Id.GetIntValue(), view.Name);
              }
            }

            if (view == null && viewNameToken != null)
            {
              var viewName = viewNameToken.ToString();
              view = new Autodesk.Revit.DB.FilteredElementCollector(doc)
                .OfClass(typeof(Autodesk.Revit.DB.View))
                .Cast<Autodesk.Revit.DB.View>()
                .FirstOrDefault(v => string.Equals(v.Name, viewName, StringComparison.OrdinalIgnoreCase));
              if (view != null)
              {
                Log.Information("OpenViewpointFromJson resolved view by name '{ViewName}' -> id {ViewId}.",
                  view.Name, view.Id.GetIntValue());
              }
            }

            if (view != null)
            {
              resolvedView = view;
              if (view.IsTemplate)
              {
                Log.Warning("OpenViewpointFromJson: view '{ViewName}' is a template. Skipping activation.", view.Name);
              }
              else
              {
                Log.Information("OpenViewpointFromJson activating view '{ViewName}' (id {ViewId}).",
                  view.Name, view.Id.GetIntValue());
                OpenViewpointEventHandler.ActivateView(view.Id);
              }
            }
            else if (!hasSelection)
            {
              Log.Warning("OpenViewpointFromJson: no selection and stored view not found. Applying viewpoint camera only.");
            }
          }

          var args = new WebUiMessageEventArgs(MessageTypes.VIEWPOINT_DATA, "0", normalizedJson);
          BcfViewpointWrapper bcfViewpoint = MessageDeserializer.DeserializeBcfViewpoint(args);
          if (resolvedView != null)
            OpenViewpointEventHandler.ShowBcfViewpoint(bcfViewpoint, resolvedView.Id);
          else
            OpenViewpointEventHandler.ShowBcfViewpoint(bcfViewpoint);
          Log.Information("OpenViewpointFromJson applied.");
        }
        catch (Exception ex)
        {
          Log.Error(ex, "OpenViewpointFromJson failed.");
        }
      });
    }

    private static string NormalizeJsonPayload(string payload)
    {
      if (string.IsNullOrWhiteSpace(payload))
        return payload;

      var trimmed = payload.Trim();
      if (trimmed.StartsWith("\uFEFF", StringComparison.Ordinal))
        trimmed = trimmed.TrimStart('\uFEFF');

      if (trimmed.StartsWith("\"", StringComparison.Ordinal))
      {
        try
        {
          var unescaped = JsonConvert.DeserializeObject<string>(trimmed);
          if (!string.IsNullOrWhiteSpace(unescaped))
            return unescaped.Trim();
        }
        catch
        {
          // fall through
        }
      }

      if (trimmed.StartsWith("{", StringComparison.Ordinal))
        return trimmed;

      var first = trimmed.IndexOf('{');
      var last = trimmed.LastIndexOf('}');
      if (first >= 0 && last > first)
        return trimmed.Substring(first, last - first + 1);

      return trimmed;
    }

    private static void LogViewpointSummary(string normalizedJson)
    {
      try
      {
        var jObject = Newtonsoft.Json.Linq.JObject.Parse(normalizedJson);
        var hasCamera =
          jObject.SelectToken("camera") != null ||
          jObject.SelectToken("camera.perspective_camera") != null ||
          jObject.SelectToken("camera.orthogonal_camera") != null ||
          jObject.SelectToken("perspective_camera") != null ||
          jObject.SelectToken("orthogonal_camera") != null;
        var hasSelection = jObject.SelectToken("components.selection") is Newtonsoft.Json.Linq.JArray selection && selection.Count > 0;
        var hasVisibility = jObject.SelectToken("components.visibility") != null;
        var hasClipping = jObject.SelectToken("clipping_planes") is Newtonsoft.Json.Linq.JArray clipping && clipping.Count > 0;
        var hasSnapshot = jObject.SelectToken("snapshot") != null || jObject.SelectToken("snapshot_url") != null;
        var viewId = jObject.SelectToken("revit_view_id")?.ToString();
        var viewName = jObject.SelectToken("revit_view_name")?.ToString();

        Log.Information(
          "ViewpointJson summary len={Length} hasCamera={HasCamera} hasSelection={HasSelection} hasVisibility={HasVisibility} hasClipping={HasClipping} hasSnapshot={HasSnapshot} viewId={ViewId} viewName='{ViewName}'",
          normalizedJson.Length,
          hasCamera,
          hasSelection,
          hasVisibility,
          hasClipping,
          hasSnapshot,
          string.IsNullOrWhiteSpace(viewId) ? "null" : viewId,
          viewName ?? string.Empty);
      }
      catch (Exception ex)
      {
        Log.Warning(ex, "ViewpointJson summary failed.");
      }
    }

    private static Autodesk.Revit.DB.ElementId CreateElementId(long id)
    {
      var t = typeof(Autodesk.Revit.DB.ElementId);
      var ctorLong = t.GetConstructor(new[] { typeof(long) });
      if (ctorLong != null)
        return (Autodesk.Revit.DB.ElementId)ctorLong.Invoke(new object[] { id });

      var ctorInt = t.GetConstructor(new[] { typeof(int) });
      if (ctorInt != null)
        return (Autodesk.Revit.DB.ElementId)ctorInt.Invoke(new object[] { checked((int)id) });

      throw new MissingMethodException("ElementId constructor (long/int) not found.");
    }

  }
}
