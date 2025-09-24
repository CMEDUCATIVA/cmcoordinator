using System.Collections.Generic;
using OpenProject.Browser.Models;
using Xunit;

namespace OpenProject.Tests.Models
{
  public class VersionTests
  {
    public static IEnumerable<object[]> DataForParse()
    {
      yield return new object[] { "0.1.0", "v0.1.0" };
      yield return new object[] { "abc", "v0.0.0" };
      yield return new object[] { "v2.42.1337", "v2.42.1337" };
      yield return new object[] { "V2.42.1337", "v2.42.1337" };
      yield return new object[] { "ver2.42.1337", "v0.0.0" };
      yield return new object[] { "0.91.2-beta3", "v0.91.2-beta3" };
      yield return new object[] { "v0.91.3-b4ef3a", "v0.91.3-b4ef3a" };
      yield return new object[] { "0.91.3.1107", "v0.91.3-1107" };
    }

    [Theory]
    [MemberData(nameof(DataForParse))]
    public void Parse_ReturnsExpectedVersion(string versionString, string expectedVersion)
    {
      // Act
      Version version = Version.Parse(versionString);

      // Assert
      Assert.Equal(expectedVersion, version.ToString());
    }

    public static IEnumerable<object[]> DataForCompare()
    {
      yield return new object[] { Version.Parse("1.0.0"), Version.Parse("0.1.0"), 1 };
      yield return new object[] { Version.Parse("1.0.0"), Version.Parse("1.1.0"), -1 };
      yield return new object[] { Version.Parse("1.0.0"), Version.Parse("1.0.0"), 0 };
      yield return new object[] { Version.Parse("1.1.13"), Version.Parse("1.1.3"), 1 };
      yield return new object[] { Version.Parse("1.1.13"), Version.Parse("1.1.13-beta3"), -1 };
      yield return new object[] { Version.Parse("1.1.3-abc"), Version.Parse("1.1.3-cba"), -1 };
      yield return new object[] { Version.Parse("1.1.3-b3ee44"), Version.Parse("1.1.3-02aa3e"), 1 };
      yield return new object[] { Version.Parse("1.1.3-0006"), Version.Parse("1.1.3-0007"), -1 };
      yield return new object[] { Version.Parse("1.1.3-0006"), Version.Parse("1.1.3-00007"), 1 };
    }

    [Theory]
    [MemberData(nameof(DataForCompare))]
    public void Compare_FindsTheBiggerVersion(Version version1, Version version2, int expected)
    {
      // Act/Assert
      Assert.Equal(expected, version1.CompareTo(version2));
    }
  }
}
