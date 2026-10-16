using System.Text.RegularExpressions;
using Serilog;

namespace OpenProject.Browser.Services
{
  public static class WorkPackageIdResolver
  {
    private static readonly Regex _regex = new Regex(@"/work_packages/(\d+)", RegexOptions.Compiled);

    public static string FromUrl(string url)
    {
      if (string.IsNullOrWhiteSpace(url))
      {
        Log.Warning("WorkPackageIdResolver.FromUrl received empty url");
        return null;
      }
      var match = _regex.Match(url);
      if (!match.Success)
      {
        Log.Warning("WorkPackageIdResolver.FromUrl did not match url={Url}", url);
        return null;
      }

      var id = match.Groups[1].Value;
      Log.Information("WorkPackageIdResolver.FromUrl matched workPackageId={WorkPackageId} url={Url}", id, url);
      return id;
    }
  }
}
