using System;
using System.IO;
using System.Linq;

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
            LogFilePath = Path.Combine(LogDirectory, $"OmenFlow_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            CleanupOldLogs();
        }
        catch
        {
            // Fallback to local directory if AppData is not accessible
            LogDirectory = Path.Combine(AppContext.BaseDirectory, "Logs");
            Directory.CreateDirectory(LogDirectory);
            LogFilePath = Path.Combine(LogDirectory, $"OmenFlow_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            CleanupOldLogs();
        }
    }

    private static void CleanupOldLogs()
    {
        try
        {
            var directory = new DirectoryInfo(LogDirectory);
            var files = directory.GetFiles("OmenFlow_*.log")
                                 .OrderByDescending(f => f.LastWriteTime)
                                 .ToList();

            // Keep the newest 10 logs
            if (files.Count > 10)
            {
                for (int i = 10; i < files.Count; i++)
                {
                    try
                    {
                        files[i].Delete();
                    }
                    catch
                    {
                        // Ignore files that are in use
                    }
                }
            }
        }
        catch
        {
            // Ignore cleanup failures
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
