using OpenProject.Browser.Models;
using Optional;

namespace OpenProject.Browser.Services
{
  /// <summary>
  /// A service for communication to the repository on github.
  /// </summary>
  public interface IGitHubService
  {
    /// <summary>
    /// Get the latest published released of the OpenProject Revit AddIn.
    /// </summary>
    /// <returns>A release object.</returns>
    Option<Release> GetLatestRelease();
  }
}
