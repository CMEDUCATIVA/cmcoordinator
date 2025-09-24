using System.Collections.Generic;
using iabi.BCF.APIObjects.V21;
using OpenProject.Shared.Math3D;
using Optional;

namespace OpenProject.Shared.BcfApi
{
  /// <summary>
  /// A view model for BCF viewpoints.
  /// </summary>
  public sealed class BcfViewpointWrapper
  {
    public Viewpoint_GET Viewpoint { get; set; }

    public Components Components { get; set; }

    /// <summary>
    /// Gets the camera of the BCF viewpoint. It returns the value within an optional, which is None, if the BCF
    /// viewpoint has no camera set.
    /// </summary>
    /// <returns>The optional containing the camera.</returns>
    public Option<Camera> GetCamera()
    {
      Camera camera = null;

      if (Viewpoint?.Perspective_camera != null)
      {
        var c = new PerspectiveCamera();
        Perspective_camera bcfPerspective = Viewpoint.Perspective_camera;

        c.FieldOfView = bcfPerspective.Field_of_view.ToDecimal();
        c.Position = new Position(
          bcfPerspective.Camera_view_point.ToVector3(),
          bcfPerspective.Camera_direction.ToVector3(),
          bcfPerspective.Camera_up_vector.ToVector3());

        camera = c;
      }

      if (Viewpoint?.Orthogonal_camera != null)
      {
        var c = new OrthogonalCamera();
        Orthogonal_camera bcfOrthogonal = Viewpoint.Orthogonal_camera;

        c.ViewToWorldScale = bcfOrthogonal.View_to_world_scale.ToDecimal();
        c.Position = new Position(
          bcfOrthogonal.Camera_view_point.ToVector3(),
          bcfOrthogonal.Camera_direction.ToVector3(),
          bcfOrthogonal.Camera_up_vector.ToVector3());

        camera = c;
      }

      return camera.SomeNotNull();
    }

    /// <summary>
    /// Gets the list of visibility exceptions or an empty list if any path element is null.
    /// </summary>
    public IEnumerable<Component> GetVisibilityExceptions() =>
      Components?.Visibility?.Exceptions ?? new List<Component>();

    /// <summary>
    /// Gets the visibility default, or false, if any path element is null.
    /// </summary>
    public bool GetVisibilityDefault() => Components?.Visibility?.Default_visibility ?? false;

    /// <summary>
    /// Gets the list of selected components or an empty list if any path element is null.
    /// </summary>
    public IEnumerable<Component> GetSelection() => Components?.Selection ?? new List<Component>();

    /// <summary>
    /// Gets the list of viewpoint clipping planes or an empty list if any path element is null.
    /// </summary>
    public IEnumerable<Clipping_plane> GetClippingPlanes() => Viewpoint?.Clipping_planes ?? new List<Clipping_plane>();
  }
}
