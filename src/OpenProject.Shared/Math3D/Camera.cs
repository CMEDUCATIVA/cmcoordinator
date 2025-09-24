using System;
using OpenProject.Shared.Math3D.Enumeration;

namespace OpenProject.Shared.Math3D
{
  /// <summary>
  /// A camera wrapper using decimal precision.
  /// </summary>
  public abstract class Camera : IEquatable<Camera>
  {
    /// <summary>
    /// The camera location and orientation.
    /// </summary>
    public Position Position { get; set; } = new Position();

    /// <summary>
    /// The camera type, which can be orthogonal or perspective.
    /// </summary>
    public abstract CameraType Type { get; }

    /// <inheritdoc />
    public virtual bool Equals(Camera other)
    {
      if (other == null) return false;

      return Position.Equals(other.Position) &&
             Type == other.Type;
    }
  }
}
