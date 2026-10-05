using System.Text;

namespace LcscBridge.Core;

public static class BridgeLog
{
    private static readonly object Sync = new();
    public static string LogDirectory { get; } = Environment.GetEnvironmentVariable("LCSC_BRIDGE_LOG_DIRECTORY") ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AltiumLcscBridge", "Logs");

    public static void Info(string area, string message, string? requestId = null) => Write("INFO", area, message, requestId, null);
    public static void Error(string area, Exception exception, string? requestId = null) => Write("ERROR", area, exception.Message, requestId, exception);

    private static void Write(string level, string area, string message, string? requestId, Exception? exception)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogDirectory);
                var path = Path.Combine(LogDirectory, $"companion-{DateTime.UtcNow:yyyyMMdd}.log");
                var clean = message.Replace('\r', ' ').Replace('\n', ' ');
                var line = $"{DateTimeOffset.Now:O} [{level}] [{area}]" +
                           (string.IsNullOrWhiteSpace(requestId) ? "" : $" [request={requestId}]") +
                           $" {clean}";
                if (exception is not null) line += Environment.NewLine + exception;
                File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
                PruneOldLogs();
            }
        }
        catch
        {
            // Diagnostics must never break importing or placement.
        }
    }

    private static void PruneOldLogs()
    {
        foreach (var file in new DirectoryInfo(LogDirectory).EnumerateFiles("companion-*.log")
                     .OrderByDescending(x => x.Name).Skip(14))
            file.Delete();
    }
}
