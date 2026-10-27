using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json;

namespace OpenProject.Revit.LiveSync.Transport
{
  // DPAPI protects the complete record for this Windows user; it never enters diagnostics.
  internal static class CdeCredentialVault
  {
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cmcoordinator", "LiveSync", "cde-connection.bin");
    public static void Save(CdeDestination destination)
    {
      var bytes = Encoding.UTF8.GetBytes(SerializeRecord(destination));
      try
      {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
        var encrypted = Transform(bytes, true);
        var temporary = FilePath + ".pending";
        File.WriteAllBytes(temporary, encrypted); NetCompat.MoveReplacing(temporary, FilePath);
      }
      finally { Array.Clear(bytes, 0, bytes.Length); }
    }
    public static CdeDestination Load()
    {
      if (!File.Exists(FilePath)) return null;
      byte[] plain = null;
      try
      {
        plain = Transform(File.ReadAllBytes(FilePath), false);
        return ParseRecord(Encoding.UTF8.GetString(plain));
      }
      catch (Exception) { return null; } // A different Windows account must reconnect.
      finally { if (plain != null) Array.Clear(plain, 0, plain.Length); }
    }
    internal static string SerializeRecord(CdeDestination destination) => JsonConvert.SerializeObject(new
    {
      link = destination.Address.AbsoluteUri, token = destination.Token, userId = destination.UserId,
      connectionId = destination.ConnectionId, projectName = destination.ProjectName, modelName = destination.ModelName
    });
    internal static CdeDestination ParseRecord(string json)
    {
      var value = Newtonsoft.Json.Linq.JObject.Parse(json);
      return new CdeDestination((string)value["link"], (string)value["token"],
        (long?)value["userId"] ?? 0, connectionId: (long?)value["connectionId"] ?? 0,
        projectName: (string)value["projectName"], modelName: (string)value["modelName"]);
    }
    private static byte[] Transform(byte[] bytes, bool encrypt)
    {
      var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
      Blob output = default;
      try
      {
        Marshal.Copy(bytes, 0, input.Data, bytes.Length);
        var ok = encrypt ? CryptProtectData(ref input, "Cmcoordinator", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
          : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
        if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
      }
      finally
      {
        for (var i = 0; i < input.Length; i++) Marshal.WriteByte(input.Data, i, 0);
        Marshal.FreeHGlobal(input.Data);
        if (output.Data != IntPtr.Zero)
        {
          for (var i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0);
          LocalFree(output.Data);
        }
      }
    }
  }
}
