using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using OpenProject.Shared;
using Serilog;

namespace OpenProject.Browser.WebViewIntegration
{
  public static class EmbeddedLandingPageHandler
  {
    public static string GetEmbeddedLandingPageIndexUrl()
    {
      Log.Debug("Fetching landing page url ...");
      var indexFilePath = GetIndexFilePath();

      Log.Debug("Fetched landing page url (file:///{url}).", indexFilePath);
      return $"file:///{indexFilePath}";
    }

    private static string _landingPageZipHash;

    private static string GetLandingPageZipHash()
    {
      if (!string.IsNullOrWhiteSpace(_landingPageZipHash))
      {
        return _landingPageZipHash;
      }

      using Stream zipStream = GetEmbeddedResourceZipStream();
      using var md5 = MD5.Create();
      _landingPageZipHash = md5.ComputeHash(zipStream)
        .Select(b => $"{b:x2}")
        .Aggregate((c, n) => c + n);
      return _landingPageZipHash;
    }

    private static string GetIndexFilePath()
    {
      // The unpacked Html should be adjacent to this Dll
      var currentAssemblyPathUri = Assembly.GetExecutingAssembly().CodeBase;
      var currentAssemblyPath = Uri.UnescapeDataString(new Uri(currentAssemblyPathUri).AbsolutePath)
        // '/' comes from the uri, we need it to be '\' for the path
        .Replace("/", "\\");
      var currentFolder = Path.GetDirectoryName(currentAssemblyPath);
      // We're versioning the folder so as to not have to do a direct file comparison of the contents
      // in case an earlier version was already present

      // The hash is used to ensure that when working locally in a debugging environment, any changes to
      // the zip files content ensure that the current version of the landing page is extracted.
      var landingPageZipHash = GetLandingPageZipHash();
      var landingPageFolder = Path.Combine(currentFolder, "LandingPage", VersionsService.Version, landingPageZipHash);
      if (!Directory.Exists(landingPageFolder))
      {
        ExtractEmbeddedLandingPageToFolder(landingPageFolder);
      }

      return Path.Combine(landingPageFolder, "index.html");
    }

    private static void ExtractEmbeddedLandingPageToFolder(string landingPageFolder)
    {
      var tempPath = Path.GetTempFileName();
      using Stream resourceStream = GetEmbeddedResourceZipStream();
      using FileStream fs = File.Create(tempPath);
      resourceStream.CopyTo(fs);
      fs.Close();

      ZipFile.ExtractToDirectory(tempPath, landingPageFolder);
    }

    private static Stream GetEmbeddedResourceZipStream()
    {
      const string resourceName = "OpenProject.Browser.WebViewIntegration.LandingPage.LandingPage.zip";
      return Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
    }
  }
}
