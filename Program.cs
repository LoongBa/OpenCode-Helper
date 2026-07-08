using System.CommandLine;
using System.CommandLine.Invocation;
using System.Reflection;
using OpenCodeHelper.Services;
using Spectre.Console;

namespace OpenCodeHelper;

/// <summary>OpenCode-Helper — 入口</summary>
public static partial class Program
{
    private const string AppName = "OpenCode 助手";
    private const string GitHubUrl = "https://github.com/[username]/OpenCode-Helper";

    private static readonly string AppVersion =
        Assembly.GetEntryAssembly()?.GetName()?.Version?.ToString() ?? "0.8.0.0";

    public static async Task<int> Main(string[] args)
    {
        Console.Title = $"{AppName} v{AppVersion}";

        ShowStartupInfo();

        // ── 命令行参数定义 ──
        var dbOption = new Option<string>("--db", "指定 OpenCode SQLite 数据库文件路径");
        dbOption.SetDefaultValueFactory(() => DatabaseService.GetDefaultDbPath());

        var backupOnlyOption = new Option<bool>("--backup-only", "仅执行全库备份后退出");
        var purgeBeforeOption = new Option<string>("--purge-before", "批量删除指定日期前的所有会话 (格式: yyyy-MM-dd)");
        var vacuumOption = new Option<bool>("--vacuum", "仅执行数据库收缩 (VACUUM)");
        var noAutoBackupOption = new Option<bool>("--no-auto-backup", "关闭删除前自动备份");

        var rootCommand = new RootCommand(
            $"{AppName} v{AppVersion} — 浏览、搜索、批量删除、备份 OpenCode 会话")
        {
            dbOption, backupOnlyOption, purgeBeforeOption, vacuumOption, noAutoBackupOption
        };

        rootCommand.SetHandler(
            (string dbPath, bool backupOnly, string? purgeBefore, bool vacuum, bool noAutoBackup) =>
            {
                if (!backupOnly && string.IsNullOrEmpty(purgeBefore) && !vacuum)
                {
                    RunTui(dbPath, noAutoBackup);
                    return;
                }
                RunCli(dbPath, backupOnly, purgeBefore, vacuum, noAutoBackup);
            },
            dbOption, backupOnlyOption, purgeBeforeOption, vacuumOption, noAutoBackupOption);

        return await rootCommand.InvokeAsync(args);
    }

    private static void ShowStartupInfo()
    {
        Console.Clear();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  {AppName}");
        Console.ResetColor();
        Console.WriteLine($"  版本 {AppVersion}");
        Console.WriteLine($"  {GitHubUrl}");
        Console.WriteLine($"  {Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description}");
        Console.WriteLine();
        AnsiConsole.MarkupLine("  [green]提示[/]: 使用 [yellow]--help[/] 查看所有命令行选项");
        AnsiConsole.MarkupLine("  [green]直接启动[/]: 进入交互式 TUI 管理模式");
        Console.WriteLine(new string('─', 60));
        Console.WriteLine();
    }
}
