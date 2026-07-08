namespace OpenCodeHelper.Services;

/// <summary>简单日志服务 — 记录操作日志到本地文本文件</summary>
public static class LogService
{
    private static readonly string LogDir;
    private static readonly string LogFilePath;
    private static readonly object LockObj = new();

    static LogService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        LogDir = Path.Combine(appData, "OpenCodeSessionManager", "logs");
        LogFilePath = Path.Combine(LogDir, $"opencode_{DateTime.Now:yyyyMM}.log");

        try
        {
            if (!Directory.Exists(LogDir))
                Directory.CreateDirectory(LogDir);
        }
        catch
        {
            // 日志目录创建失败则静默忽略
        }
    }

    /// <summary>记录日志</summary>
    public static void Log(string level, string message)
    {
        try
        {
            lock (LockObj)
            {
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
                File.AppendAllText(LogFilePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // 日志写入失败静默忽略
        }
    }

    public static void Info(string message) => Log("INFO", message);
    public static void Warn(string message) => Log("WARN", message);
    public static void Error(string message) => Log("ERROR", message);
}
