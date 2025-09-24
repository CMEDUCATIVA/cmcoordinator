using System;
using Serilog;

namespace OpenProject.Browser.Models
{
  /// <summary>
  /// Immutable class representing semantic versions.
  /// </summary>
  public sealed class Version : IComparable<Version>
  {
    private readonly int _major;
    private readonly int _minor;
    private readonly int _patch;
    private readonly string _suffix;

    private Version(int major, int minor, int patch, string suffix)
    {
      _major = major;
      _minor = minor;
      _patch = patch;
      _suffix = suffix;
    }

    /// <summary>
    /// Parses a string into a semantic version. Returns 'v0.0.0' if an invalid string is given.
    /// </summary>
    /// <param name="versionString">The input string</param>
    /// <returns>A semantic version object</returns>
    public static Version Parse(string versionString)
    {
      var separators = new[] { '.', '-' };
      var split = versionString.Split(separators);

      var major = 0;
      var minor = 0;
      var patch = 0;
      var suffix = "";

      try
      {
        if (split.Length >= 1)
        {
          var str = split[0].StartsWith("v", StringComparison.InvariantCultureIgnoreCase) ? split[0][1..] : split[0];
          major = int.Parse(str);
        }

        if (split.Length >= 2)
          minor = int.Parse(split[1]);
        if (split.Length >= 3)
          patch = int.Parse(split[2]);
        if (split.Length >= 4)
          suffix = split[3];
      }
      catch (FormatException)
      {
        Log.Error("{version} is no valid version string.", versionString);
        return new Version(0, 0, 0, "");
      }

      return new Version(major, minor, patch, suffix);
    }

    /// <inheritdoc />
    public int CompareTo(Version other)
    {
      if (ReferenceEquals(this, other)) return 0;
      if (ReferenceEquals(null, other)) return 1;

      if (_major > other._major)
        return 1;
      if (_major < other._major)
        return -1;

      if (_minor > other._minor)
        return 1;
      if (_minor < other._minor)
        return -1;

      if (_patch > other._patch)
        return 1;
      if (_patch < other._patch)
        return -1;

      var stringComparison = string.CompareOrdinal(_suffix, other._suffix);
      return stringComparison > 0 ? 1 : stringComparison < 0 ? -1 : 0;
    }

    /// <inheritdoc />
    public override string ToString() =>
      _suffix.Length > 0 ? $"v{_major}.{_minor}.{_patch}-{_suffix}" : $"v{_major}.{_minor}.{_patch}";
  }
}
