using OpenCodeHelper.Models;
using OpenCodeHelper.Services;

namespace OpenCodeHelper.Services;

/// <summary>终端 UI 服务 — 纯 System.Console 实现</summary>
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
    private string? _sessionType = "sisyphus";
    private List<string> _availableTypes = new();
    private const string DefaultSessionType = "sisyphus";

    // — 排序 —
    private bool _sortAscending;

    // — 日期分组 —
    private readonly List<(string label, int start, int count)> _dateGroups = new();

    private string _timeFilter = "all";
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
        _sessionType = DefaultSessionType;
    }

    // ══════════════════════════════════════════════
    //  主循环
    // ══════════════════════════════════════════════

    public void Run()
    {
        Console.CursorVisible = false;

        // 校验 + 连接
        var (valid, err) = _dbService.ValidateDatabase();
        if (!valid)
        {
            WriteError($"数据库错误: {err}");
            PromptAnyKey();
            return;
        }
        if (!_dbService.Open())
        {
            WriteError("无法打开数据库连接");
            PromptAnyKey();
            return;
        }

        ShowLoading("正在加载数据...", () =>
        {
            _projectPaths = _dbService.GetProjectPaths();
            _availableTypes = _dbService.GetSessionTypes();
            LoadData();
        });

        while (_running)
        {
            Render();
            HandleInput();
        }

        Console.CursorVisible = true;
    }

    // ══════════════════════════════════════════════
    //  数据加载
    // ══════════════════════════════════════════════

    private void LoadData()
    {
        DateTime? beforeDate = _timeFilter switch
        {
            "month" => DateTime.Now.AddMonths(-1),
            "half-year" => DateTime.Now.AddMonths(-6),
            _ => null
        };

        _totalFilteredCount = _dbService.GetFilteredCount(
            string.IsNullOrWhiteSpace(_searchKeyword) ? null : _searchKeyword,
            beforeDate, _projectFilter, _sessionType);

        _sessions = _dbService.GetSessions(
            _page * PageSize, PageSize,
            string.IsNullOrWhiteSpace(_searchKeyword) ? null : _searchKeyword,
            beforeDate, _projectFilter, _sessionType);

        BuildDateGroups();
    }

    private void BuildDateGroups()
    {
        _dateGroups.Clear();
        string? current = null;
        int start = 0;
        for (int i = 0; i < _sessions.Count; i++)
        {
            var label = _sessions[i].DateGroup;
            if (label != current)
            {
                if (current is not null)
                    _dateGroups.Add((current, start, i - start));
                current = label;
                start = i;
            }
        }
        if (current is not null)
            _dateGroups.Add((current, start, _sessions.Count - start));
    }

    // ══════════════════════════════════════════════
    //  渲染
    // ══════════════════════════════════════════════

    private void Render()
    {
        Console.SetCursorPosition(0, 0);
        var w = Console.WindowWidth;
        var h = Console.WindowHeight;

        // 状态栏
        var typeLabel = _sessionType ?? "全部";
        var sortIcon = _sortAscending ? "↑" : "↓";
        var status = $" OpenCode 助手 [{_selectedIds.Count}选中/{_totalFilteredCount}总] 类型:{typeLabel} 排序:{sortIcon}时间  F1帮助 Q退出 ";
        WriteColor(status.PadRight(w), ConsoleColor.White, ConsoleColor.DarkBlue);
        Console.WriteLine();

        // 筛选栏
        var filter = $" 搜索:[{(string.IsNullOrEmpty(_searchKeyword) ? "无" : _searchKeyword)}]";
        filter += $" 时间:[{(_timeFilter == "all" ? "全部" : _timeFilter == "month" ? "近1月" : "近6月")}]";
        if (_projectFilter is not null)
            filter += $" 项目:[{Path.GetFileName(_projectFilter)}]";
        WriteLineColor(filter.PadRight(w), ConsoleColor.Cyan);
        Console.ResetColor();

        // 状态消息
        if (!string.IsNullOrEmpty(_statusMessage) && (DateTime.Now - _statusMessageTime).TotalSeconds < 5)
            WriteLineColor($" > {_statusMessage}".PadRight(w), ConsoleColor.Yellow);
        else
            Console.WriteLine();

        // 会话列表
        int rows = 4;
        if (_sessions.Count == 0)
        {
            WriteLineColor(" (无匹配会话)".PadRight(w), ConsoleColor.DarkGray);
            rows++;
        }
        else
        {
            foreach (var (label, start, count) in _dateGroups)
            {
                WriteLineColor($" ── {label} ({count}条) ──".PadRight(w), ConsoleColor.DarkYellow);
                rows++;

                for (int i = start; i < start + count; i++)
                {
                    var s = _sessions[i];
                    var sel = _selectedIds.Contains(s.Id);
                    var cur = i == _cursorIndex;
                    var main = s.IsMainSession;

                    if (cur) Console.BackgroundColor = ConsoleColor.DarkGray;

                    var cb = sel ? "[*]" : "[ ]";
                    var icon = main ? "●" : "○";
                    var title = s.Title.Length > w - 10 ? s.Title[..(w - 13)] + "..." : s.Title;
                    var line1 = $" {cb} {icon} {title}";
                    if (sel && !cur) Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine(line1[..Math.Min(line1.Length, w)]);
                    Console.ResetColor();
                    if (cur) Console.BackgroundColor = ConsoleColor.DarkGray;

                    var proj = string.IsNullOrEmpty(s.ProjectPath) ? "(全局)" : s.ProjectPath;
                    if (proj.Length > 50) proj = "..." + proj[^47..];
                    var meta = $"    {proj,-50} {s.FormattedTime}  {s.MessageCount}条";
                    WriteLineColor(meta[..Math.Min(meta.Length, w)], ConsoleColor.DarkGray);

                    rows += 2;
                }
            }
        }

        // 填充剩余行
        for (int r = rows; r < h - 2 && Console.CursorTop < h - 2; r++)
            Console.WriteLine(new string(' ', w));

        // 底部帮助栏
        var help = " ↑↓移动 Space选中 A全选 Shift+↑↓区间 D删除 Enter预览 S搜索 F筛选 T类型 R排序 B备份 V收缩 Esc清选";
        WriteLineColor(help[..Math.Min(help.Length, w)], ConsoleColor.DarkGray);
    }

    // ══════════════════════════════════════════════
    //  键盘输入
    // ══════════════════════════════════════════════

    private void HandleInput()
    {
        var key = Console.ReadKey(true);

        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                if (key.Modifiers.HasFlag(ConsoleModifiers.Shift) && _cursorIndex > 0)
                    SelectRange(_cursorIndex, _cursorIndex - 1);
                if (_cursorIndex > 0) _cursorIndex--;
                break;
            case ConsoleKey.DownArrow:
                if (key.Modifiers.HasFlag(ConsoleModifiers.Shift))
                    SelectRange(_cursorIndex, _cursorIndex + 1);
                if (_cursorIndex < _sessions.Count - 1) _cursorIndex++;
                break;
            case ConsoleKey.Spacebar:
                ToggleCurrentSelection();
                break;
            case ConsoleKey.A when key.Modifiers == 0:
                ToggleSelectAll();
                break;
            case ConsoleKey.Enter:
                PreviewSession();
                break;
            case ConsoleKey.D when key.Modifiers == 0:
                DeleteSelectedSessions();
                break;
            case ConsoleKey.S when key.Modifiers == 0:
                ShowSearchDialog();
                break;
            case ConsoleKey.F when key.Modifiers == 0:
                ShowFilterDialog();
                break;
            case ConsoleKey.T when key.Modifiers == 0:
                ToggleSessionType();
                break;
            case ConsoleKey.R when key.Modifiers == 0:
                ToggleSortOrder();
                break;
            case ConsoleKey.B when key.Modifiers == 0:
                ShowBackupMenu();
                break;
            case ConsoleKey.V when key.Modifiers == 0:
                ShowVacuumDialog();
                break;
            case ConsoleKey.F1:
                ShowHelpDialog();
                break;
            case ConsoleKey.Escape when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                _running = false; break;
            case ConsoleKey.Escape:
                ClearSelection(); break;
            case ConsoleKey.RightArrow:
            case ConsoleKey.PageDown:
                if ((_page + 1) * PageSize < _totalFilteredCount) { _page++; ResetPage(); }
                break;
            case ConsoleKey.LeftArrow:
            case ConsoleKey.PageUp:
                if (_page > 0) { _page--; ResetPage(); }
                break;
            case ConsoleKey.Home:
                _page = 0; ResetPage(); break;
            case ConsoleKey.End:
                _page = Math.Max(0, (_totalFilteredCount - 1) / PageSize); ResetPage(); break;
        }
    }

    private void ResetPage() { LoadData(); _cursorIndex = 0; }

    // ══════════════════════════════════════════════
    //  选中操作
    // ══════════════════════════════════════════════

    private void ToggleCurrentSelection()
    {
        if (_cursorIndex < _sessions.Count)
        {
            var id = _sessions[_cursorIndex].Id;
            if (!_selectedIds.Remove(id)) _selectedIds.Add(id);
        }
    }

    private void ToggleSelectAll()
    {
        if (_selectedIds.Count == _sessions.Count && _sessions.Count > 0)
            _selectedIds.Clear();
        else
            foreach (var s in _sessions) _selectedIds.Add(s.Id);
    }

    private void SelectRange(int from, int to)
    {
        int start = Math.Min(from, to), end = Math.Max(from, to);
        for (int i = start; i <= end && i < _sessions.Count; i++)
            _selectedIds.Add(_sessions[i].Id);
    }

    private void ClearSelection() { _selectedIds.Clear(); }

    // ══════════════════════════════════════════════
    //  类型/排序切换
    // ══════════════════════════════════════════════

    private void ToggleSessionType()
    {
        _sessionType = _sessionType is null ? DefaultSessionType : null;
        _page = 0; _cursorIndex = 0; _selectedIds.Clear();
        LoadData();
        SetStatus(_sessionType is null ? "显示: 全部类型" : $"显示: 仅 {_sessionType}");
    }

    private void ToggleSortOrder()
    {
        _sortAscending = !_sortAscending;
        _sessions.Reverse();
        BuildDateGroups();
        SetStatus($"排序: 时间{(_sortAscending ? "↑ 升序" : "↓ 降序")}");
    }

    // ══════════════════════════════════════════════
    //  状态栏
    // ══════════════════════════════════════════════

    private void SetStatus(string msg) { _statusMessage = msg; _statusMessageTime = DateTime.Now; }

    // ══════════════════════════════════════════════
    //  预览
    // ══════════════════════════════════════════════

    private void PreviewSession()
    {
        if (_cursorIndex >= _sessions.Count) return;
        var s = _sessions[_cursorIndex];
        var (ok, content) = _dbService.GetSessionPreview(s.Id);

        Console.Clear();
        WriteLineColor($"━━━ 会话预览 — {s.Title} ━━━", ConsoleColor.Yellow);
        Console.WriteLine();
        Console.WriteLine(ok ? content : $"[错误] {content}");
        Console.WriteLine();
        PromptAnyKey();
    }

    // ══════════════════════════════════════════════
    //  搜索
    // ══════════════════════════════════════════════

    private void ShowSearchDialog()
    {
        Console.Clear();
        WriteLineColor("🔍 搜索会话", ConsoleColor.Yellow);
        Console.WriteLine("输入关键词模糊匹配标题或项目路径（留空清除）");
        Console.Write("> ");
        Console.CursorVisible = true;
        var input = Console.ReadLine() ?? "";
        Console.CursorVisible = false;
        _searchKeyword = input.Trim();
        _page = 0; _cursorIndex = 0;
        LoadData();
        SetStatus(string.IsNullOrEmpty(_searchKeyword) ? "已清除搜索" : $"搜索: {_searchKeyword}");
    }

    // ══════════════════════════════════════════════
    //  筛选
    // ══════════════════════════════════════════════

    private void ShowFilterDialog()
    {
        Console.Clear();
        WriteLineColor("⏱ 筛选设置", ConsoleColor.Yellow);

        WriteLineColor("\n[时间范围]", ConsoleColor.Cyan);
        Console.WriteLine("  1. 全部");
        Console.WriteLine("  2. 近 1 个月");
        Console.WriteLine("  3. 近 6 个月");
        Console.Write("选择 (1-3): ");

        var choice = Console.ReadKey(true).KeyChar;
        _timeFilter = choice switch { '2' => "month", '3' => "half-year", _ => "all" };
        _config.TimeFilter = _timeFilter;

        // 项目目录筛选
        if (_projectPaths.Count > 0)
        {
            Console.Clear();
            WriteLineColor("\n[项目目录筛选]", ConsoleColor.Cyan);
            Console.WriteLine("  0. (全部项目)");
            for (int i = 0; i < _projectPaths.Count && i < 20; i++)
            {
                var d = _projectPaths[i].Length > 50 ? "..." + _projectPaths[i][^47..] : _projectPaths[i];
                Console.WriteLine($"  {i + 1}. {d}");
            }
            if (_projectPaths.Count > 20) Console.WriteLine($"  ... 还有 {_projectPaths.Count - 20} 个");
            Console.Write("选择编号 (Enter=全部): ");
            var input = Console.ReadLine()?.Trim();
            if (int.TryParse(input, out int idx) && idx > 0 && idx <= _projectPaths.Count)
                _projectFilter = _projectPaths[idx - 1];
            else
                _projectFilter = null;
        }

        // 会话类型筛选
        Console.Clear();
        WriteLineColor("\n[会话类型]", ConsoleColor.Cyan);
        Console.WriteLine("  0. 全部类型");
        var typeOptions = _availableTypes.Count > 0 ? _availableTypes : new List<string> { "sisyphus", "explore" };
        for (int i = 0; i < typeOptions.Count; i++)
            Console.WriteLine($"  {i + 1}. 仅 {typeOptions[i]}");
        Console.Write("选择 (0=全部): ");
        var tc = Console.ReadKey(true).KeyChar;
        var ti = tc - '1';
        _sessionType = (ti >= 0 && ti < typeOptions.Count) ? typeOptions[ti] : null;

        _page = 0; _cursorIndex = 0; _selectedIds.Clear();
        LoadData();
        SetStatus($"筛选完成");
    }

    // ══════════════════════════════════════════════
    //  删除
    // ══════════════════════════════════════════════

    private void DeleteSelectedSessions()
    {
        if (_selectedIds.Count == 0) { SetStatus("没有选中的会话"); return; }

        Console.Clear();
        var delList = _sessions.Where(s => _selectedIds.Contains(s.Id)).ToList();

        WriteLineColor($"🗑️ 确认删除 — 共 {_selectedIds.Count} 条会话", ConsoleColor.Yellow);
        Console.WriteLine(new string('─', 60));
        foreach (var s in delList.Take(15))
        {
            var t = s.Title.Length > 40 ? s.Title[..37] + "..." : s.Title;
            Console.WriteLine($"  {t,-42} {s.MessageCount}条");
        }
        if (delList.Count > 15) Console.WriteLine($"  ... 还有 {delList.Count - 15} 条");
        Console.WriteLine(new string('─', 60));

        // 自动备份
        if (_config.AutoBackup)
        {
            Console.Write("正在执行删除前自动备份...");
            try
            {
                var bp = _backupService.CreateBackup();
                WriteLineColor($" 已完成: {Path.GetFileName(bp)}", ConsoleColor.Green);
            }
            catch (Exception ex)
            {
                WriteLineColor($" 备份失败: {ex.Message}", ConsoleColor.Red);
                Console.Write("是否继续删除? (y/N): ");
                if (Console.ReadKey(true).Key != ConsoleKey.Y) { SetStatus("已取消"); return; }
            }
        }

        Console.Write($"\n确认删除 {_selectedIds.Count} 条？此操作不可撤销！(y/N): ");
        if (Console.ReadKey(true).Key != ConsoleKey.Y) { SetStatus("已取消"); return; }

        Console.WriteLine("\n正在删除...");
        try
        {
            var ids = _selectedIds.ToList();
            var cnt = _dbService.DeleteSessions(ids);
            LogService.Info($"批量删除 {cnt} 条: {string.Join(", ", ids)}");
            WriteLineColor($"成功删除 {cnt} 条", ConsoleColor.Green);

            _selectedIds.Clear(); _page = 0; _cursorIndex = 0; LoadData();

            Console.Write("\n是否执行 VACUUM 回收磁盘空间? (Y/n): ");
            if (Console.ReadKey(true).Key != ConsoleKey.N) ExecuteVacuum();
            else SetStatus($"已删除 {cnt} 条");
        }
        catch (Exception ex)
        {
            WriteLineColor($"删除失败: {ex.Message}", ConsoleColor.Red);
            SetStatus("删除操作失败");
        }
    }

    // ══════════════════════════════════════════════
    //  备份管理
    // ══════════════════════════════════════════════

    private void ShowBackupMenu()
    {
        while (true)
        {
            Console.Clear();
            WriteLineColor("💾 备份管理", ConsoleColor.Yellow);
            Console.WriteLine("  1. 创建备份");
            Console.WriteLine("  2. 查看历史备份");
            Console.WriteLine("  3. 清理旧备份");
            Console.WriteLine("  0. 返回");
            Console.Write("选择: ");
            var c = Console.ReadKey(true).KeyChar;
            switch (c)
            {
                case '1': CreateBackupManually(); break;
                case '2': ListBackups(); break;
                case '3': CleanupBackups(); break;
                case '0': return;
            }
        }
    }

    private void CreateBackupManually()
    {
        Console.Clear();
        Console.Write("正在创建全库备份...");
        try
        {
            var path = _backupService.CreateBackup();
            WriteLineColor($" 完成: {path}", ConsoleColor.Green);
            LogService.Info($"手动备份: {path}");
        }
        catch (Exception ex) { WriteLineColor($" 失败: {ex.Message}", ConsoleColor.Red); }
        PromptAnyKey();
    }

    private void ListBackups()
    {
        Console.Clear();
        var list = _backupService.ListBackups();
        if (list.Count == 0) { WriteLineColor("暂无备份文件", ConsoleColor.Yellow); }
        else
        {
            WriteLineColor($"📋 备份列表 ({list.Count} 个)", ConsoleColor.Yellow);
            Console.WriteLine(new string('─', 80));
            int i = 1;
            foreach (var b in list.Take(30))
            {
                var fn = b.FileName.Length > 50 ? b.FileName[..47] + "..." : b.FileName;
                Console.WriteLine($"  {i,-3} {fn,-52} {b.FormattedSize,8}  {b.FormattedTime}");
                i++;
            }
        }
        PromptAnyKey();
    }

    private void CleanupBackups()
    {
        Console.Clear();
        Console.Write("保留最近几天的备份? (默认 30): ");
        Console.CursorVisible = true;
        var input = Console.ReadLine()?.Trim();
        Console.CursorVisible = false;
        int days = int.TryParse(input, out var d) && d > 0 ? d : 30;
        var cnt = _backupService.CleanupOldBackups(days);
        WriteLineColor($"已清理 {cnt} 个旧备份文件", ConsoleColor.Green);
        PromptAnyKey();
    }

    // ══════════════════════════════════════════════
    //  VACUUM
    // ══════════════════════════════════════════════

    private void ShowVacuumDialog()
    {
        Console.Clear();
        ExecuteVacuum();
        PromptAnyKey();
    }

    private void ExecuteVacuum()
    {
        Console.WriteLine("正在执行数据库收缩 (VACUUM)...");
        AnimateSpinner(() =>
        {
            var (ok, before, after, err) = _dbService.Vacuum();
            if (ok)
            {
                var saved = before - after;
                var pct = before > 0 ? (saved * 100.0 / before) : 0;
                WriteLineColor($"\nVACUUM 完成", ConsoleColor.Green);
                Console.WriteLine($"  压缩前: {FormatBytes(before)}");
                Console.WriteLine($"  压缩后: {FormatBytes(after)}");
                Console.WriteLine($"  节省:   {FormatBytes(saved)} ({pct:F1}%)");
                LogService.Info($"VACUUM: {FormatBytes(before)} → {FormatBytes(after)}");
            }
            else
            {
                WriteLineColor($"\nVACUUM 失败: {err}", ConsoleColor.Red);
            }
        });
    }

    // ══════════════════════════════════════════════
    //  帮助
    // ══════════════════════════════════════════════

    private void ShowHelpDialog()
    {
        Console.Clear();
        WriteLineColor("📖 帮助", ConsoleColor.Yellow);
        Console.WriteLine(new string('─', 60));
        Console.WriteLine(@"[键盘快捷键]
  ↑/↓        移动光标
  Space      切换当前行选中
  A          全选/取消全选
  Shift+↑/↓  区间选中
  Enter      预览会话
  D          删除选中
  S          搜索
  F          筛选(时间/项目/类型)
  T          切换类型(全部↔主对话)
  R          切换排序
  B          备份管理
  V          数据库收缩
  Esc        清除选中
  ←/→        翻页
  Home/End   首页/末页
  F1         帮助
  Q/Esc      退出

[会话类型]
  ● 主对话(sisyphus) — 可恢复
  ○ 其他(explore等) — 默认隐藏，按T切换

[命令行参数]
  --version             显示版本
  --help                显示帮助
  --backup-only         仅备份后退出
  --purge-before 日期   删除指定日期前会话
  --vacuum              仅库收缩
  --db 路径             自定义数据库路径
  --no-auto-backup      关闭自动备份");
        Console.WriteLine(new string('─', 60));
        PromptAnyKey();
    }

    // ══════════════════════════════════════════════
    //  工具方法
    // ══════════════════════════════════════════════

    private static void WriteColor(string text, ConsoleColor fg, ConsoleColor? bg = null)
    {
        Console.ForegroundColor = fg;
        if (bg.HasValue) Console.BackgroundColor = bg.Value;
        Console.Write(text);
        Console.ResetColor();
    }

    private static void WriteLineColor(string text, ConsoleColor fg)
    {
        Console.ForegroundColor = fg;
        Console.WriteLine(text);
        Console.ResetColor();
    }

    private static void WriteError(string msg)
    {
        WriteLineColor($"错误: {msg}", ConsoleColor.Red);
    }

    private static void PromptAnyKey()
    {
        WriteLineColor("\n按任意键返回...", ConsoleColor.DarkGray);
        Console.ReadKey(true);
    }

    private static void AnimateSpinner(Action action)
    {
        var frames = new[] { '|', '/', '-', '\\' };
        int i = 0;
        var task = Task.Run(action);
        while (!task.IsCompleted)
        {
            Console.Write($"\r  正在执行... {frames[i % 4]}");
            i++;
            Thread.Sleep(100);
        }
        Console.Write("\r" + new string(' ', Console.WindowWidth) + "\r");
        task.Wait();
    }

    private static void ShowLoading(string message, Action action)
    {
        Console.Clear();
        var frames = new[] { '|', '/', '-', '\\' };
        int i = 0;
        var task = Task.Run(action);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            Console.Write($"\r {message} {frames[i % 4]}  ({sw.Elapsed.TotalSeconds:F1}s)");
            i++;
            Thread.Sleep(100);
        }
        Console.WriteLine($"\r {message} 完成! ({sw.Elapsed.TotalSeconds:F1}s)" + new string(' ', 10));
        Thread.Sleep(300);
        task.Wait();
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB"
    };
}
