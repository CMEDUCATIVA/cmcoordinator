using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using OpenProject.Revit.LiveSync;
using OpenProject.Revit.LiveSync.Queue;
using OpenProject.Revit.LiveSync.Transport;
using Xunit;

namespace OpenProject.Tests.LiveSync
{
  public class ReceiverIntegrationTests
  {
    [Fact(Skip = "El visor local protocolo 2 fue retirado del flujo de sincronización; la integración activa es CDE protocolo 3.")]
    public async Task RealCSharpTransportResumesNodeSqliteReceiverAndCommitsDeltaAfterRestart()
    {
      using var folder = new OutboxTests.TestFolder();
      using var box = new SqliteOutbox(folder.Path);
      var elements = Enumerable.Range(0, 180).ToDictionary(i => "e" + i, i => new LiveElement {
        Id = "e" + i, ElementId = i.ToString(), Name = "Cubo", Category = "Demo",
        Properties = new Dictionary<string, string> { ["Datos"] = new string('\u00f1', 2048) },
        Meshes = new List<LiveMesh> { new() { Positions = new double[] { 0,0,0,1,0,0,0,1,0 }, Indices = new[] { 0,1,2 } } }
      });
      box.Save(new LiveSnapshot("real-doc", "Modelo", "3D", elements));
      var started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
      var job = box.Prepare("real-session", started, null, true);
      Assert.True(job.ChunkCount > 1);
      var server = await NodeFixture.Start(folder.Path);
      try
      {
        using var client = new SyncReceiver(baseAddress: new Uri($"http://127.0.0.1:{server.Port}/"));
        Assert.Contains(2, (await client.StatusAsync(CancellationToken.None)).Protocols);
        await client.BeginAsync(job, CancellationToken.None);
        await client.ChunkAsync(job.JobId, 0, box.Chunk(0), CancellationToken.None); box.Ack(0);
        var port = server.Port; await server.Stop(); server = await NodeFixture.Start(folder.Path, port);
        Assert.Equal(new[] { 0 }, (await client.BeginAsync(job, CancellationToken.None)).Received);
        for (var i = 1; i < job.ChunkCount; i++) await client.ChunkAsync(job.JobId, i, box.Chunk(i), CancellationToken.None);
        await client.CommitAsync(job.JobId, CancellationToken.None);
        await client.CommitAsync(job.JobId, CancellationToken.None);
        box.Complete(job);
        Assert.Equal(180, (await client.StatusAsync(CancellationToken.None)).Count);
        elements.Remove("e1"); elements["new"] = new LiveElement { Id = "new", ElementId = "999", Name = "Nuevo", Category = "Demo" };
        box.Save(new LiveSnapshot("real-doc", "Modelo", "3D", elements));
        var delta = box.Prepare("real-session", started, job.JobId, false);
        Assert.True(delta.ByteLength < job.ByteLength / 10);
        await client.BeginAsync(delta, CancellationToken.None);
        for (var i = 0; i < delta.ChunkCount; i++) await client.ChunkAsync(delta.JobId, i, box.Chunk(i), CancellationToken.None);
        await client.CommitAsync(delta.JobId, CancellationToken.None);
        Assert.Equal(delta.JobId, (await client.StatusAsync(CancellationToken.None)).RevisionId);
        Assert.Equal(180, (await client.StatusAsync(CancellationToken.None)).Count);
      }
      finally { await server.Stop(); }
    }
    private sealed class NodeFixture
    {
      private Process _process;
      public int Port { get; private set; }
      public static async Task<NodeFixture> Start(string directory, int port = 0)
      {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "tools/local-live-viewer/test/receiver-fixture.mjs"))) root = root.Parent;
        Assert.NotNull(root);
        var start = new ProcessStartInfo("node") { WorkingDirectory = root.FullName, UseShellExecute = false,
          CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardInput = true,
          RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("tools/local-live-viewer/test/receiver-fixture.mjs");
        start.ArgumentList.Add(Path.Combine(directory, "receiver.sqlite")); start.ArgumentList.Add(port.ToString());
        var process = Process.Start(start); process.BeginErrorReadLine();
        try
        {
          var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
          return new NodeFixture { _process = process, Port = JObject.Parse(line)["port"].Value<int>() };
        }
        catch { if (!process.HasExited) process.Kill(); process.Dispose(); throw; }
      }
      public async Task Stop()
      {
        if (_process == null) return;
        try
        {
          if (!_process.HasExited) { await _process.StandardInput.WriteLineAsync("stop"); await _process.StandardInput.FlushAsync(); await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        }
        finally { if (!_process.HasExited) _process.Kill(); _process.Dispose(); _process = null; }
      }
    }
  }
}
