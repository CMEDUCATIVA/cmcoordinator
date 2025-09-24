using System.IO;
using Serilog;

namespace OpenProject.Shared.Logging
{
  public static class Logger
  {
    private static readonly string _logFolderPath =
      Path.Combine(ConfigurationConstant.OpenProjectApplicationData, "logs");

    public static void ConfigureLogger(string logFileName = null)
    {
      var fileName = string.IsNullOrWhiteSpace(logFileName) ? "OpenProject.Log..txt" : logFileName;
      var logFilePath = Path.Combine(_logFolderPath, fileName);

      Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Debug()
        .WriteTo.File(logFilePath, rollingInterval: RollingInterval.Day)
        .WriteTo.Console()
        .CreateLogger();
    }
  }
}
