using System;
using System.Collections.Generic;
using Castle.Core.Internal;
using iabi.BCF.APIObjects.V21;
using OpenProject.Shared.BcfApi;
using OpenProject.Shared.Math3D;
using Xunit;

namespace OpenProject.Tests.Shared.Math3D
{
  public class BcfApiExtensionsTests
  {
    public static IEnumerable<object[]> DataForToClippingPlane()
    {
      yield return new object[]
      {
        AxisAlignedBoundingBox.Infinite,
        null,
        new List<Clipping_plane>()
      };

      yield return new object[]
      {
        new AxisAlignedBoundingBox(Vector3.InfiniteMin, new Vector3(decimal.MaxValue, decimal.MaxValue, 42)),
        Vector3.Zero,
        new List<Clipping_plane>
        {
          new Clipping_plane
          {
            Direction = new Direction { X = 0, Y = 0, Z = 1 },
            Location = new Location { X = 0, Y = 0, Z = 42 }
          }
        }
      };

      yield return new object[]
      {
        new AxisAlignedBoundingBox(Vector3.Zero, new Vector3(1, 1, 1)),
        null,
        new List<Clipping_plane>
        {
          new Clipping_plane
          {
            Direction = new Direction { X = -1, Y = 0, Z = 0 },
            Location = new Location { X = 0, Y = 0.5f, Z = 0.5f }
          },
          new Clipping_plane
          {
            Direction = new Direction { X = 0, Y = -1, Z = 0 },
            Location = new Location { X = 0.5f, Y = 0, Z = 0.5f }
          },
          new Clipping_plane
          {
            Direction = new Direction { X = 0, Y = 0, Z = -1 },
            Location = new Location { X = 0.5f, Y = 0.5f, Z = 0 }
          },
          new Clipping_plane
          {
            Direction = new Direction { X = 1, Y = 0, Z = 0 },
            Location = new Location { X = 1, Y = 0.5f, Z = 0.5f }
          },
          new Clipping_plane
          {
            Direction = new Direction { X = 0, Y = 1, Z = 0 },
            Location = new Location { X = 0.5f, Y = 1, Z = 0.5f }
          },
          new Clipping_plane
          {
            Direction = new Direction { X = 0, Y = 0, Z = 1 },
            Location = new Location { X = 0.5f, Y = 0.5f, Z = 1 }
          }
        }
      };

      yield return new object[]
      {
        new AxisAlignedBoundingBox(Vector3.Zero, new Vector3(1, 1, 1)),
        new Vector3(0.2m, 0.2m, 0.2m),
        new List<Clipping_plane>
        {
          new Clipping_plane
          {
            Direction = new Direction { X = -1, Y = 0, Z = 0 },
            Location = new Location { X = 0, Y = 0.2f, Z = 0.2f }
          },
          new Clipping_plane
          {
            Direction = new Direction { X = 0, Y = -1, Z = 0 },
            Location = new Location { X = 0.2f, Y = 0, Z = 0.2f }
          },
          new Clipping_plane
          {
            Direction = new Direction { X = 0, Y = 0, Z = -1 },
            Location = new Location { X = 0.2f, Y = 0.2f, Z = 0 }
          },
          new Clipping_plane
          {
            Direction = new Direction { X = 1, Y = 0, Z = 0 },
            Location = new Location { X = 1, Y = 0.2f, Z = 0.2f }
          },
          new Clipping_plane
          {
            Direction = new Direction { X = 0, Y = 1, Z = 0 },
            Location = new Location { X = 0.2f, Y = 1, Z = 0.2f }
          },
          new Clipping_plane
          {
            Direction = new Direction { X = 0, Y = 0, Z = 1 },
            Location = new Location { X = 0.2f, Y = 0.2f, Z = 1 }
          }
        }
      };
    }


    [Theory]
    [MemberData(nameof(DataForToClippingPlane))]
    public void AxisAlignedBoundingBox_ToClippingPlanes_ConvertsAabbToCorrectListOfPlanes(
      AxisAlignedBoundingBox box, Vector3 center, List<Clipping_plane> expected)
    {
      // Act
      var planes = box.ToClippingPlanes(center);

      // Assert
      Assert.Equal(expected.Count, planes.Count);

      foreach (Clipping_plane plane in planes)
        Assert.Contains(expected, p =>
          Math.Abs(plane.Direction.X - p.Direction.X) < 0.01 &&
          Math.Abs(plane.Direction.Y - p.Direction.Y) < 0.01 &&
          Math.Abs(plane.Direction.Z - p.Direction.Z) < 0.01 &&
          Math.Abs(plane.Location.X - p.Location.X) < 0.01 &&
          Math.Abs(plane.Location.Y - p.Location.Y) < 0.01 &&
          Math.Abs(plane.Location.Z - p.Location.Z) < 0.01);
    }
  }
}
