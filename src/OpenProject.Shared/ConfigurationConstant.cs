using System;
using System.IO;

namespace OpenProject.Shared
{
  public static class ConfigurationConstant
  {
    private const string _openProjectRevitAddInFolderName = "OpenProject.Revit";

    private const string _openProjectBrowserFolderName = "OpenProject.Browser";

    private const string _openProjectBrowserExecutableName = "OpenProject.Browser.exe";

    public static readonly string OpenProjectApplicationData =
      Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        _openProjectRevitAddInFolderName);

    public static readonly string OpenProjectBrowserExecutablePath =
      Path.Combine(_openProjectBrowserFolderName, _openProjectBrowserExecutableName);

    public static readonly string OpenProjectTempDirectory =
      Path.Combine(Path.GetTempPath(), _openProjectRevitAddInFolderName);
  }
}
