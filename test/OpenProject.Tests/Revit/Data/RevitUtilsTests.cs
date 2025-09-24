using System;
using System.Collections.Generic;
using OpenProject.Revit.Data;
using OpenProject.Shared.Math3D;
using Xunit;

namespace OpenProject.Tests.Revit.Data
{
  public class RevitUtilsTests
  {
    public static IEnumerable<object[]> TransformCameraPositionTestDta()
    {
      // zero vectors must not change, independent of reverse flag
      yield return new object[]
      {
        new ProjectPositionWrapper(0, 0, 0, 0),
        new Position(),
        new Position(),
        true
      };

      // zero project position must result in unchanged transform position, independent of reverse flag
      yield return new object[]
      {
        new ProjectPositionWrapper(0, 0, 0, 0),
        new Position(new Vector3(1, 1, 1), new Vector3(0, 0, 1), new Vector3(1, 0, 0)),
        new Position(new Vector3(1, 1, 1), new Vector3(0, 0, 1), new Vector3(1, 0, 0)),
        false
      };

      // a simple camera position must get transformed into global coordinates
      yield return new object[]
      {
        new ProjectPositionWrapper(10, 10, 5, Convert.ToDecimal(Math.PI * 0.5)),
        new Position(new Vector3(1, 1, 1), new Vector3(0, 0, 1), new Vector3(1, 0, 0)),
        new Position(new Vector3(9, 11, 6), new Vector3(0, 0, 1), new Vector3(0, 1, 0)),
        false
      };

      // a camera position must get transformed into project position coordinates
      yield return new object[]
      {
        new ProjectPositionWrapper(247, -107, -1, Convert.ToDecimal(Math.PI * 0.5)),
        new Position(new Vector3(213.2m, -117.75m, 12), new Vector3(0.5m, -0.5m, 0), new Vector3(0, 0, 1)),
        new Position(new Vector3(-10.75m, 33.8m, 13), new Vector3(-0.5m, -0.5m, 0), new Vector3(0, 0, 1)),
        true
      };
    }

    [Theory]
    [MemberData(nameof(TransformCameraPositionTestDta))]
    public void TransformCameraPosition_ReturnsExpectedPosition(
      ProjectPositionWrapper projectBase,
      Position initialCamera,
      Position expectedCamera,
      bool reverse)
    {
      // Arrange / Act
      Position cameraPosition = RevitUtils.TransformCameraPosition(projectBase, initialCamera, reverse);

      // Assert
      Assert.Equal(expectedCamera.Center.X, cameraPosition.Center.X, 10);
      Assert.Equal(expectedCamera.Center.Y, cameraPosition.Center.Y, 10);
      Assert.Equal(expectedCamera.Center.Z, cameraPosition.Center.Z, 10);

      Assert.Equal(expectedCamera.Forward.X, cameraPosition.Forward.X, 10);
      Assert.Equal(expectedCamera.Forward.Y, cameraPosition.Forward.Y, 10);
      Assert.Equal(expectedCamera.Forward.Z, cameraPosition.Forward.Z, 10);

      Assert.Equal(expectedCamera.Up.X, cameraPosition.Up.X, 10);
      Assert.Equal(expectedCamera.Up.Y, cameraPosition.Up.Y, 10);
      Assert.Equal(expectedCamera.Up.Z, cameraPosition.Up.Z, 10);
    }
  }
}
