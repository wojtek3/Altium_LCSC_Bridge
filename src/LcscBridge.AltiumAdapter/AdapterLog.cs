using System;
using System.IO;
using System.Text;

namespace EasyEDA_Loader
{
    internal static class AdapterLog
    {
        private static readonly object Sync = new object();

        public static void Info(string stage, string message, string requestId = null) => Write("INFO", stage, message, requestId, null);
        public static void Error(string stage, Exception exception, string requestId = null) => Write("ERROR", stage, exception.Message, requestId, exception);

        private static void Write(string level, string stage, string message, string requestId, Exception exception)
        {
            try
            {
                lock (Sync)
                {
                    var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AltiumLcscBridge", "Logs");
                    Directory.CreateDirectory(directory);
                    var path = Path.Combine(directory, "altium-adapter-" + DateTime.UtcNow.ToString("yyyyMMdd") + ".log");
                    var clean = (message ?? "").Replace('\r', ' ').Replace('\n', ' ');
                    var line = DateTimeOffset.Now.ToString("O") + " [" + level + "] [" + stage + "]" +
                               (string.IsNullOrWhiteSpace(requestId) ? "" : " [request=" + requestId + "]") + " " + clean;
                    if (exception != null) line += Environment.NewLine + exception;
                    File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch { }
        }
    }
}
