using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace OpenProject.Revit.LiveSync.Transport
{
  internal static class LocalServiceHost
  {
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static async Task EnsureAsync()
    {
      await Gate.WaitAsync().ConfigureAwait(false);
      try
      {
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(1) };
        try
        {
          var value = JObject.Parse(await http.GetStringAsync(LocalViewerEndpoint.Url + "api/status").ConfigureAwait(false));
          if (value["application"]?.ToString() == "cmcoordinator-local-live-viewer") return;
          throw new InvalidOperationException("El puerto del visor está ocupado por otra aplicación.");
        }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { throw new HttpRequestException("El puerto del visor no responde; se reintentará."); }
        var directory = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "LiveViewer");
        if (!File.Exists(Path.Combine(directory, "server.mjs"))) throw new InvalidOperationException("Faltan los archivos del visor local.");
        var start = new ProcessStartInfo(FindNode()) { WorkingDirectory = directory, UseShellExecute = false,
          CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        NetCompat.SetArguments(start, new[] { "server.mjs" });
        using var process = Process.Start(start);
        for (var i = 0; i < 20; i++)
        {
          await Task.Delay(150).ConfigureAwait(false);
          try { await http.GetStringAsync(LocalViewerEndpoint.Url + "api/status").ConfigureAwait(false); return; }
          catch (HttpRequestException) { }
          catch (TaskCanceledException) { }
        }
        throw new HttpRequestException("El servidor no inició. Se necesita Node 24.11 o posterior.");
      }
      finally { Gate.Release(); }
    }
    private static string FindNode()
    {
      foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
      { var file = Path.Combine(path.Trim('"'), "node.exe"); if (File.Exists(file)) return file; }
      throw new InvalidOperationException("Instala Node 24.11 o posterior para ejecutar el visor local.");
    }
  }
}
