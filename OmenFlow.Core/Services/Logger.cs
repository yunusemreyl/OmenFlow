using System;
using System.IO;

namespace OmenFlow.Core.Services;

public static class Logger
{
    private static readonly string LogDirectory;
    private static readonly string LogFilePath;
    private static readonly object _lock = new object();

    static Logger()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            LogDirectory = Path.Combine(appData, "OmenFlow", "Logs");
            Directory.CreateDirectory(LogDirectory);
            LogFilePath = Path.Combine(LogDirectory, $"OmenFlow_{DateTime.Now:yyyyMMdd}.log");
        }
        catch
        {
            // Fallback to local directory if AppData is not accessible
            LogDirectory = Path.Combine(AppContext.BaseDirectory, "Logs");
            Directory.CreateDirectory(LogDirectory);
            LogFilePath = Path.Combine(LogDirectory, $"OmenFlow_{DateTime.Now:yyyyMMdd}.log");
        }
    }

    public static void LogInfo(string message)
    {
        Log(message, "INFO");
    }

    public static void LogWarning(string message)
    {
        Log(message, "WARN");
    }

    public static void LogError(string message, Exception? ex = null)
    {
        string fullMessage = ex != null ? $"{message} - {ex.Message}\n{ex.StackTrace}" : message;
        Log(fullMessage, "ERROR");
    }

    private static void Log(string message, string level)
    {
        try
        {
            string processName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
            string logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{processName}] [{level}] {message}{Environment.NewLine}";
            
            lock (_lock)
            {
                File.AppendAllText(LogFilePath, logLine);
            }
        }
        catch
        {
            // Silently ignore logging errors to prevent crashes in the logging pipeline
        }
    }
}
