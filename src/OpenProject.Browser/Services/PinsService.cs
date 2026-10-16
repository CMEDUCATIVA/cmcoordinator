using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using OpenProject.Browser.WebViewIntegration;
using OpenProject.Shared.Pins;
using Serilog;

namespace OpenProject.Browser.Services
{
  public sealed class PinsService
  {
    private readonly PinStore _store = new();
    private readonly List<PinRecord> _pins;
    private readonly Dictionary<string, string> _pendingSnapshotUrls = new();
    private readonly Dictionary<string, string> _pendingViewpointJson = new();
    private readonly HashSet<string> _viewpointFetchInFlight = new();
    private string _lastViewpointWorkPackageId;
    private System.DateTime _lastViewpointWorkPackageIdAt;

    public PinsService()
    {
      _pins = _store.Load();
      Log.Information("PinsService initialized with {Count} pin(s) from local store", _pins.Count);
    }

    public void AddOrUpdatePin(string workPackageId, List<string> ifcGuids, string url, string viewpointJson)
    {
      if (string.IsNullOrWhiteSpace(workPackageId))
        return;

      var existing = _pins.FirstOrDefault(p => p.WorkPackageId == workPackageId);
      if (existing != null)
      {
        if (ifcGuids != null && ifcGuids.Count > 0)
          existing.IfcGuids = ifcGuids;
        existing.Visible = true;
        if (!string.IsNullOrWhiteSpace(url))
        {
          existing.Url = url;
          ApplyProjectMetadata(existing, url);
        }
        if (!string.IsNullOrWhiteSpace(viewpointJson))
          existing.ViewpointJson = viewpointJson;
        UpdateRevitViewInfo(existing, viewpointJson);
        ApplyPendingSnapshot(existing);
        ApplyPendingViewpoint(existing);
        LogPinState("AddOrUpdatePin updated", existing);
        _store.Save(_pins);
        SendPinsUpdate();
        Log.Information("AddOrUpdatePin updated workPackageId={WorkPackageId} ifcGuids={Count} url={Url}", workPackageId, ifcGuids?.Count ?? 0, url ?? string.Empty);
        return;
      }

      var record = new PinRecord
      {
        WorkPackageId = workPackageId,
        IfcGuids = ifcGuids ?? new List<string>(),
        Visible = true,
        Url = url,
        ViewpointJson = viewpointJson
      };
      ApplyProjectMetadata(record, url);
      UpdateRevitViewInfo(record, viewpointJson);
      ApplyPendingSnapshot(record);
      ApplyPendingViewpoint(record);
      _pins.Insert(0, record);
      LogPinState("AddOrUpdatePin added", record);

      _store.Save(_pins);
      SendPinsUpdate();
      Log.Information("AddOrUpdatePin added workPackageId={WorkPackageId} ifcGuids={Count} url={Url}", workPackageId, ifcGuids?.Count ?? 0, url ?? string.Empty);
    }

    public void UpdateSnapshotUrl(string workPackageId, string snapshotUrl)
    {
      if (string.IsNullOrWhiteSpace(workPackageId) || string.IsNullOrWhiteSpace(snapshotUrl))
        return;

      var existing = _pins.FirstOrDefault(p => p.WorkPackageId == workPackageId);
      if (existing != null)
      {
        existing.SnapshotUrl = snapshotUrl;
        _store.Save(_pins);
        SendPinsUpdate();
        Log.Information("SnapshotUrl updated workPackageId={WorkPackageId} url={Url}", workPackageId, snapshotUrl);
        return;
      }

      _pendingSnapshotUrls[workPackageId] = snapshotUrl;
      Log.Information("SnapshotUrl cached workPackageId={WorkPackageId} url={Url}", workPackageId, snapshotUrl);
    }

    public void UpdateViewpointJson(string workPackageId, string viewpointJson)
    {
      if (string.IsNullOrWhiteSpace(workPackageId) || string.IsNullOrWhiteSpace(viewpointJson))
        return;

      var existing = _pins.FirstOrDefault(p => p.WorkPackageId == workPackageId);
      if (existing != null)
      {
        var merged = MergeRevitViewInfo(existing.ViewpointJson, viewpointJson);
        Log.Information("ViewpointJson merge workPackageId={WorkPackageId} existingLength={ExistingLength} incomingLength={IncomingLength} mergedLength={MergedLength}",
          workPackageId,
          existing.ViewpointJson?.Length ?? 0,
          viewpointJson?.Length ?? 0,
          merged?.Length ?? 0);
        LogViewpointSummary(workPackageId, viewpointJson);
        if (!string.IsNullOrWhiteSpace(merged))
          LogViewpointSummary(workPackageId, merged);
        if (!string.IsNullOrWhiteSpace(existing.ViewpointJson)
            && string.Equals(existing.ViewpointJson, merged, System.StringComparison.Ordinal))
          return;

        existing.ViewpointJson = merged;
        UpdateRevitViewInfo(existing, merged);
        LogPinState("ViewpointJson updated", existing);
        _store.Save(_pins);
        SendPinsUpdate();
        Log.Information("ViewpointJson updated workPackageId={WorkPackageId} length={Length}",
          workPackageId, existing.ViewpointJson?.Length ?? 0);
        return;
      }

      _pendingViewpointJson[workPackageId] = viewpointJson;
      LogViewpointSummary(workPackageId, viewpointJson);
      Log.Information("ViewpointJson cached workPackageId={WorkPackageId} length={Length}",
        workPackageId, viewpointJson.Length);
    }

    public bool NeedsViewpointJson(string workPackageId)
    {
      if (string.IsNullOrWhiteSpace(workPackageId))
        return false;

      if (_pendingViewpointJson.ContainsKey(workPackageId))
        return false;

      var existing = _pins.FirstOrDefault(p => p.WorkPackageId == workPackageId);
      return existing == null || string.IsNullOrWhiteSpace(existing.ViewpointJson);
    }

    public bool TryBeginViewpointFetch(string workPackageId)
    {
      if (string.IsNullOrWhiteSpace(workPackageId))
        return false;

      lock (_viewpointFetchInFlight)
      {
        if (_viewpointFetchInFlight.Contains(workPackageId))
          return false;
        _viewpointFetchInFlight.Add(workPackageId);
        return true;
      }
    }

    public void EndViewpointFetch(string workPackageId)
    {
      if (string.IsNullOrWhiteSpace(workPackageId))
        return;

      lock (_viewpointFetchInFlight)
      {
        _viewpointFetchInFlight.Remove(workPackageId);
      }
    }

    private void ApplyPendingSnapshot(PinRecord record)
    {
      if (record == null || string.IsNullOrWhiteSpace(record.WorkPackageId))
        return;

      if (!_pendingSnapshotUrls.TryGetValue(record.WorkPackageId, out var snapshotUrl))
        return;

      record.SnapshotUrl = snapshotUrl;
      _pendingSnapshotUrls.Remove(record.WorkPackageId);
    }

    private void ApplyPendingViewpoint(PinRecord record)
    {
      if (record == null || string.IsNullOrWhiteSpace(record.WorkPackageId))
        return;

      if (!_pendingViewpointJson.TryGetValue(record.WorkPackageId, out var viewpointJson))
        return;

      record.ViewpointJson = MergeRevitViewInfo(record.ViewpointJson, viewpointJson);
      UpdateRevitViewInfo(record, record.ViewpointJson);
      LogPinState("ApplyPendingViewpoint", record);
      _pendingViewpointJson.Remove(record.WorkPackageId);
    }

    private static void UpdateRevitViewInfo(PinRecord record, string viewpointJson)
    {
      if (record == null || string.IsNullOrWhiteSpace(viewpointJson))
        return;

      try
      {
        var jObject = JObject.Parse(viewpointJson);
        if (jObject["revit_view_id"] != null
            && long.TryParse(jObject["revit_view_id"]?.ToString(), out var viewId))
          record.RevitViewId = viewId;
        if (jObject["revit_view_name"] != null)
          record.RevitViewName = jObject["revit_view_name"]?.ToString();
      }
      catch
      {
        // ignore malformed payloads
      }
    }

    private static void ApplyProjectMetadata(PinRecord record, string url)
    {
      if (record == null || string.IsNullOrWhiteSpace(url))
        return;

      var projectIdentifier = ExtractProjectIdentifierFromUrl(url);
      if (string.IsNullOrWhiteSpace(projectIdentifier))
        return;

      record.ProjectIdentifier = projectIdentifier;
      if (string.IsNullOrWhiteSpace(record.ProjectId))
        record.ProjectId = projectIdentifier;
      if (string.IsNullOrWhiteSpace(record.ProjectName))
        record.ProjectName = projectIdentifier;
    }

    private static string ExtractProjectIdentifierFromUrl(string url)
    {
      if (string.IsNullOrWhiteSpace(url))
        return null;

      string path;
      if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
      {
        path = absolute.AbsolutePath;
      }
      else if (Uri.TryCreate(url, UriKind.Relative, out var relative))
      {
        path = relative.OriginalString;
      }
      else
      {
        return null;
      }

      var segments = path
        .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(Uri.UnescapeDataString)
        .ToArray();

      for (var i = 0; i < segments.Length - 1; i++)
      {
        if (!segments[i].Equals("projects", StringComparison.OrdinalIgnoreCase))
          continue;

        return segments[i + 1];
      }

      return null;
    }

    private static string MergeRevitViewInfo(string existingJson, string incomingJson)
    {
      if (string.IsNullOrWhiteSpace(incomingJson))
        return incomingJson;

      if (string.IsNullOrWhiteSpace(existingJson))
        return incomingJson;

      try
      {
        var incoming = JObject.Parse(incomingJson);
        var hasViewId = incoming["revit_view_id"] != null;
        var hasViewName = incoming["revit_view_name"] != null;
        if (hasViewId && hasViewName)
          return incomingJson;

        var existing = JObject.Parse(existingJson);
        if (!hasViewId && existing["revit_view_id"] != null)
          incoming["revit_view_id"] = existing["revit_view_id"];
        if (!hasViewName && existing["revit_view_name"] != null)
          incoming["revit_view_name"] = existing["revit_view_name"];

        return incoming.ToString(Newtonsoft.Json.Formatting.None);
      }
      catch
      {
        return incomingJson;
      }
    }

    public void TryAddFromViewpointPayload(string messagePayload, string currentUrl)
    {
      var workPackageId = ResolveWorkPackageId(currentUrl);
      if (string.IsNullOrWhiteSpace(workPackageId))
      {
        Log.Warning("TryAddFromViewpointPayload: empty workPackageId from url={Url}", currentUrl);
        return;
      }

      Log.Information("TryAddFromViewpointPayload resolved workPackageId={WorkPackageId} url={Url}", workPackageId, currentUrl ?? string.Empty);

      try
      {
        var json = NormalizeJsonPayload(messagePayload);
        if (string.IsNullOrWhiteSpace(json))
        {
          Log.Warning("TryAddFromViewpointPayload: empty payload for workPackageId={WorkPackageId}", workPackageId);
          return;
        }
        LogViewpointSummary(workPackageId, json);
        var jObject = JObject.Parse(json);
        var components = jObject["components"];
        var selection = components?["selection"];
        if (selection == null)
        {
          Log.Warning("TryAddFromViewpointPayload: selection missing for workPackageId={WorkPackageId}, creating pin without selection", workPackageId);
          AddOrUpdatePin(workPackageId, new List<string>(), currentUrl, json);
          return;
        }

        var ifcGuids = selection
          .Select(item => (string)item?["ifc_guid"])
          .Where(id => !string.IsNullOrWhiteSpace(id))
          .ToList();

        if (ifcGuids.Count == 0)
        {
          Log.Warning("TryAddFromViewpointPayload: no ifc_guids for workPackageId={WorkPackageId}, creating pin without selection", workPackageId);
          AddOrUpdatePin(workPackageId, new List<string>(), currentUrl, json);
          return;
        }

        AddOrUpdatePin(workPackageId, ifcGuids, currentUrl, json);
      }
      catch (Exception ex)
      {
        // ignore malformed payloads
        Log.Warning(ex, "TryAddFromViewpointPayload: malformed payload for workPackageId={WorkPackageId}", workPackageId);
      }
    }

    private static string NormalizeJsonPayload(string payload)
    {
      if (string.IsNullOrWhiteSpace(payload))
        return payload;

      var trimmed = payload.Trim();
      if (trimmed.StartsWith("\uFEFF", System.StringComparison.Ordinal))
        trimmed = trimmed.TrimStart('\uFEFF');

      if (trimmed.StartsWith("\"", System.StringComparison.Ordinal))
      {
        try
        {
          var unescaped = Newtonsoft.Json.JsonConvert.DeserializeObject<string>(trimmed);
          if (!string.IsNullOrWhiteSpace(unescaped))
            return unescaped.Trim();
        }
        catch
        {
          // fall through
        }
      }

      if (trimmed.StartsWith("{", System.StringComparison.Ordinal))
        return trimmed;

      var first = trimmed.IndexOf('{');
      var last = trimmed.LastIndexOf('}');
      if (first >= 0 && last > first)
        return trimmed.Substring(first, last - first + 1);

      return trimmed;
    }

    private static void LogViewpointSummary(string workPackageId, string normalizedJson)
    {
      try
      {
        var jObject = JObject.Parse(normalizedJson);
        var hasCamera =
          jObject.SelectToken("camera") != null ||
          jObject.SelectToken("camera.perspective_camera") != null ||
          jObject.SelectToken("camera.orthogonal_camera") != null ||
          jObject.SelectToken("perspective_camera") != null ||
          jObject.SelectToken("orthogonal_camera") != null;
        var hasSelection = jObject.SelectToken("components.selection") is JArray selection && selection.Count > 0;
        var hasVisibility = jObject.SelectToken("components.visibility") != null;
        var hasClipping = jObject.SelectToken("clipping_planes") is JArray clipping && clipping.Count > 0;
        var hasSnapshot = jObject.SelectToken("snapshot") != null || jObject.SelectToken("snapshot_url") != null;
        var viewId = jObject.SelectToken("revit_view_id")?.ToString();
        var viewName = jObject.SelectToken("revit_view_name")?.ToString();

        Log.Information(
          "ViewpointJson summary workPackageId={WorkPackageId} len={Length} hasCamera={HasCamera} hasSelection={HasSelection} hasVisibility={HasVisibility} hasClipping={HasClipping} hasSnapshot={HasSnapshot} viewId={ViewId} viewName='{ViewName}'",
          workPackageId,
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
        Log.Warning(ex, "ViewpointJson summary failed workPackageId={WorkPackageId}", workPackageId);
      }
    }

    public void ReplacePinsFromRevit(List<PinRecord> pins)
    {
      if (pins == null) return;
      _pins.Clear();
      _pins.AddRange(pins);
      _store.Save(_pins);
    }

    public void RemovePin(string workPackageId)
    {
      if (string.IsNullOrWhiteSpace(workPackageId))
      {
        Log.Warning("RemovePin called with empty workPackageId");
        return;
      }

      var removed = _pins.RemoveAll(p => p.WorkPackageId == workPackageId);
      if (removed == 0)
      {
        Log.Warning("RemovePin did not find pin for workPackageId={WorkPackageId}", workPackageId);
        return;
      }

      _store.Save(_pins);
      SendPinsUpdate();
      Log.Information("RemovePin removed {Count} pin(s) for workPackageId={WorkPackageId}", removed, workPackageId);
    }

    public void SendPinsUpdate()
    {
      foreach (var pin in _pins.Where(p => p != null && !string.IsNullOrWhiteSpace(p.WorkPackageId)))
        LogPinState("SendPinsUpdate", pin);
      IpcManager.SendPinsUpdate(_pins);
    }

    public void SetLastViewpointWorkPackageId(string workPackageId)
    {
      if (string.IsNullOrWhiteSpace(workPackageId))
        return;

      _lastViewpointWorkPackageId = workPackageId;
      _lastViewpointWorkPackageIdAt = System.DateTime.UtcNow;
      Log.Information("LastViewpointWorkPackageId set to {WorkPackageId}", workPackageId);
    }

    private string ResolveWorkPackageId(string currentUrl)
    {
      var fromUrl = WorkPackageIdResolver.FromUrl(currentUrl);
      if (!string.IsNullOrWhiteSpace(fromUrl))
        return fromUrl;

      if (string.IsNullOrWhiteSpace(_lastViewpointWorkPackageId))
        return fromUrl;

      if ((System.DateTime.UtcNow - _lastViewpointWorkPackageIdAt).TotalSeconds > 30)
        return fromUrl;

      Log.Information("ResolveWorkPackageId using last viewpoint workPackageId={WorkPackageId}", _lastViewpointWorkPackageId);
      return _lastViewpointWorkPackageId;
    }

    private static void LogPinState(string stage, PinRecord record)
    {
      if (record == null)
        return;

      Log.Information(
        "{Stage} workPackageId={WorkPackageId} ifcGuids={IfcGuids} viewpointLength={ViewpointLength} snapshotUrl={HasSnapshotUrl} revitViewId={ViewId} revitViewName='{ViewName}'",
        stage,
        record.WorkPackageId ?? string.Empty,
        record.IfcGuids?.Count ?? 0,
        record.ViewpointJson?.Length ?? 0,
        !string.IsNullOrWhiteSpace(record.SnapshotUrl),
        record.RevitViewId?.ToString() ?? "null",
        record.RevitViewName ?? string.Empty);
    }
  }
}
