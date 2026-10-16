using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace OpenProject.Revit.LiveSync.Telemetry
{
  public interface ISyncTelemetrySink { void Emit(string stage, string code, string message, string jobId = null, long durationMs = 0, long bytes = 0, int elements = 0); }

  public sealed class SyncTelemetry : ISyncTelemetrySink, IDisposable
  {
    private readonly ConcurrentQueue<object> _pending = new();
    private readonly Queue<string> _recent = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly Task _writer;
    private readonly string _directory;
    private volatile string _writeError;
    public string WriteError => _writeError;
    public SyncTelemetry(string directory)
    { _directory = directory; _writer = Task.Run(WriteAsync); }
    public void Emit(string stage, string code, string message, string jobId = null, long durationMs = 0, long bytes = 0, int elements = 0)
    {
      if (_pending.Count > 512) return;
      _pending.Enqueue(new { timestampUtc = DateTime.UtcNow, stage, code, message, jobId, durationMs, bytes, elementCount = elements,
        appVersion = typeof(SyncTelemetry).Assembly.GetName().Version?.ToString(), protocol = 2 });
      lock (_recent) { _recent.Enqueue($"{DateTime.Now:HH:mm:ss} [{code}] {message}"); while (_recent.Count > 60) _recent.Dequeue(); }
    }
    public string Recent { get { lock (_recent) return string.Join(Environment.NewLine, _recent.Reverse()); } }
    private async Task WriteAsync()
    {
      while (!_cancel.IsCancellationRequested || !_pending.IsEmpty)
      {
        if (_pending.IsEmpty)
        {
          try { await Task.Delay(500, _cancel.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
          continue;
        }
        try
        {
          Directory.CreateDirectory(_directory);
          var path = Path.Combine(_directory, "sync.jsonl");
          if (File.Exists(path) && new FileInfo(path).Length > 5 * 1024 * 1024)
          {
            for (var i = 4; i >= 1; i--)
            {
              var from = i == 1 ? path : path + "." + (i - 1);
              if (File.Exists(from)) File.Move(from, path + "." + i, true);
            }
          }
          using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
          using (var writer = new StreamWriter(stream))
            while (_pending.TryDequeue(out var entry)) writer.WriteLine(JsonConvert.SerializeObject(entry));
          _writeError = null;
        }
        catch (Exception exception) { _writeError = "No se pudo escribir el diagnóstico: " + exception.GetType().Name; }
        if (_cancel.IsCancellationRequested) break;
        try { await Task.Delay(500, _cancel.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
      }
    }
    public string Export()
    {
      Directory.CreateDirectory(_directory);
      var path = Path.Combine(_directory, $"diagnostico-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
      using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
      foreach (var file in Directory.GetFiles(_directory, "*.jsonl*"))
      {
        var entry = zip.CreateEntry(Path.GetFileName(file));
        using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var destination = entry.Open(); source.CopyTo(destination);
      }
      return path;
    }
    public void Dispose() { _cancel.Cancel(); _ = _writer.ContinueWith(_ => _cancel.Dispose(), TaskScheduler.Default); }
  }
}
