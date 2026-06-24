using System;
using System.IO;

namespace OwnWand.Core.Helpers;

public static class Logger
{
    private static readonly string LogFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ownwand.log");
    private static readonly string ProjectLogFilePath = @"C:\Projects\WAND\ownwand.log";
    private static readonly object LockObj = new();

    public static void Log(string message)
    {
        var logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
        System.Diagnostics.Debug.WriteLine(logLine);
        
        lock (LockObj)
        {
            try
            {
                File.AppendAllText(LogFilePath, logLine + Environment.NewLine);
            }
            catch {}

            try
            {
                File.AppendAllText(ProjectLogFilePath, logLine + Environment.NewLine);
            }
            catch {}
        }
    }

    public static void Clear()
    {
        lock (LockObj)
        {
            try { File.WriteAllText(LogFilePath, string.Empty); } catch {}
            try { File.WriteAllText(ProjectLogFilePath, string.Empty); } catch {}
        }
    }
}
