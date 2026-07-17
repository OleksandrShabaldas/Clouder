namespace Clouder.Core.Logging;

public static class ClouderLog
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clouder", "logs");
    private static readonly object WriteLock = new();

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}\n  {ex.GetType().Name}: {ex.Message}\n  {ex.StackTrace}");
    public static void Debug(string message) => Write("DEBUG", message);

    public static void CleanOldLogs(int keepDays = 14)
    {
        try
        {
            if (!Directory.Exists(LogDir)) return;
            var cutoff = DateTime.Now.AddDays(-keepDays);
            foreach (var file in Directory.GetFiles(LogDir, "clouder-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch { }
    }

    private static void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            var file = Path.Combine(LogDir, $"clouder-{DateTime.Now:yyyy-MM-dd}.log");
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level,-5}] {message}{Environment.NewLine}";
            lock (WriteLock)
            {
                File.AppendAllText(file, line);
            }
        }
        catch { /* Logging must never crash the app */ }
    }
}
