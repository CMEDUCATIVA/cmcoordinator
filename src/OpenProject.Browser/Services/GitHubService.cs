using System.Net;
using OpenProject.Browser.Models;
using OpenProject.Shared;
using Optional;
using RestSharp;

namespace OpenProject.Browser.Services
{
  public sealed class GitHubService : IGitHubService
  {
    private RestClient _client;

    private RestClient Client
    {
      get
      {
        if (_client != null)
          return _client;

        _client = new RestClient(@"https://api.github.com/");
        return _client;
      }
    }

    /// <inheritdoc />
    public Option<Release> GetLatestRelease()
    {
      var request =
        new RestRequest($"repos/{RepositoryInfo.GitHubOwner}/{RepositoryInfo.GitHubRepository}/releases/latest", Method.GET);
      request.AddHeader("Content-Type", "application/json");
      request.RequestFormat = DataFormat.Json;
      request.OnBeforeDeserialization = resp => { resp.ContentType = "application/json"; };

      var response = Client.Execute<Release>(request);

      return response.StatusCode == HttpStatusCode.OK ? response.Data.SomeNotNull() : Option.None<Release>();
    }
  }
}
