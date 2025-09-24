using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using iabi.BCF.APIObjects.V21;
using OpenProject.Revit.Data;
using OpenProject.Shared;
using OpenProject.Shared.BcfApi;
using OpenProject.Shared.Math3D;
using OpenProject.Shared.Math3D.Enumeration;

namespace OpenProject.Revit.Extensions
{
  /// <summary>
  /// A namespace for static extensions for Revit document classes with focus on getting BCF data out
  /// of the current state.
  /// </summary>
  public static class BcfExtensions
  {
    /// <summary>
    /// Generates a model meant to send a viewpoint creation request to the BCF API from a Revit UI document.
    /// </summary>
    /// <param name="uiDocument">The Revit UI document</param>
    /// <returns>A BCF model for creating viewpoints.</returns>
    public static Viewpoint_POST GenerateViewpoint(this UIDocument uiDocument)
    {
      (Orthogonal_camera ortho, Perspective_camera perspective) = uiDocument.GetBcfCameraValues();

      return new Viewpoint_POST
      {
        Clipping_planes = uiDocument.GetBcfClippingPlanes(),
        Snapshot = uiDocument.GetBcfSnapshotData(),
        Components = uiDocument.GetBcfComponents(),
        Orthogonal_camera = ortho,
        Perspective_camera = perspective
      };
    }

    private static List<Clipping_plane> GetBcfClippingPlanes(this UIDocument uiDocument)
    {
      if (uiDocument.ActiveView is not View3D view3D)
        return new List<Clipping_plane>();

      BoundingBoxXYZ sectionBox = view3D.GetSectionBox();
      XYZ transformedMin = sectionBox.Transform.OfPoint(sectionBox.Min);
      XYZ transformedMax = sectionBox.Transform.OfPoint(sectionBox.Max);
      Vector3 minCorner = transformedMin.ToVector3().ToMeters();
      Vector3 maxCorner = transformedMax.ToVector3().ToMeters();

      return new AxisAlignedBoundingBox(minCorner, maxCorner).ToClippingPlanes();
    }

    private static Snapshot_POST GetBcfSnapshotData(this UIDocument uiDocument)
    {
      return new Snapshot_POST
      {
        Snapshot_type = Snapshot_type.Png,
        Snapshot_data = "data:image/png;base64," + uiDocument.GetBase64RevitSnapshot()
      };
    }

    private static Components GetBcfComponents(this UIDocument uiDocument)
    {
      var hiddenElementsOfView = uiDocument.Document.GetHiddenElementsOfView(uiDocument.ActiveView).ToList();
      var visibleElementsOfView = uiDocument.Document.GetVisibleElementsOfView(uiDocument.ActiveView).ToList();

      var defaultVisibility = visibleElementsOfView.Count >= hiddenElementsOfView.Count;
      var exceptions = (defaultVisibility ? hiddenElementsOfView : visibleElementsOfView)
        .Select(uiDocument.Document.ElementIdToComponentSelector())
        .ToList();

      var selectedComponents = uiDocument.Selection.GetElementIds()
        .Select(uiDocument.Document.ElementIdToComponentSelector())
        .ToList();

      return new Components
      {
        Selection = selectedComponents,
        Visibility = new iabi.BCF.APIObjects.V21.Visibility
        {
          Default_visibility = defaultVisibility,
          Exceptions = exceptions
        }
      };
    }

    private static (Orthogonal_camera orthogonalCamera, Perspective_camera perspectiveCamera) GetBcfCameraValues(
      this UIDocument uiDocument)
    {
      if (uiDocument.ActiveView is not View3D view3D)
        return (null, null);

      CameraType cameraType = view3D.IsPerspective ? CameraType.Perspective : CameraType.Orthogonal;
      Position cameraPosition = uiDocument.GetCameraPosition(view3D.IsPerspective);
      Vector3 center = cameraPosition.Center.ToMeters();

      var cameraViewpoint = new iabi.BCF.APIObjects.V21.Point
      {
        X = Convert.ToSingle(center.X),
        Y = Convert.ToSingle(center.Y),
        Z = Convert.ToSingle(center.Z)
      };

      var cameraUpVector = new Direction
      {
        X = Convert.ToSingle(cameraPosition.Up.X),
        Y = Convert.ToSingle(cameraPosition.Up.Y),
        Z = Convert.ToSingle(cameraPosition.Up.Z)
      };

      var cameraDirection = new Direction
      {
        X = Convert.ToSingle(cameraPosition.Forward.X * -1),
        Y = Convert.ToSingle(cameraPosition.Forward.Y * -1),
        Z = Convert.ToSingle(cameraPosition.Forward.Z * -1)
      };

      Orthogonal_camera ortho = null;
      Perspective_camera perspective = null;

      switch (cameraType)
      {
        case CameraType.Perspective:
          perspective = new Perspective_camera
          {
            Field_of_view = 45, // revit default value
            Camera_direction = cameraDirection,
            Camera_up_vector = cameraUpVector,
            Camera_view_point = cameraViewpoint
          };
          break;
        case CameraType.Orthogonal:
          ortho = new Orthogonal_camera
          {
            View_to_world_scale = uiDocument.GetViewBoxHeight(),
            Camera_direction = cameraDirection,
            Camera_up_vector = cameraUpVector,
            Camera_view_point = cameraViewpoint
          };
          break;
        default:
          throw new ArgumentOutOfRangeException(nameof(cameraType), cameraType, "invalid camera type");
      }

      return (ortho, perspective);
    }

    private static float GetViewBoxHeight(this UIDocument uiDocument)
    {
      var zoomCorners = uiDocument.GetOpenUIViews()[0].GetZoomCorners();
      XYZ bottomLeft = zoomCorners[0];
      XYZ topRight = zoomCorners[1];
      var (viewBoxHeight, _) =
        RevitUtils.ConvertToViewBoxValues(topRight, bottomLeft, uiDocument.ActiveView.RightDirection);

      return Convert.ToSingle(viewBoxHeight.ToMeters());
    }

    private static Position GetCameraPosition(this UIDocument uiDocument, bool isPerspective)
    {
      ProjectPosition projectPosition = uiDocument.Document.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);
      var zoomCorners = uiDocument.GetOpenUIViews()[0].GetZoomCorners();
      XYZ bottomLeft = zoomCorners[0];
      XYZ topRight = zoomCorners[1];
      XYZ viewCenter = uiDocument.ActiveView.Origin;
      if (!isPerspective)
        viewCenter = new XYZ((topRight.X + bottomLeft.X) / 2,
          (topRight.Y + bottomLeft.Y) / 2,
          (topRight.Z + bottomLeft.Z) / 2);

      return RevitUtils.TransformCameraPosition(
        new ProjectPositionWrapper(projectPosition),
        new Position(
          viewCenter.ToVector3(),
          uiDocument.ActiveView.ViewDirection.ToVector3(),
          uiDocument.ActiveView.UpDirection.ToVector3()));
    }

    private static string GetBase64RevitSnapshot(this UIDocument uiDocument)
    {
      var tmpPath = ConfigurationConstant.OpenProjectTempDirectory;
      Directory.CreateDirectory(tmpPath);
      var tempImg = Path.Combine(tmpPath, Path.GetTempFileName() + ".png");
      var options = new ImageExportOptions
      {
        FilePath = tempImg,
        HLRandWFViewsFileType = ImageFileType.PNG,
        ShadowViewsFileType = ImageFileType.PNG,
        ExportRange = ExportRange.VisibleRegionOfCurrentView,
        ZoomType = ZoomFitType.FitToPage,
        ImageResolution = ImageResolution.DPI_72,
        PixelSize = 1000
      };
      uiDocument.Document.ExportImage(options);

      var bytes = File.ReadAllBytes(tempImg);
      File.Delete(tempImg);
      return Convert.ToBase64String(bytes);
    }
  }
}
