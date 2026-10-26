using System;
using System.IO;

namespace SpenTabletHost;

/// <summary>
/// 简易日志：同时写控制台和日志文件。
/// 托盘程序（WinExe）没有控制台窗口，必须落盘才能事后排查。
/// </summary>
internal static class AppLog
{
    private static readonly object _lock = new();
    private static string? _file;

    public static string FilePath
    {
        get
        {
            if (_file != null) return _file;
            string dir;
            try { dir = Path.GetDirectoryName(Environment.ProcessPath) ?? Directory.GetCurrentDirectory(); }
            catch { dir = Directory.GetCurrentDirectory(); }
            if (string.IsNullOrEmpty(dir)) dir = Directory.GetCurrentDirectory();
            _file = Path.Combine(dir, "SpenTabletHost.log");
            return _file;
        }
    }

    public static void Info(string line)
    {
        try { Console.WriteLine(line); } catch { }
        Write("INFO", line);
    }

    public static void Warn(string line)
    {
        try { Console.Error.WriteLine(line); } catch { }
        Write("WARN", line);
    }

    public static void Error(string line)
    {
        try { Console.Error.WriteLine(line); } catch { }
        Write("ERR ", line);
    }

    private static void Write(string level, string line)
    {
        try
        {
            lock (_lock)
                File.AppendAllText(FilePath, $"{DateTime.Now:MM-dd HH:mm:ss} [{level}] {line}\n");
        }
        catch { /* 日志失败绝不拖垮主流程 */ }
    }
}
