using System;

// ReSharper disable UnusedAutoPropertyAccessor.Global
// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable InconsistentNaming

namespace OpenProject.Browser.Models
{
  /// <summary>
  /// Class model for deserialization of github release JSON data from the github rest API.
  /// </summary>
  // ReSharper disable once ClassNeverInstantiated.Global
  public sealed class Release
  {
    public string tag_name { private get; set; }
    public DateTime published_at { private get; set; }
    public string html_url { private get; set; }

    public Version Version() => Models.Version.Parse(tag_name);

    public DateTime PublishedAt() => published_at;

    public string DownloadUrl() => html_url;
  }
}
