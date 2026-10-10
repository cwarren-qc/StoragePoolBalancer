using System;
using System.IO;
using Microsoft.AspNetCore.Hosting;

namespace StorageBalancer.App.Subsystems.Logging;

public class AppEventLogger : IAppEventLogger
{
    private readonly string _logFilePath;
    private readonly object _lock = new();

    public AppEventLogger(IWebHostEnvironment? environment = null)
    {
        string root = environment?.ContentRootPath ?? AppDomain.CurrentDomain.BaseDirectory;
        string logsDir = Path.Combine(root, "logs");
        try
        {
            Directory.CreateDirectory(logsDir);
        }
        catch
        {
            // Fallback to current directory if creating logs dir fails
            logsDir = root;
        }

        _logFilePath = Path.Combine(logsDir, "application-events.log");
    }

    public string LogFilePath => _logFilePath;

    public void LogInfo(string category, string message) =>
        WriteEntry("INFO", category, message, null);

    public void LogWarning(string category, string message) =>
        WriteEntry("WARN", category, message, null);

    public void LogError(string category, string message, Exception? exception = null) =>
        WriteEntry("ERROR", category, message, exception);

    private void WriteEntry(string level, string category, string message, Exception? exception)
    {
        string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff UTC");
        string line = $"[{timestamp}] [{level}] [{category}] {message}";

        if (exception != null)
        {
            line += $"{Environment.NewLine}  -> Exception: {exception.GetType().Name}: {exception.Message}";
            if (!string.IsNullOrEmpty(exception.StackTrace))
            {
                line += $"{Environment.NewLine}  -> StackTrace: {exception.StackTrace}";
            }
        }

        Console.WriteLine(line);

        lock (_lock)
        {
            try
            {
                File.AppendAllText(_logFilePath, line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AppEventLogger] Failed to write log file: {ex.Message}");
            }
        }
    }
}
