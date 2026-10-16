using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OpenProject.Revit.Services.Updates
{
  public sealed class GitHubUpdateClient
  {
    private readonly HttpClient _http;
    public GitHubUpdateClient(HttpClient http) { _http = http; }

    public async Task<UpdateRelease> LatestAsync(CancellationToken token)
    {
      using var request = new HttpRequestMessage(HttpMethod.Get,
        "https://api.github.com/repos/CMEDUCATIVA/cmcoordinator/releases/latest");
      request.Headers.UserAgent.ParseAdd("Cmcoordinator-Updater/1.0");
      request.Headers.Accept.ParseAdd("application/vnd.github+json");
      using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
      timeout.CancelAfter(TimeSpan.FromSeconds(25));
      using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
      if (response.StatusCode == HttpStatusCode.NotFound) return null;
      if (response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429)
        throw new InvalidOperationException("GitHub limitó la consulta. Inténtalo más tarde.");
      response.EnsureSuccessStatusCode();
      var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
      if (json.Length > 2 * 1024 * 1024) throw new InvalidOperationException("Respuesta de GitHub demasiado grande.");
      return UpdateRelease.Parse(json);
    }

    public async Task DownloadAsync(UpdateRelease release, string file, IProgress<int> progress, CancellationToken token)
    {
      var temporary = file + ".part";
      try
      {
        using var response = await _http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
        {
          var buffer = new byte[81920]; long total = 0; int read;
          while ((read = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
          {
            total += read;
            if (total > release.Size) throw new InvalidOperationException("El instalador excede el tamaño publicado.");
            await output.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);
            progress?.Report((int)(100 * total / release.Size));
          }
          if (total != release.Size) throw new InvalidOperationException("La descarga está incompleta.");
        }
        token.ThrowIfCancellationRequested();
        if (UpdateRelease.FileHash(temporary) != release.Sha256)
          throw new InvalidOperationException("El SHA256 no coincide. El archivo no se instalará.");
        using (var input = File.OpenRead(temporary))
          if (input.ReadByte() != 'M' || input.ReadByte() != 'Z') throw new InvalidOperationException("El archivo no es un instalador Windows.");
        File.Move(temporary, file);
      }
      finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
  }
}
