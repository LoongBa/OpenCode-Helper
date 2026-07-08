using Spectre.Console;
using OpenCodeHelper.Models;
using OpenCodeHelper.Services;

namespace OpenCodeHelper.Services;

/// <summary>终端 UI 服务 — 渲染交互式 TUI 界面，处理键盘输入</summary>
public class TuiService
{
    private readonly DatabaseService _dbService;
    private readonly BackupService _backupService;
    private readonly AppConfig _config;

    // — 会话数据 —
    private List<Session> _sessions = new();
    private HashSet<string> _selectedIds = new();
    private int _cursorIndex;
    private int _page;
    private const int PageSize = 20;
    private string _searchKeyword = string.Empty;
    private string? _projectFilter;
    private List<string> _projectPaths = new();

    // — 会话类型筛选 —
    private string? _sessionType;       // null=全部, "sisyphus"=仅主对话
    private List<string> _availableTypes = new();
    private const string DefaultSessionType = "sisyphus";

    // — 排序 —
    private bool _sortAscending;        // false = 降序（最新在前）

    // — 日期分组缓存 —
    private readonly List<(string label, int start, int count)> _dateGroups = new();

    private string _timeFilter = "all"; // all / month / half-year
    private bool _running = true;
    private string _statusMessage = string.Empty;
    private DateTime _statusMessageTime;
    private int _totalFilteredCount;

    public TuiService(DatabaseService dbService, BackupService backupService, AppConfig config)
    {
        _dbService = dbService;
        _backupService = backupService;
        _config = config;
        _timeFilter = config.TimeFilter;
        _sessionType = DefaultSessionType; // 默认仅显示主对话
    }

    /// <summary>启动交互式 TUI 主循环</summary>
    public void Run()
    {
        Console.CursorVisible = false;

        // 校验数据库
        var (valid, errorMsg) = _dbService.ValidateDatabase();
        if (!valid)
        {
            AnsiConsole.Write(new Panel(
                new Markup($"[red]数据库错误[/]\n\n{errorMsg}"))
                .Header("❌ 错误")
                .Border(BoxBorder.Rounded));
            AnsiConsole.MarkupLine("\n按任意键退出...");
            Console.ReadKey(true);
            return;
        }

        // 连接数据库
        if (!_dbService.Open())
        {
            AnsiConsole.MarkupLine("[red]无法打开数据库连接[/]");
            Console.ReadKey(true);
            return;
        }

        // 加载数据前显示进度（过程可能需要几秒）
        _projectPaths = _dbService.GetProjectPaths();
        _availableTypes = _dbService.GetSessionTypes();

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("yellow"))
            .Start("⏳ 正在加载数据库... (首次加载可能需要几秒)", ctx =>
            {
                ctx.Status("正在获取项目目录列表...");

                ctx.Status("正在加载会话数据...");
                LoadData();
            });

        while (_running)
        {
            Render();
            HandleInput();
        }

        Console.CursorVisible = true;
    }

    /// <summary>加载当前页数据</summary>
    private void LoadData()
    {
        var beforeDate = _timeFilter switch
        {
            "month" => DateTime.Now.AddMonths(-1),
            "half-year" => DateTime.Now.AddMonths(-6),
            _ => (DateTime?)null
        };

        _totalFilteredCount = _dbService.GetFilteredCount(
            string.IsNullOrWhiteSpace(_searchKeyword) ? null : _searchKeyword,
            beforeDate,
            _projectFilter,
            _sessionType);

        _sessions = _dbService.GetSessions(
            _page * PageSize, PageSize,
            string.IsNullOrWhiteSpace(_searchKeyword) ? null : _searchKeyword,
            beforeDate,
            _projectFilter,
            _sessionType);

        // 计算日期分组
        BuildDateGroups();
    }

    /// <summary>构建日期分组索引</summary>
    private void BuildDateGroups()
    {
        _dateGroups.Clear();
        string? currentLabel = null;
        int groupStart = 0;
        for (int i = 0; i < _sessions.Count; i++)
        {
            var label = _sessions[i].DateGroup;
            if (label != currentLabel)
            {
                if (currentLabel is not null)
                {
                    _dateGroups.Add((currentLabel, groupStart, i - groupStart));
                }
                currentLabel = label;
                groupStart = i;
            }
        }
        if (currentLabel is not null)
        {
            _dateGroups.Add((currentLabel, groupStart, _sessions.Count - groupStart));
        }
    }

    /// <summary>清空选中项</summary>
    private void ClearSelection()
    {
        _selectedIds.Clear();
    }

    /// <summary>切换当前行选中状态</summary>
    private void ToggleCurrentSelection()
    {
        if (_cursorIndex < _sessions.Count)
        {
            var id = _sessions[_cursorIndex].Id;
            if (_selectedIds.Contains(id))
                _selectedIds.Remove(id);
            else
                _selectedIds.Add(id);
        }
    }

    /// <summary>全选 / 取消全选</summary>
    private void ToggleSelectAll()
    {
        if (_selectedIds.Count == _sessions.Count && _sessions.Count > 0)
        {
            _selectedIds.Clear();
        }
        else
        {
            foreach (var s in _sessions)
                _selectedIds.Add(s.Id);
        }
    }

    /// <summary>区间选择</summary>
    private void SelectRange(int from, int to)
    {
        var start = Math.Min(from, to);
        var end = Math.Max(from, to);
        for (int i = start; i <= end && i < _sessions.Count; i++)
        {
            _selectedIds.Add(_sessions[i].Id);
        }
    }

    /// <summary>设置状态消息</summary>
    private void SetStatus(string message)
    {
        _statusMessage = message;
        _statusMessageTime = DateTime.Now;
    }

    /// <summary>渲染 TUI 界面</summary>
    private void Render()
    {
        Console.SetCursorPosition(0, 0);

        var windowWidth = Console.WindowWidth;
        var windowHeight = Console.WindowHeight;
        var separator = new string('─', Math.Min(windowWidth, 120));

        // ── 顶部状态栏 ──
        Console.ForegroundColor = ConsoleColor.White;
        Console.BackgroundColor = ConsoleColor.DarkBlue;
        var typeLabel = _sessionType is null ? "全部" : _sessionType;
        var sortIcon = _sortAscending ? "↑" : "↓";
        var statusBar = $" OpenCode 助手 [{_selectedIds.Count}选中 / {_totalFilteredCount}总] | 类型:{typeLabel} | 排序:{sortIcon}时间 | F1帮助 Q退出 ";
        Console.Write(statusBar.PadRight(windowWidth));
        Console.ResetColor();
        Console.WriteLine();

        // ── 筛选信息栏 ──
        var filterInfo = $" 🔍 搜索:[{(string.IsNullOrEmpty(_searchKeyword) ? "无" : _searchKeyword)}]";
        filterInfo += $" | 时间:[{_timeFilter switch { "all" => "全部", "month" => "近1月", "half-year" => "近6月", _ => _timeFilter }}]";
        if (_projectFilter is not null)
            filterInfo += $" | 项目:[{Path.GetFileName(_projectFilter)}]";

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(filterInfo.PadRight(windowWidth));
        Console.ResetColor();

        // ── 状态消息 ──
        if (!string.IsNullOrEmpty(_statusMessage) && (DateTime.Now - _statusMessageTime).TotalSeconds < 5)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($" > {_statusMessage}".PadRight(windowWidth));
            Console.ResetColor();
        }
        else
        {
            Console.WriteLine();
        }

        // ── 会话列表（带日期分组 + 双行布局） ──
        int rowIndex = 0;
        if (_sessions.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(" (无匹配会话)".PadRight(windowWidth));
            Console.ResetColor();
        }
        else
        {
            foreach (var (label, start, count) in _dateGroups)
            {
                // 日期分组标题
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($" ── {label} ({count}条) ──".PadRight(windowWidth));
                Console.ResetColor();

                for (int i = start; i < start + count; i++)
                {
                    var s = _sessions[i];
                    var isSelected = _selectedIds.Contains(s.Id);
                    var isCursor = i == _cursorIndex;
                    var isMain = s.IsMainSession;

                    if (isCursor)
                        Console.BackgroundColor = ConsoleColor.DarkGray;

                    // 选择框 + 类型标记
                    var checkBox = isSelected ? "[✓]" : "[ ]";
                    var typeBadge = isMain ? "●" : "○";

                    // 第1行: 复选框 + 类型标记 + 标题
                    var titleDisplay = s.Title.Length > windowWidth - 10 ? s.Title[..(windowWidth - 13)] + "..." : s.Title;
                    var line1 = $" {checkBox} {typeBadge} {titleDisplay}";

                    if (isSelected && !isCursor)
                        Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine(line1[..Math.Min(line1.Length, windowWidth)]);
                    Console.ResetColor();

                    if (isCursor)
                        Console.BackgroundColor = ConsoleColor.DarkGray;

                    // 第2行: 项目路径 + 时间 + 消息数（灰色，缩进）
                    var projectDisplay = string.IsNullOrEmpty(s.ProjectPath) ? "(全局)" : s.ProjectPath;
                    if (projectDisplay.Length > 50) projectDisplay = "..." + projectDisplay[^47..];
                    var timeDisplay = s.FormattedTime;
                    var meta = $"    {projectDisplay,-50} {timeDisplay}  {s.MessageCount}条消息";

                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine(meta[..Math.Min(meta.Length, windowWidth)]);
                    Console.ResetColor();

                    rowIndex += 2;
                }
            }
        }

        // ── 底部操作栏 ──
        Console.ForegroundColor = ConsoleColor.DarkGray;
        var remainingLines = windowHeight - 6 - rowIndex;
        if (remainingLines > 0)
        {
            for (int i = 0; i < remainingLines && Console.CursorTop < windowHeight - 2; i++)
                Console.WriteLine(new string(' ', windowWidth));
        }

        var helpBar = " ↑↓移动  Space选中  A全选  Shift+↑↓区间  D删除  Enter预览  S搜索  F筛选  T切换类型  R排序  B备份  V收缩  Esc清选";
        Console.ResetColor();
        if (helpBar.Length > windowWidth)
            helpBar = helpBar[..windowWidth];
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(helpBar);
        Console.ResetColor();
    }

    /// <summary>处理键盘输入</summary>
    private void HandleInput()
    {
        var key = Console.ReadKey(true);

        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                if (key.Modifiers.HasFlag(ConsoleModifiers.Shift) && _cursorIndex > 0)
                {
                    SelectRange(_cursorIndex, _cursorIndex - 1);
                }
                // 跳过日期分组标题行
                if (_cursorIndex > 0)
                    _cursorIndex--;
                break;

            case ConsoleKey.DownArrow:
                if (key.Modifiers.HasFlag(ConsoleModifiers.Shift))
                {
                    SelectRange(_cursorIndex, _cursorIndex + 1);
                }
                if (_cursorIndex < _sessions.Count - 1)
                    _cursorIndex++;
                break;

            case ConsoleKey.Spacebar:
                ToggleCurrentSelection();
                break;

            case ConsoleKey.A:
                if (key.Modifiers == 0)
                    ToggleSelectAll();
                break;

            case ConsoleKey.Enter:
                PreviewSession();
                break;

            case ConsoleKey.D:
                if (key.Modifiers == 0)
                    DeleteSelectedSessions();
                break;

            case ConsoleKey.S:
                ShowSearchDialog();
                break;

            case ConsoleKey.F:
                ShowFilterDialog();
                break;

            case ConsoleKey.T:
                if (key.Modifiers == 0)
                    ToggleSessionType();
                break;

            case ConsoleKey.R:
                if (key.Modifiers == 0)
                    ToggleSortOrder();
                break;

            case ConsoleKey.B:
                ShowBackupMenu();
                break;

            case ConsoleKey.V:
                ShowVacuumDialog();
                break;

            case ConsoleKey.F1:
                ShowHelpDialog();
                break;

            case ConsoleKey.Escape:
                if (key.Modifiers.HasFlag(ConsoleModifiers.Control))
                    _running = false;
                else
                    ClearSelection();
                break;

            case ConsoleKey.RightArrow:
            case ConsoleKey.PageDown:
                if ((_page + 1) * PageSize < _totalFilteredCount)
                {
                    _page++;
                    LoadData();
                    _cursorIndex = 0;
                }
                break;

            case ConsoleKey.LeftArrow:
            case ConsoleKey.PageUp:
                if (_page > 0)
                {
                    _page--;
                    LoadData();
                    _cursorIndex = 0;
                }
                break;

            case ConsoleKey.Home:
                _page = 0;
                LoadData();
                _cursorIndex = 0;
                break;

            case ConsoleKey.End:
                _page = Math.Max(0, (_totalFilteredCount - 1) / PageSize);
                LoadData();
                _cursorIndex = 0;
                break;
        }
    }

    /// <summary>切换会话类型显示：全部 ↔ 仅主对话(sisyphus)</summary>
    private void ToggleSessionType()
    {
        if (_sessionType is null)
            _sessionType = DefaultSessionType;
        else
            _sessionType = null;

        _page = 0;
        _cursorIndex = 0;
        _selectedIds.Clear();
        LoadData();
        SetStatus(_sessionType is null ? "显示: 全部类型" : $"显示: 仅 {_sessionType}");
    }

    /// <summary>切换排序：时间升序 ↔ 时间降序</summary>
    private void ToggleSortOrder()
    {
        _sortAscending = !_sortAscending;
        // 反转列表
        _sessions.Reverse();
        SetStatus($"排序: 时间{(_sortAscending ? "↑ 升序" : "↓ 降序")}");
    }

    /// <summary>预览会话内容</summary>
    private void PreviewSession()
    {
        if (_cursorIndex >= _sessions.Count) return;

        var session = _sessions[_cursorIndex];
        var (success, content) = _dbService.GetSessionPreview(session.Id);

        Console.Clear();
        if (success)
        {
            AnsiConsole.Write(new Panel(
                new Markup(EscapeMarkup(content)))
                .Header($"📄 会话预览 — {EscapeMarkup(session.Title)}")
                .Border(BoxBorder.Rounded)
                .Expand());
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]{EscapeMarkup(content)}[/]");
        }

        AnsiConsole.MarkupLine("\n[dim]按任意键返回列表...[/]");
        Console.ReadKey(true);
    }

    /// <summary>显示搜索对话框</summary>
    private void ShowSearchDialog()
    {
        Console.Clear();
        AnsiConsole.Write(new Rule("[yellow]🔍 搜索会话[/]") { Justification = Justify.Left });
        AnsiConsole.MarkupLine("[dim]输入关键词模糊匹配标题或项目路径（留空清除搜索）[/]\n");

        var keyword = AnsiConsole.Ask<string>("搜索关键词:") ?? string.Empty;
        _searchKeyword = keyword.Trim();
        _page = 0;
        _cursorIndex = 0;
        LoadData();
        SetStatus(string.IsNullOrEmpty(_searchKeyword) ? "已清除搜索" : $"搜索: {_searchKeyword}");
    }

    /// <summary>显示筛选对话框</summary>
    private void ShowFilterDialog()
    {
        Console.Clear();
        AnsiConsole.Write(new Rule("[yellow]⏱ 时间筛选[/]") { Justification = Justify.Left });

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("选择时间范围:")
                .PageSize(6)
                .AddChoices(["全部", "近 1 个月", "近 6 个月"]));

        _timeFilter = choice switch
        {
            "近 1 个月" => "month",
            "近 6 个月" => "half-year",
            _ => "all"
        };
        _config.TimeFilter = _timeFilter;

        // 项目目录筛选
        if (_projectPaths.Count > 0)
        {
            var projectChoices = new List<string> { "(全部项目)" };
            projectChoices.AddRange(_projectPaths.Select(p =>
            {
                var display = p.Length > 50 ? "..." + p[^47..] : p;
                return display;
            }));

            AnsiConsole.MarkupLine("\n[dim]按 Enter 跳过项目筛选[/]");

            // 简化处理：只允许用户选择 "全部项目"
            var projectChoice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("项目目录筛选 (Esc=全部):")
                    .PageSize(8)
                    .AddChoices(projectChoices));

            if (projectChoice != "(全部项目)")
            {
                var idx = projectChoices.IndexOf(projectChoice);
                _projectFilter = _projectPaths[idx - 1];
            }
            else
            {
                _projectFilter = null;
            }
        }

        // 会话类型筛选
        AnsiConsole.MarkupLine("\n[bold]会话类型筛选:[/]");
        var typeChoices = new List<string> { "全部类型" };
        if (_availableTypes.Count > 0)
        {
            typeChoices.AddRange(_availableTypes.Select(t => $"仅 {t}"));
        }
        else
        {
            typeChoices.Add("仅 sisyphus (主对话)");
            typeChoices.Add("仅 explore (探索)");
        }

        var typeChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("选择会话类型:")
                .PageSize(5)
                .AddChoices(typeChoices));

        _sessionType = typeChoice switch
        {
            "全部类型" => null,
            "仅 sisyphus (主对话)" => "sisyphus",
            "仅 explore (探索)" => "explore",
            _ when typeChoice.StartsWith("仅 ") => typeChoice[3..].TrimEnd(" (主对话)".ToCharArray()).TrimEnd(" (探索)".ToCharArray()),
            _ => null
        };

        _page = 0;
        _cursorIndex = 0;
        _selectedIds.Clear();
        LoadData();
        SetStatus($"筛选: {choice}, 类型: {typeChoice}");
    }

    /// <summary>批量删除选中的会话</summary>
    private void DeleteSelectedSessions()
    {
        if (_selectedIds.Count == 0)
        {
            SetStatus("没有选中的会话");
            return;
        }

        Console.Clear();

        // ① 展示待删除会话标题预览
        var deleteSessions = _sessions.Where(s => _selectedIds.Contains(s.Id)).ToList();
        var table = new Table();
        table.AddColumn("ID");
        table.AddColumn("标题");
        table.AddColumn("项目");
        table.AddColumn("消息");
        foreach (var s in deleteSessions.Take(15))
        {
            table.AddRow(
                s.Id.Length > 16 ? s.Id[..13] + "..." : s.Id,
                s.Title.Length > 30 ? s.Title[..27] + "..." : s.Title,
                s.ProjectPath.Length > 20 ? "..." + s.ProjectPath[^17..] : s.ProjectPath,
                s.MessageCount.ToString());
        }
        if (deleteSessions.Count > 15)
            table.AddRow("...", $"... 还有 {deleteSessions.Count - 15} 条", "", "");

        AnsiConsole.Write(new Panel(table)
            .Header($"🗑️ 确认删除 — 共 {_selectedIds.Count} 条会话")
            .Border(BoxBorder.Rounded));

        // ② 备份（如果开启）
        if (_config.AutoBackup)
        {
            AnsiConsole.MarkupLine("\n[yellow]⏳ 正在执行删除前自动备份...[/]");
            try
            {
                var backupPath = _backupService.CreateBackup();
                AnsiConsole.MarkupLine($"[green]✓ 备份完成: {EscapeMarkup(Path.GetFileName(backupPath))}[/]");
                LogService.Info($"删除前自动备份: {backupPath}");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗ 备份失败: {EscapeMarkup(ex.Message)}[/]");
                AnsiConsole.MarkupLine("[yellow]是否继续删除? (y/N):[/]");
                if (Console.ReadKey(true).Key != ConsoleKey.Y)
                {
                    SetStatus("删除已取消（备份失败）");
                    return;
                }
            }
        }

        // ③ 二次确认
        AnsiConsole.MarkupLine($"\n[yellow]确认删除这 [bold]{_selectedIds.Count}[/] 条会话？此操作不可撤销！[/]");
        AnsiConsole.MarkupLine("[red]按 Y 确认删除 / 任意键取消:[/]");
        if (Console.ReadKey(true).Key != ConsoleKey.Y)
        {
            SetStatus("已取消删除操作");
            return;
        }

        // ④ 执行删除
        AnsiConsole.MarkupLine("\n[yellow]⏳ 正在删除...[/]");
        try
        {
            var idsToDelete = _selectedIds.ToList();
            var deletedCount = _dbService.DeleteSessions(idsToDelete);

            LogService.Info($"批量删除 {deletedCount} 条会话: {string.Join(", ", idsToDelete)}");

            AnsiConsole.MarkupLine($"[green]✓ 成功删除 {deletedCount} 条会话[/]");

            // 清除选中项并重载数据
            _selectedIds.Clear();
            _page = 0;
            _cursorIndex = 0;
            LoadData();

            // ⑤ 询问是否执行 Vacuum
            AnsiConsole.MarkupLine("\n[yellow]是否执行数据库收缩 (VACUUM) 以回收磁盘空间?[/]");
            AnsiConsole.MarkupLine("[green]按 Y 执行 / 任意键跳过:[/]");
            if (Console.ReadKey(true).Key == ConsoleKey.Y)
            {
                ExecuteVacuum();
            }
            else
            {
                SetStatus($"已删除 {deletedCount} 条会话");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ 删除失败: {EscapeMarkup(ex.Message)}[/]");
            LogService.Error($"删除失败: {ex.Message}");
            SetStatus("删除操作失败");
        }
    }

    /// <summary>显示备份菜单</summary>
    private void ShowBackupMenu()
    {
        Console.Clear();
        AnsiConsole.Write(new Rule("[yellow]💾 备份管理[/]") { Justification = Justify.Left });

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("选择操作:")
                .PageSize(5)
                .AddChoices(["创建备份", "查看历史备份", "清理旧备份", "返回"]));

        switch (choice)
        {
            case "创建备份":
                CreateBackupManually();
                break;
            case "查看历史备份":
                ListBackups();
                break;
            case "清理旧备份":
                CleanupBackups();
                break;
        }
    }

    /// <summary>手动创建备份</summary>
    private void CreateBackupManually()
    {
        Console.Clear();
        AnsiConsole.MarkupLine("[yellow]⏳ 正在创建全库备份...[/]");

        try
        {
            var backupPath = _backupService.CreateBackup();
            AnsiConsole.MarkupLine($"[green]✓ 备份创建成功[/]");
            AnsiConsole.MarkupLine($"[dim]路径: {EscapeMarkup(backupPath)}[/]");
            LogService.Info($"手动备份: {backupPath}");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ 备份失败: {EscapeMarkup(ex.Message)}[/]");
        }

        AnsiConsole.MarkupLine("\n[dim]按任意键返回...[/]");
        Console.ReadKey(true);
    }

    /// <summary>列出历史备份</summary>
    private void ListBackups()
    {
        Console.Clear();
        var backups = _backupService.ListBackups();

        if (backups.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]暂无备份文件[/]");
        }
        else
        {
            var table = new Table();
            table.AddColumn("序号");
            table.AddColumn("文件名");
            table.AddColumn("大小");
            table.AddColumn("创建时间");

            int idx = 1;
            foreach (var b in backups.Take(30))
            {
                table.AddRow(
                    idx.ToString(),
                    b.FileName.Length > 40 ? b.FileName[..37] + "..." : b.FileName,
                    b.FormattedSize,
                    b.FormattedTime);
                idx++;
            }

            AnsiConsole.Write(new Panel(table)
                .Header($"📋 备份列表 ({backups.Count} 个)")
                .Border(BoxBorder.Rounded));
        }

        AnsiConsole.MarkupLine("\n[dim]按任意键返回...[/]");
        Console.ReadKey(true);
    }

    /// <summary>清理旧备份</summary>
    private void CleanupBackups()
    {
        Console.Clear();
        var days = AnsiConsole.Ask<int>("保留最近几天的备份? (默认 30):", 30);
        if (days <= 0) days = 30;

        var deleted = _backupService.CleanupOldBackups(days);
        AnsiConsole.MarkupLine($"[green]已清理 {deleted} 个旧备份文件[/]");

        AnsiConsole.MarkupLine("\n[dim]按任意键返回...[/]");
        Console.ReadKey(true);
    }

    /// <summary>显示 Vacuum 对话框</summary>
    private void ShowVacuumDialog()
    {
        Console.Clear();
        ExecuteVacuum();
        AnsiConsole.MarkupLine("\n[dim]按任意键返回...[/]");
        Console.ReadKey(true);
    }

    /// <summary>执行 VACUUM 操作</summary>
    private void ExecuteVacuum()
    {
        AnsiConsole.MarkupLine("[yellow]⏳ 正在执行数据库收缩 (VACUUM)...[/]");

        try
        {
            // 显示进度条
            AnsiConsole.Progress()
                .Start(ctx =>
                {
                    var task = ctx.AddTask("[yellow]VACUUM 执行中...[/]");
                    task.IsIndeterminate = true;

                    var (success, beforeSize, afterSize, error) = _dbService.Vacuum();

                    task.Value = 100;
                    task.IsIndeterminate = false;

                    if (success)
                    {
                        var saved = beforeSize - afterSize;
                        var savedPercent = beforeSize > 0 ? (saved * 100.0 / beforeSize) : 0;

                        AnsiConsole.MarkupLine($"[green]✓ VACUUM 完成[/]");
                        AnsiConsole.MarkupLine($"  压缩前: [cyan]{FormatBytes(beforeSize)}[/]");
                        AnsiConsole.MarkupLine($"  压缩后: [green]{FormatBytes(afterSize)}[/]");
                        AnsiConsole.MarkupLine($"  节省:   [yellow]{FormatBytes(saved)} ({savedPercent:F1}%)[/]");

                        LogService.Info($"VACUUM 完成: {FormatBytes(beforeSize)} → {FormatBytes(afterSize)}, 节省 {FormatBytes(saved)}");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[red]✗ VACUUM 失败: {EscapeMarkup(error)}[/]");
                        LogService.Error($"VACUUM 失败: {error}");
                    }
                });
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ VACUUM 异常: {EscapeMarkup(ex.Message)}[/]");
        }
    }

    /// <summary>显示帮助对话框</summary>
    private void ShowHelpDialog()
    {
        Console.Clear();

        var helpPanel = new Panel(
            new Markup(
                "[bold]键盘快捷键[/]\n\n" +
                "[green]↑/↓[/]       移动光标\n" +
                "[green]Space[/]      切换当前行选中\n" +
                "[green]A[/]          全选 / 取消全选\n" +
                "[green]Shift+↑/↓[/]  区间批量选中\n" +
                "[green]Enter[/]      预览当前会话内容\n" +
                "[green]D[/]          删除选中的会话\n" +
                "[green]S[/]          搜索（关键字过滤）\n" +
                "[green]F[/]          筛选（时间 / 项目 / 类型）\n" +
                "[green]T[/]          切换类型过滤（全部 ↔ 仅主对话）\n" +
                "[green]R[/]          切换排序（时间升序 / 降序）\n" +
                "[green]B[/]          备份管理\n" +
                "[green]V[/]          数据库收缩 (VACUUM)\n" +
                "[green]Esc[/]        清除所有选中\n" +
                "[green]←/→[/]       上一页 / 下一页\n" +
                "[green]Home/End[/]   首页 / 末页\n" +
                "[green]F1[/]         本帮助\n" +
                "[green]Q[/] / [green]Ctrl+Esc[/]  退出程序\n\n" +
                "[bold]会话类型说明[/]\n" +
                "[yellow]●[/] [bold]主对话 (sisyphus)[/] — 可恢复的主编码对话\n" +
                "[gray]○[/] [dim]其他 (explore 等)[/] — 探索/辅助对话\n" +
                "默认 [green]仅显示主对话[/]，按 [green]T[/] 切换显示全部\n\n" +
                "[bold]命令行模式[/] (无需 TUI)\n" +
                "[gray]  --version              显示版本信息[/]\n" +
                "[gray]  --help                 显示帮助信息[/]\n" +
                "[gray]  --backup-only         仅备份后退出[/]\n" +
                "[gray]  --purge-before 日期    删除指定日期前所有会话[/]\n" +
                "[gray]  --vacuum              仅执行库收缩[/]\n" +
                "[gray]  --db 路径             指定自定义数据库路径[/]\n" +
                "[gray]  --no-auto-backup      关闭删除前自动备份[/]"))
            .Header("📖 帮助")
            .Border(BoxBorder.Rounded)
            .Expand();

        AnsiConsole.Write(helpPanel);
        AnsiConsole.MarkupLine("\n[dim]按任意键返回...[/]");
        Console.ReadKey(true);
    }

    /// <summary>转义 Markup 特殊字符</summary>
    private static string EscapeMarkup(string text)
    {
        return text.Replace("[", "[[").Replace("]", "]]");
    }

    /// <summary>格式化字节数</summary>
    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes / (1024.0 * 1024.0):F1} MB"
        };
    }
}
