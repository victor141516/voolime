using System.Diagnostics;
using System.IO;

namespace Voolime;

internal static class AppLogger
{
    private static readonly object Sync = new();
    private static string? _logFilePath;

    public static string LogFilePath
    {
        get
        {
            EnsureInitialized();
            return _logFilePath!;
        }
    }

    public static void Initialize()
    {
        EnsureInitialized();
        Info("Logger initialized.");
    }

    public static void Info(string message) =>
        Write("INFO", message);

    public static void Warn(string message) =>
        Write("WARN", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message} {exception}");

    private static void EnsureInitialized()
    {
        if (_logFilePath is not null)
        {
            return;
        }

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Voolime",
            "Logs");
        Directory.CreateDirectory(root);
        _logFilePath = Path.Combine(root, "voolime.log");
    }

    private static void Write(string level, string message)
    {
        try
        {
            EnsureInitialized();
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}{Environment.NewLine}";
            lock (Sync)
            {
                File.AppendAllText(_logFilePath!, line);
            }
        }
        catch
        {
            Debug.WriteLine(message);
        }
    }
}
