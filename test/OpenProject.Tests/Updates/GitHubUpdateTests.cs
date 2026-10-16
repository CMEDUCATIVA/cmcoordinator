using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using OpenProject.Revit.Services.Updates;
using Xunit;

namespace OpenProject.Tests.Updates
{
  public sealed class GitHubUpdateTests
  {
    private static string ReleaseJson(byte[] bytes)
    {
      using var sha = System.Security.Cryptography.SHA256.Create();
      var digest = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
      return new JObject { ["tag_name"] = "v2026.10.1", ["draft"] = false, ["prerelease"] = false,
        ["assets"] = new JArray(new JObject { ["id"] = 42, ["state"] = "uploaded", ["name"] = "Cmcoordinator-Setup.exe",
          ["size"] = bytes.Length, ["digest"] = "sha256:" + digest,
          ["browser_download_url"] = "https://github.com/CMEDUCATIVA/cmcoordinator/releases/download/v2026.10.1/setup.exe" }) }.ToString();
    }

    [Fact]
    public async Task NoReleaseIsDifferentFromRateLimitedOrOffline()
    {
      using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
      Assert.Null(await new GitHubUpdateClient(http).LatestAsync(CancellationToken.None));
      using var limited = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
      await Assert.ThrowsAsync<InvalidOperationException>(() => new GitHubUpdateClient(limited).LatestAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("digest", "")]
    [InlineData("browser_download_url", "https://example.com/setup.exe")]
    [InlineData("size", "999999999999")]
    public void RejectsUnverifiableOrForeignAsset(string field, string value)
    {
      var data = JObject.Parse(ReleaseJson(Encoding.ASCII.GetBytes("MZ-test")));
      data["assets"][0][field] = value;
      Assert.Throws<InvalidOperationException>(() => UpdateRelease.Parse(data.ToString()));
    }

    [Fact]
    public void StableReleaseRequiresInstallerAndNumericVersion()
    {
      var data = JObject.Parse(ReleaseJson(Encoding.ASCII.GetBytes("MZ-test")));
      data["assets"] = new JArray();
      Assert.Throws<InvalidOperationException>(() => UpdateRelease.Parse(data.ToString()));
      Assert.Throws<InvalidOperationException>(() => UpdateRelease.ParseVersion("../release"));
      Assert.Equal(UpdateRelease.ParseVersion("2026.10.1.0"), UpdateRelease.ParseVersion("v2026.10.1"));
      data["prerelease"] = true;
      Assert.Null(UpdateRelease.Parse(data.ToString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadVerifiesBytesAndDoesNotPublishCorruptFile(bool corrupt)
    {
      var folder = Path.Combine(Path.GetTempPath(), "cmcoordinator-update-test-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(folder);
      var file = Path.Combine(folder, "setup.exe");
      try
      {
        var bytes = Encoding.ASCII.GetBytes("MZ-valid-fixture");
        var release = UpdateRelease.Parse(ReleaseJson(bytes));
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
          { Content = new ByteArrayContent(corrupt ? Encoding.ASCII.GetBytes("MZ-wrong-fixture") : bytes) }));
        var task = new GitHubUpdateClient(http).DownloadAsync(release, file, null, CancellationToken.None);
        if (corrupt) { await Assert.ThrowsAsync<InvalidOperationException>(() => task); Assert.False(File.Exists(file)); }
        else { await task; Assert.Equal(bytes, File.ReadAllBytes(file)); }
        Assert.False(File.Exists(file + ".part"));
      }
      finally
      {
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(folder));
        Directory.Delete(folder, true);
      }
    }

    [Fact]
    public async Task CancellationNeverLeavesInstallableFile()
    {
      using var http = new HttpClient(new Handler(_ => throw new OperationCanceledException()));
      var release = UpdateRelease.Parse(ReleaseJson(Encoding.ASCII.GetBytes("MZ-test")));
      var file = Path.Combine(Path.GetTempPath(), "cmcoordinator-cancel-" + Guid.NewGuid().ToString("N") + ".exe");
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitHubUpdateClient(http).DownloadAsync(release, file, null, new CancellationToken(true)));
      Assert.False(File.Exists(file)); Assert.False(File.Exists(file + ".part"));
    }

    [Fact]
    public void DoesNotDowngradeToOlderRelease()
    {
      var release = UpdateRelease.Parse(ReleaseJson(Encoding.ASCII.GetBytes("MZ-test")));
      var assembly = typeof(GitHubUpdateTests).Assembly.Location;
      Assert.True(UpdateInstallation.IsCurrent(release, assembly, new Version(2027, 1, 1, 0)));
      Assert.False(UpdateInstallation.IsCurrent(release, assembly, new Version(2, 2, 2, 0)));
    }

    [Fact]
    public void ReceiptDetectsAssetReplacementAndIsIgnoredWhenInstalledBinaryDoesNotMatch()
    {
      var assembly = Path.Combine(Path.GetTempPath(), "cmcoordinator-receipt-" + Guid.NewGuid().ToString("N") + ".dll");
      File.WriteAllText(assembly, "fixture binary");
      Directory.CreateDirectory(UpdateInstallation.Root);
      var receipt = UpdateInstallation.ReceiptPath(assembly);
      try
      {
        var release = UpdateRelease.Parse(ReleaseJson(Encoding.ASCII.GetBytes("MZ-test")));
        File.WriteAllText(receipt, new JObject { ["Tag"] = release.Tag, ["Key"] = release.Key,
          ["AssemblyHash"] = UpdateRelease.FileHash(assembly) }.ToString());
        Assert.True(UpdateInstallation.IsCurrent(release, assembly, new Version(2, 2, 2)));
        release.AssetId++;
        Assert.False(UpdateInstallation.IsCurrent(release, assembly, new Version(2, 2, 2)));
        File.AppendAllText(assembly, "changed outside installer");
        release.AssetId--;
        Assert.False(UpdateInstallation.IsCurrent(release, assembly, new Version(2, 2, 2)));
      }
      finally { File.Delete(receipt); File.Delete(assembly); }
    }

    private sealed class Handler : HttpMessageHandler
    {
      private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
      public Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) { _respond = respond; }
      protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
      { token.ThrowIfCancellationRequested(); return Task.FromResult(_respond(request)); }
    }
  }
}
