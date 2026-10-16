using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace OpenProject.Revit.Services.Updates
{
  public sealed class UpdateRelease
  {
    public string Tag { get; set; }
    public long AssetId { get; set; }
    public long Size { get; set; }
    public string Sha256 { get; set; }
    public string DownloadUrl { get; set; }
    public string Key => Tag + ":" + AssetId + ":" + Sha256;

    public static Version ParseVersion(string tag)
    {
      var value = (tag ?? "").TrimStart('v', 'V');
      if (!Regex.IsMatch(value, @"^\d+\.\d+\.\d+(\.\d+)?$") || !Version.TryParse(value, out var version))
        throw new InvalidOperationException("La etiqueta debe tener formato v2026.10.1 o v1.2.3.");
      return new Version(version.Major, version.Minor, version.Build, Math.Max(0, version.Revision));
    }

    public static UpdateRelease Parse(string json)
    {
      var data = JObject.Parse(json);
      if (data.Value<bool>("draft") || data.Value<bool>("prerelease")) return null;
      var tag = data.Value<string>("tag_name");
      ParseVersion(tag);
      var assets = (data["assets"] as JArray ?? new JArray()).OfType<JObject>().Where(a =>
        a.Value<string>("state") == "uploaded" &&
        (a.Value<string>("name") ?? "").StartsWith("Cmcoordinator", StringComparison.OrdinalIgnoreCase) &&
        (a.Value<string>("name") ?? "").EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList();
      if (assets.Count != 1)
        throw new InvalidOperationException("La publicación debe adjuntar un único instalador Cmcoordinator*.exe.");
      var asset = assets[0];
      var digest = asset.Value<string>("digest") ?? "";
      if (!Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$"))
        throw new InvalidOperationException("El instalador publicado no tiene un SHA256 verificable de GitHub.");
      var url = asset.Value<string>("browser_download_url");
      if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
          uri.Host != "github.com" || !uri.AbsolutePath.StartsWith(
            "/CMEDUCATIVA/cmcoordinator/releases/download/", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("El instalador no pertenece al repositorio oficial de Cmcoordinator.");
      var size = asset.Value<long>("size");
      if (size < 2 || size > 512L * 1024 * 1024) throw new InvalidOperationException("Tamaño de instalador no admitido.");
      return new UpdateRelease { Tag = tag, AssetId = asset.Value<long>("id"), Size = size,
        Sha256 = digest.Substring(7).ToLowerInvariant(), DownloadUrl = url };
    }

    public static string FileHash(string path)
    {
      using var sha = SHA256.Create();
      using var stream = File.OpenRead(path);
      return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    public static string FileHashOfText(string text)
    {
      using var sha = SHA256.Create();
      return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
    }
  }
}
