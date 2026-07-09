using OpenCodeHelper.Models;
using OpenCodeHelper.Services;

namespace OpenCodeHelper;

/// <summary>TUI 模式入口与命令行操作</summary>
public static partial class Program
{
    private const string ConfigFileName = "opencode-helper.json";

    /// <summary>进入交互式 TUI 模式</summary>
    private static void RunTui(string dbPath, bool noAutoBackup)
    {
        using var dbService = new DatabaseService(dbPath);
        var config = LoadConfig(dbPath, noAutoBackup);

        if (!TryOpenDatabase(dbService)) return;

        var backupService = new BackupService(dbPath, config.BackupDirectory);
        config.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFileName));

        var tui = new TuiService(dbService, backupService, config);
        tui.Run();
    }

    /// <summary>执行命令行操作后退出</summary>
    private static void RunCli(string dbPath, bool backupOnly, string? purgeBefore, bool vacuum, bool noAutoBackup)
    {
        using var dbService = new DatabaseService(dbPath);
        var config = LoadConfig(dbPath, noAutoBackup);

        if (!TryOpenDatabase(dbService)) return;

        var backupService = new BackupService(dbPath, config.BackupDirectory);

        if (backupOnly)
        {
            Console.WriteLine("正在执行全库备份...");
            try
            {
                var backupPath = backupService.CreateBackup();
                Console.WriteLine($"备份完成: {backupPath}");
                LogService.Info($"命令行备份: {backupPath}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"备份失败: {ex.Message}");
                Environment.Exit(1);
            }
            return;
        }

        if (vacuum)
        {
            Console.WriteLine("正在执行数据库收缩 (VACUUM)...");
            var (success, beforeSize, afterSize, error) = dbService.Vacuum();
            if (success)
            {
                var saved = beforeSize - afterSize;
                var savedPercent = beforeSize > 0 ? (saved * 100.0 / beforeSize) : 0;
                Console.WriteLine($"VACUUM 完成:");
                Console.WriteLine($"  压缩前: {TuiService.FormatBytes(beforeSize)}");
                Console.WriteLine($"  压缩后: {TuiService.FormatBytes(afterSize)}");
                Console.WriteLine($"  节省:   {TuiService.FormatBytes(saved)} ({savedPercent:F1}%)");
                LogService.Info($"命令行 VACUUM: {TuiService.FormatBytes(beforeSize)} → {TuiService.FormatBytes(afterSize)}");
            }
            else
            {
                Console.Error.WriteLine($"VACUUM 失败: {error}");
                Environment.Exit(1);
            }
            return;
        }

        if (!string.IsNullOrEmpty(purgeBefore))
        {
            if (!DateTime.TryParse(purgeBefore, out var cutoffDate))
            {
                Console.Error.WriteLine($"日期格式错误: {purgeBefore}，请使用 yyyy-MM-dd 格式");
                Environment.Exit(1);
                return;
            }

            if (config.AutoBackup)
            {
                Console.WriteLine("正在进行删除前自动备份...");
                try
                {
                    var backupPath = backupService.CreateBackup();
                    Console.WriteLine($"备份完成: {backupPath}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"备份失败: {ex.Message}");
                    Environment.Exit(1);
                    return;
                }
            }

            Console.WriteLine($"正在删除 {cutoffDate:yyyy-MM-dd} 前的所有会话...");
            try
            {
                var deletedCount = dbService.PurgeSessionsBefore(cutoffDate);
                Console.WriteLine($"成功删除 {deletedCount} 条会话");
                LogService.Info($"命令行 purge-before {purgeBefore}: 删除 {deletedCount} 条");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"删除失败: {ex.Message}");
                Environment.Exit(1);
            }
            return;
        }
    }

    /// <summary>加载配置</summary>
    private static AppConfig LoadConfig(string dbPath, bool noAutoBackup)
    {
        var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFileName);
        var config = AppConfig.Load(configPath);
        config.CustomDbPath = dbPath;
        if (noAutoBackup) config.AutoBackup = false;
        return config;
    }

    /// <summary>校验并打开数据库，成功返回 true</summary>
    private static bool TryOpenDatabase(DatabaseService dbService)
    {
        var (valid, errorMsg) = dbService.ValidateDatabase();
        if (!valid)
        {
            Console.Error.WriteLine($"错误: {errorMsg}");
            return false;
        }
        if (!dbService.Open())
        {
            Console.Error.WriteLine("错误: 无法打开数据库连接");
            return false;
        }
        return true;
    }
}
