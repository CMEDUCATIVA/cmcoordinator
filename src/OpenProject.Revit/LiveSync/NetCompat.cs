using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenProject.Revit.LiveSync
{
  // Revit 2023 and 2024 run on .NET Framework 4.8; 2025–2027 on .NET 8 and 10. These helpers give the same
  // behaviour on all of them, so the synchronization code is one source for every Revit version.
  public static class NetCompat
  {
    public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    public static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
    public static double Clamp(double value, double min, double max) => value < min ? min : value > max ? max : value;

    public static byte[] Sha256(byte[] value) { using var sha = SHA256.Create(); return sha.ComputeHash(value); }

    // Lower-case hexadecimal, as the CDE stores hashes.
    public static string Hex(byte[] value)
    {
      var text = new StringBuilder(value.Length * 2);
      foreach (var b in value) text.Append(b.ToString("x2"));
      return text.ToString();
    }

    public static int ProcessId
    {
      get { using var process = Process.GetCurrentProcess(); return process.Id; }
    }

    [ThreadStatic] private static Random _random;
    // Retry jitter; not for anything secret.
    public static double NextJitter() => (_random ??= new Random(Guid.NewGuid().GetHashCode())).NextDouble();

    // Moves a finished temporary file over the previous one.
    public static void MoveReplacing(string source, string destination)
    {
#if NETFRAMEWORK
      if (File.Exists(destination)) File.Replace(source, destination, null);
      else File.Move(source, destination);
#else
      File.Move(source, destination, true);
#endif
    }

    public static void SetArguments(ProcessStartInfo start, IEnumerable<string> arguments)
    {
#if NETFRAMEWORK
      start.Arguments = string.Join(" ", System.Linq.Enumerable.Select(arguments, Quote));
#else
      foreach (var argument in arguments) start.ArgumentList.Add(argument);
#endif
    }

    // Windows command-line quoting (CommandLineToArgvW rules), what ArgumentList does on .NET.
    public static string Quote(string argument)
    {
      if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return argument;
      var result = new StringBuilder("\"");
      var backslashes = 0;
      foreach (var c in argument)
      {
        if (c == '\\') { backslashes++; continue; }
        if (c == '"') { result.Append('\\', backslashes * 2 + 1).Append('"'); backslashes = 0; continue; }
        result.Append('\\', backslashes).Append(c); backslashes = 0;
      }
      return result.Append('\\', backslashes * 2).Append('"').ToString();
    }
  }

#if NETFRAMEWORK
  // Same names and behaviour as the .NET 8 members, so calls read the same on every Revit version.
  internal static class NetFrameworkExtensions
  {
    public static bool TryAdd<TKey, TValue>(this Dictionary<TKey, TValue> dictionary, TKey key, TValue value)
    {
      if (dictionary.ContainsKey(key)) return false;
      dictionary.Add(key, value); return true;
    }

    public static bool Remove<TKey, TValue>(this Dictionary<TKey, TValue> dictionary, TKey key, out TValue value) =>
      dictionary.TryGetValue(key, out value) && dictionary.Remove(key);

    public static bool Contains(this string text, char value) => text.IndexOf(value) >= 0;

    public static async Task WaitAsync(this Task task, CancellationToken token)
    {
      var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
      using (token.Register(() => cancelled.TrySetCanceled(token)))
        await (await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false)).ConfigureAwait(false);
    }
  }
#endif
}
