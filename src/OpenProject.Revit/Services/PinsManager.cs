using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using OpenProject.Revit.Extensions;
using OpenProject.Shared.Math3D.Enumeration;
using OpenProject.Shared;
using OpenProject.Shared.Pins;
using Serilog;

namespace OpenProject.Revit.Services
{
  public static class PinsManager
  {
    private static readonly Dictionary<string, List<ElementId>> _pinElements = new();
    private static readonly Dictionary<string, List<string>> _pinIfcGuids = new();

    public static void UpdatePins(UIApplication app, List<PinRecord> pins)
    {
      if (app?.ActiveUIDocument?.Document == null)
        return;

      var doc = app.ActiveUIDocument.Document;
      var view = app.ActiveUIDocument.ActiveView;
      if (view == null)
        return;

      using var trans = new Transaction(doc);
      if (trans.Start("Update Cmcoordinator Pins") != TransactionStatus.Started)
        return;

      // Remove previous pins
      foreach (var entry in _pinElements.Values.SelectMany(v => v))
      {
        try { doc.Delete(entry); } catch { /* ignore */ }
      }
      _pinElements.Clear();
      _pinIfcGuids.Clear();
      

      if (pins == null || pins.Count == 0)
      {
        trans.Commit();
        return;
      }

      var guidToId = BuildIfcGuidMap(doc);

      foreach (var pin in pins)
      {
        if (pin.IfcGuids == null || pin.IfcGuids.Count == 0) continue;

        _pinIfcGuids[pin.WorkPackageId] = pin.IfcGuids;

        if (!pin.Visible) continue;

        var elementIds = pin.IfcGuids
          .Where(guidToId.ContainsKey)
          .Select(g => guidToId[g])
          .Distinct()
          .ToList();

        if (elementIds.Count == 0) continue;

        var center = ComputeCenter(doc, view, elementIds);
        if (center == null) continue;

        var pinElementIds = CreatePin(doc, view, center, pin.WorkPackageId);
        if (pinElementIds == null || pinElementIds.Count == 0) continue;

        _pinElements[pin.WorkPackageId] = pinElementIds;
      }

      trans.Commit();
    }

    public static void FocusPin(UIApplication app, string workPackageId)
    {
      FocusPin(app, workPackageId, null);
    }

    public static void FocusPin(UIApplication app, string workPackageId, PinRecord record)
    {
      if (app?.ActiveUIDocument?.Document == null)
      {
        Log.Warning("FocusPin skipped because UIApplication or document is unavailable. workPackageId={WorkPackageId}",
          workPackageId ?? string.Empty);
        return;
      }

      if (string.IsNullOrWhiteSpace(workPackageId))
      {
        Log.Warning("FocusPin skipped because workPackageId is empty.");
        return;
      }

      Log.Information("FocusPin enqueued for UI execution. workPackageId={WorkPackageId}", workPackageId);
      PinsPaneManager.EnqueueUiAction(() => ExecuteFocusPin(app, workPackageId, record));
    }

    private static void ExecuteFocusPin(UIApplication app, string workPackageId, PinRecord record)
    {
      if (app?.ActiveUIDocument?.Document == null)
      {
        Log.Warning("FocusPin skipped because UIApplication or document is unavailable. workPackageId={WorkPackageId}",
          workPackageId ?? string.Empty);
        return;
      }

      if (!_pinIfcGuids.TryGetValue(workPackageId, out var guids) || guids.Count == 0)
      {
        Log.Warning("FocusPin skipped because pin has no tracked IFC GUIDs. workPackageId={WorkPackageId}",
          workPackageId);
        return;
      }

      var uiDoc = app.ActiveUIDocument;
      var doc = uiDoc.Document;
      var guidToId = BuildIfcGuidMap(doc);
      var elementIds = guids
        .Where(guidToId.ContainsKey)
        .Select(g => guidToId[g])
        .Distinct()
        .ToList();

      if (elementIds.Count == 0)
      {
        Log.Warning("FocusPin skipped because no Revit elements matched IFC GUIDs. workPackageId={WorkPackageId}",
          workPackageId);
        return;
      }

      var targetView = ResolveBestFocusView(uiDoc, record);
      if (targetView != null && uiDoc.ActiveView?.Id != targetView.Id)
      {
        Log.Information("FocusPin switching to best view. workPackageId={WorkPackageId} targetViewId={ViewId} targetViewName='{ViewName}'",
          workPackageId,
          targetView.Id.GetIntValue(),
          targetView.Name ?? string.Empty);
        uiDoc.ActiveView = targetView;
        uiDoc.RefreshActiveView();
      }
      else
      {
        Log.Information("FocusPin using current active view. workPackageId={WorkPackageId} activeViewId={ViewId} activeViewName='{ViewName}'",
          workPackageId,
          uiDoc.ActiveView?.Id?.GetIntValue() ?? -1,
          uiDoc.ActiveView?.Name ?? string.Empty);
      }

      var cameraApplied = false;
      if (uiDoc.ActiveView is View3D active3D)
        cameraApplied = ApplyBestCameraPosition(doc, active3D, elementIds, workPackageId);

      uiDoc.Selection.SetElementIds(elementIds);
      if (cameraApplied)
      {
        uiDoc.RefreshActiveView();
        var zoomApplied = TryZoomToSelectedElements(uiDoc, uiDoc.ActiveView, elementIds, workPackageId);
        if (zoomApplied)
        {
          Log.Information("FocusPin skipped ShowElements because camera orientation and zoom framing were applied. workPackageId={WorkPackageId}",
            workPackageId);
        }
        else
        {
          uiDoc.ShowElements(elementIds);
          Log.Information("FocusPin used ShowElements fallback because zoom framing could not be applied after camera orientation. workPackageId={WorkPackageId}",
            workPackageId);
        }
      }
      else
      {
        uiDoc.ShowElements(elementIds);
        Log.Information("FocusPin used ShowElements fallback because no camera orientation was applied. workPackageId={WorkPackageId}",
          workPackageId);
      }
      Log.Information("FocusPin completed. workPackageId={WorkPackageId} elementCount={Count} finalViewId={ViewId} finalViewName='{ViewName}'",
        workPackageId,
        elementIds.Count,
        uiDoc.ActiveView?.Id?.GetIntValue() ?? -1,
        uiDoc.ActiveView?.Name ?? string.Empty);
    }

    public static void ClearPins(UIApplication app)
    {
      if (app?.ActiveUIDocument?.Document == null)
        return;

      var doc = app.ActiveUIDocument.Document;
      using var trans = new Transaction(doc);
      if (trans.Start("Clear Cmcoordinator Pins") != TransactionStatus.Started)
        return;

      foreach (var entry in _pinElements.Values.SelectMany(v => v))
      {
        try { doc.Delete(entry); } catch { /* ignore */ }
      }
      _pinElements.Clear();
      _pinIfcGuids.Clear();
      

      trans.Commit();
    }

    private static Dictionary<string, ElementId> BuildIfcGuidMap(Document doc)
    {
      var map = new Dictionary<string, ElementId>();
      var elements = new FilteredElementCollector(doc)
        .WhereElementIsNotElementType()
        .WhereElementIsViewIndependent()
        .ToElements();

      foreach (var element in elements)
      {
        try
        {
          var ifcGuid = IfcGuid.ToIfcGuid(ExportUtils.GetExportId(doc, element.Id));
          if (!map.ContainsKey(ifcGuid))
            map.Add(ifcGuid, element.Id);
        }
        catch
        {
          // ignore elements without IFC GUID
        }
      }

      return map;
    }

    private static XYZ ComputeCenter(Document doc, View view, IEnumerable<ElementId> elementIds)
    {
      var centers = new List<XYZ>();
      foreach (var id in elementIds)
      {
        var element = doc.GetElement(id);
        if (element == null) continue;

        var bbox = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
        if (bbox == null) continue;

        var min = bbox.Min;
        var max = bbox.Max;
        centers.Add(new XYZ(
          (min.X + max.X) / 2,
          (min.Y + max.Y) / 2,
          (min.Z + max.Z) / 2));
      }

      if (centers.Count == 0) return null;

      var avg = new XYZ(
        centers.Average(c => c.X),
        centers.Average(c => c.Y),
        centers.Average(c => c.Z));

      return avg;
    }

    private static List<ElementId> CreatePin(Document doc, View view, XYZ center, string workPackageId)
    {
      try
      {
        if (view == null)
          return new List<ElementId>();

        return new List<ElementId>();
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Failed to create pin for work package {id}.", workPackageId);
        return new List<ElementId>();
      }
    }

    private static View3D ResolveBestFocusView(UIDocument uiDoc, PinRecord record)
    {
      var doc = uiDoc?.Document;
      if (doc == null)
        return null;

      if (record?.RevitViewId is long rawViewId && rawViewId > 0)
      {
        var storedView = doc.GetElement(new ElementId((int)rawViewId)) as View3D;
        if (storedView != null && !storedView.IsTemplate)
        {
          Log.Information("FocusPin best view resolved from stored RevitViewId. viewId={ViewId} viewName='{ViewName}'",
            storedView.Id.GetIntValue(),
            storedView.Name ?? string.Empty);
          return storedView;
        }
      }

      var active3D = uiDoc.ActiveView as View3D;
      if (active3D != null && !active3D.IsTemplate)
      {
        Log.Information("FocusPin best view resolved from active 3D view. viewId={ViewId} viewName='{ViewName}'",
          active3D.Id.GetIntValue(),
          active3D.Name ?? string.Empty);
        return active3D;
      }

      var fallback = doc.GetOpenProjectView(CameraType.Orthogonal);
      Log.Information("FocusPin best view resolved from OpenProject fallback view. viewId={ViewId} viewName='{ViewName}'",
        fallback?.Id?.GetIntValue() ?? -1,
        fallback?.Name ?? string.Empty);
      return fallback;
    }

    private static bool ApplyBestCameraPosition(Document doc, View3D view, List<ElementId> elementIds, string workPackageId)
    {
      if (doc == null || view == null || elementIds == null || elementIds.Count == 0)
        return false;

      if (!TryComputeBoundingBox(doc, view, elementIds, out var min, out var max))
      {
        Log.Warning("FocusPin camera skipped because bounding box could not be computed. workPackageId={WorkPackageId}",
          workPackageId ?? string.Empty);
        return false;
      }

      var center = new XYZ(
        (min.X + max.X) / 2.0,
        (min.Y + max.Y) / 2.0,
        (min.Z + max.Z) / 2.0);
      var size = max - min;
      var diagonal = size.GetLength();
      var targetDistance = Math.Max(diagonal * 2.25, 10.0);

      var currentOrientation = view.GetOrientation();
      var forward = ComputeBestFocusForward(size, currentOrientation?.ForwardDirection);
      var up = ComputeBestFocusUp(forward, currentOrientation?.UpDirection);
      var eye = center - forward.Multiply(targetDistance);

      Log.Information(
        "FocusPin camera computed. workPackageId={WorkPackageId} center=({CenterX:F3},{CenterY:F3},{CenterZ:F3}) size=({SizeX:F3},{SizeY:F3},{SizeZ:F3}) diagonal={Diagonal:F3} distance={Distance:F3} forward=({ForwardX:F3},{ForwardY:F3},{ForwardZ:F3}) up=({UpX:F3},{UpY:F3},{UpZ:F3})",
        workPackageId ?? string.Empty,
        center.X, center.Y, center.Z,
        size.X, size.Y, size.Z,
        diagonal,
        targetDistance,
        forward.X, forward.Y, forward.Z,
        up.X, up.Y, up.Z);

      try
      {
        using var tx = new Transaction(doc, "OpenProject Focus Pin Camera");
        if (tx.Start() != TransactionStatus.Started)
        {
          Log.Warning("FocusPin camera skipped because transaction could not start. workPackageId={WorkPackageId}",
            workPackageId ?? string.Empty);
          return false;
        }

        view.SetOrientation(new ViewOrientation3D(eye, up, forward));
        tx.Commit();

        Log.Information(
          "FocusPin camera applied. workPackageId={WorkPackageId} eye=({EyeX:F3},{EyeY:F3},{EyeZ:F3}) up=({UpX:F3},{UpY:F3},{UpZ:F3}) viewId={ViewId} viewName='{ViewName}'",
          workPackageId ?? string.Empty,
          eye.X, eye.Y, eye.Z,
          up.X, up.Y, up.Z,
          view.Id.GetIntValue(),
          view.Name ?? string.Empty);
        return true;
      }
      catch (Exception ex)
      {
        Log.Error(ex, "FocusPin camera application failed. workPackageId={WorkPackageId}", workPackageId ?? string.Empty);
        return false;
      }
    }

    private static XYZ ComputeBestFocusForward(XYZ size, XYZ currentForward)
    {
      var horizontal = new XYZ(currentForward?.X ?? 1.0, currentForward?.Y ?? -1.0, 0.0);
      if (horizontal.GetLength() < 1e-6)
        horizontal = new XYZ(1.0, -1.0, 0.0);

      horizontal = horizontal.Normalize();

      var longestHorizontal = Math.Max(size?.X ?? 0.0, size?.Y ?? 0.0);
      var vertical = Math.Max(size?.Z ?? 0.0, 0.0);
      var verticalBias = longestHorizontal <= 1e-6
        ? 0.35
        : Math.Max(0.25, Math.Min(0.60, 0.25 + (vertical / longestHorizontal) * 0.20));

      return NormalizeOrDefault(new XYZ(horizontal.X, horizontal.Y, -verticalBias), new XYZ(1, -1, -0.35));
    }

    private static XYZ ComputeBestFocusUp(XYZ forward, XYZ currentUp)
    {
      var worldUp = NormalizeOrDefault(currentUp, XYZ.BasisZ);
      var right = forward.CrossProduct(worldUp);
      if (right.GetLength() < 1e-6)
        right = forward.CrossProduct(XYZ.BasisX);

      right = NormalizeOrDefault(right, XYZ.BasisY);
      var up = right.CrossProduct(forward);
      return NormalizeOrDefault(up, XYZ.BasisZ);
    }


    private static bool TryZoomToSelectedElements(UIDocument uiDoc, View view, List<ElementId> elementIds, string workPackageId)
    {
      if (uiDoc?.Document == null || view == null || elementIds == null || elementIds.Count == 0)
        return false;

      if (!TryComputeBoundingBox(uiDoc.Document, view, elementIds, out var min, out var max))
      {
        Log.Warning("FocusPin zoom skipped because bounding box could not be computed. workPackageId={WorkPackageId}",
          workPackageId ?? string.Empty);
        return false;
      }

      var uiView = uiDoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id);
      if (uiView == null)
      {
        Log.Warning("FocusPin zoom skipped because no matching UIView was found. workPackageId={WorkPackageId} viewId={ViewId}",
          workPackageId ?? string.Empty,
          view.Id?.GetIntValue() ?? -1);
        return false;
      }

      var center = new XYZ(
        (min.X + max.X) / 2.0,
        (min.Y + max.Y) / 2.0,
        (min.Z + max.Z) / 2.0);

      var right = NormalizeOrDefault(view.RightDirection, XYZ.BasisX);
      var up = NormalizeOrDefault(view.UpDirection, XYZ.BasisZ);

      var corners = new[]
      {
        new XYZ(min.X, min.Y, min.Z),
        new XYZ(min.X, min.Y, max.Z),
        new XYZ(min.X, max.Y, min.Z),
        new XYZ(min.X, max.Y, max.Z),
        new XYZ(max.X, min.Y, min.Z),
        new XYZ(max.X, min.Y, max.Z),
        new XYZ(max.X, max.Y, min.Z),
        new XYZ(max.X, max.Y, max.Z)
      };

      var halfWidth = 0.0;
      var halfHeight = 0.0;
      foreach (var corner in corners)
      {
        var delta = corner - center;
        halfWidth = Math.Max(halfWidth, Math.Abs(delta.DotProduct(right)));
        halfHeight = Math.Max(halfHeight, Math.Abs(delta.DotProduct(up)));
      }

      halfWidth = Math.Max(halfWidth, 1.0) * 1.35;
      halfHeight = Math.Max(halfHeight, 1.0) * 1.35;

      var topRight = center + right.Multiply(halfWidth) + up.Multiply(halfHeight);
      var bottomLeft = center - right.Multiply(halfWidth) - up.Multiply(halfHeight);

      try
      {
        uiView.ZoomAndCenterRectangle(topRight, bottomLeft);
        Log.Information(
          "FocusPin zoom applied. workPackageId={WorkPackageId} center=({CenterX:F3},{CenterY:F3},{CenterZ:F3}) halfWidth={HalfWidth:F3} halfHeight={HalfHeight:F3} viewId={ViewId} viewName='{ViewName}'",
          workPackageId ?? string.Empty,
          center.X, center.Y, center.Z,
          halfWidth, halfHeight,
          view.Id.GetIntValue(),
          view.Name ?? string.Empty);
        return true;
      }
      catch (Exception ex)
      {
        Log.Error(ex, "FocusPin zoom application failed. workPackageId={WorkPackageId}", workPackageId ?? string.Empty);
        return false;
      }
    }

    private static bool TryComputeBoundingBox(Document doc, View view, IEnumerable<ElementId> elementIds, out XYZ min, out XYZ max)
    {
      min = null;
      max = null;

      foreach (var id in elementIds)
      {
        var element = doc.GetElement(id);
        if (element == null)
          continue;

        var bbox = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
        if (bbox == null)
          continue;

        min = min == null
          ? bbox.Min
          : new XYZ(
            Math.Min(min.X, bbox.Min.X),
            Math.Min(min.Y, bbox.Min.Y),
            Math.Min(min.Z, bbox.Min.Z));

        max = max == null
          ? bbox.Max
          : new XYZ(
            Math.Max(max.X, bbox.Max.X),
            Math.Max(max.Y, bbox.Max.Y),
            Math.Max(max.Z, bbox.Max.Z));
      }

      return min != null && max != null;
    }

    private static XYZ NormalizeOrDefault(XYZ vector, XYZ fallback)
    {
      var candidate = vector;
      if (candidate == null || candidate.GetLength() <= 1e-9)
        candidate = fallback;

      var length = candidate.GetLength();
      if (length <= 1e-9)
        return fallback;

      return new XYZ(candidate.X / length, candidate.Y / length, candidate.Z / length);
    }

  }
}



