using OpenCodeHelper.Models;
using OpenCodeHelper.Services;

namespace OpenCodeHelper.Services;

// ══════════════════════════════════════════════════════════════
//  屏幕缓冲区 — 支持脏区域追踪和增量渲染
// ══════════════════════════════════════════════════════════════

internal class ScreenBuffer
{
    private char[][] _chars = Array.Empty<char[]>();
    private ConsoleColor?[][] _fgs = Array.Empty<ConsoleColor?[]>();
    private ConsoleColor?[][] _bgs = Array.Empty<ConsoleColor?[]>();
    private int _width, _height;

    public ScreenBuffer(int w = 80, int h = 25) => Resize(w, h);

    public void Resize(int w, int h)
    {
        _width = w; _height = h;
        _chars = new char[h][];
        _fgs = new ConsoleColor?[h][];
        _bgs = new ConsoleColor?[h][];
        for (int y = 0; y < h; y++)
        {
            _chars[y] = new char[w];
            _fgs[y] = new ConsoleColor?[w];
            _bgs[y] = new ConsoleColor?[w];
            Array.Fill(_chars[y], ' ');
        }
    }

    public void Set(int x, int y, char c, ConsoleColor? fg = null, ConsoleColor? bg = null)
    {
        if (x < 0 || x >= _width || y < 0 || y >= _height) return;
        _chars[y][x] = c;
        _fgs[y][x] = fg;
        _bgs[y][x] = bg;
    }

    public void Write(int x, int y, string text, ConsoleColor? fg = null, ConsoleColor? bg = null)
    {
        for (int i = 0; i < text.Length && x + i < _width; i++)
            Set(x + i, y, text[i], fg, bg);
    }

    /// <summary>将缓冲区差异渲染到终端</summary>
    public void Flush(ScreenBuffer? previous = null)
    {
        bool first = previous is null;
        for (int y = 0; y < _height; y++)
        {
            bool lineDirty = first;
            if (!lineDirty)
                for (int x = 0; x < _width; x++)
                    if (_chars[y][x] != previous!._chars[y][x] ||
                        _fgs[y][x] != previous!._fgs[y][x] ||
                        _bgs[y][x] != previous!._bgs[y][x]) { lineDirty = true; break; }

            if (!lineDirty) continue;

            Console.SetCursorPosition(0, y);
            for (int x = 0; x < _width; x++)
            {
                var fg = _fgs[y][x]; var bg = _bgs[y][x];
                if (fg.HasValue) Console.ForegroundColor = fg.Value;
                if (bg.HasValue) Console.BackgroundColor = bg.Value;
                Console.Write(_chars[y][x]);
                Console.ResetColor();
            }
        }
    }

    public void Clear()
    {
        for (int y = 0; y < _height; y++)
        {
            Array.Fill(_chars[y], ' ');
            Array.Fill(_fgs[y], (ConsoleColor?)null);
            Array.Fill(_bgs[y], (ConsoleColor?)null);
        }
    }
}

// ══════════════════════════════════════════════════════════════
//  对话框组件
// ══════════════════════════════════════════════════════════════

internal class Dialog
{
    public string Title { get; set; } = "";
    public List<string> Lines { get; } = new();
    public List<(string key, string label)> Options { get; } = new();

    public void Render(ScreenBuffer buf, int screenW, int screenH)
    {
        int dlgW = Math.Min(72, screenW - 4);
        int dlgH = Math.Min(Lines.Count + 5 + Options.Count, screenH - 4);
        int left = (screenW - dlgW) / 2;
        int top = Math.Max(2, (screenH - dlgH) / 2 - 2);

        // 半透明覆盖
        for (int y = top - 1; y < top + dlgH + 1 && y < screenH; y++)
            for (int x = left - 1; x < left + dlgW + 1 && x < screenW; x++)
                buf.Set(x, y, ' ', null, ConsoleColor.DarkGray);

        var line = new string('═', dlgW - 2);

        // 标题栏
        buf.Write(left, top, $"╔{line}╗", ConsoleColor.White, ConsoleColor.DarkBlue);
        buf.Write(left, top + 1, $"║ {Title.PadRight(dlgW - 3)}║", ConsoleColor.Yellow, ConsoleColor.DarkBlue);
        buf.Write(left, top + 2, $"╠{line}╣", ConsoleColor.White, ConsoleColor.DarkBlue);

        // 内容
        int row = 3;
        foreach (var l in Lines)
        {
            var text = l.Length > dlgW - 4 ? l[..(dlgW - 7)] + "..." : l;
            buf.Write(left, top + row, $"  {text.PadRight(dlgW - 4)}");
            row++;
        }

        // 底部分隔线
        buf.Write(left, top + row, $"╠{line}╣", ConsoleColor.White, ConsoleColor.DarkBlue);
        row++;

        // 选项按钮
        foreach (var (key, label) in Options)
        {
            buf.Write(left, top + row, $"  [{key}] {label}".PadRight(dlgW), ConsoleColor.Green);
            row++;
        }

        // 底部边框
        buf.Write(left, top + row, $"╚{line}╝", ConsoleColor.White, ConsoleColor.DarkBlue);
    }
}

// ══════════════════════════════════════════════════════════════
//  主 TUI 服务
// ══════════════════════════════════════════════════════════════

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

    // — 屏幕 —
    private ScreenBuffer _screen = new();
    private ScreenBuffer _prev = new();
    private int _width, _height;

    public TuiService(DatabaseService dbService, BackupService backupService, AppConfig config)
    {
        _dbService = dbService;
        _backupService = backupService;
        _config = config;
        _timeFilter = config.TimeFilter;
        _sessionType = DefaultSessionType;
    }

    public void Run()
    {
        try { Console.CursorVisible = false; } catch { }

        ShowLoading("正在搜索数据库...", () =>
        {
            _projectPaths = _dbService.GetProjectPaths();
            _availableTypes = _dbService.GetSessionTypes();
            LoadData();
        });

        // 初始渲染
        GetScreenSize();
        MainRender();

        while (_running)
        {
            try { Console.CursorVisible = false; } catch { }
            MainRender();
            HandleInput();
        }

        try { Console.CursorVisible = true; } catch { }
    }

    // ═════════════════════════════════════════════════
    //  屏幕尺寸
    // ═════════════════════════════════════════════════

    private void GetScreenSize()
    {
        try { _width = Console.WindowWidth; _height = Console.WindowHeight; }
        catch { _width = 80; _height = 25; }
    }

    // ═════════════════════════════════════════════════
    //  主渲染
    // ═════════════════════════════════════════════════

    private void MainRender()
    {
        GetScreenSize();
        _screen.Resize(_width, _height);
        _screen.Clear();

        var typeLabel = _sessionType ?? "全部";
        var sortIcon = _sortAscending ? "↑" : "↓";

        // 状态栏（蓝底白字）
        var status = $" OpenCode 助手  [{_selectedIds.Count}选中/{_totalFilteredCount}总]  类型:{typeLabel}  排序:{sortIcon}时间  [F1]帮助 [Q]退出 ";
        _screen.Write(0, 0, status.PadRight(_width), ConsoleColor.White, ConsoleColor.DarkBlue);

        // 筛选栏
        var filter = $" 🔍{(string.IsNullOrEmpty(_searchKeyword) ? "无" : _searchKeyword)}";
        filter += $"  ⏱{(_timeFilter == "all" ? "全部" : _timeFilter == "month" ? "近1月" : "近6月")}";
        if (_projectFilter is not null)
            filter += $"  📁{Path.GetFileName(_projectFilter)}";
        _screen.Write(0, 1, filter.PadRight(_width), ConsoleColor.Cyan);

        // 状态消息（3秒自动消失）
        if (!string.IsNullOrEmpty(_statusMessage) && (DateTime.Now - _statusMessageTime).TotalSeconds < 3)
            _screen.Write(0, 2, $" ⚡{_statusMessage}".PadRight(_width), ConsoleColor.Yellow);

        // 分割线
        _screen.Write(0, 3, new string('─', _width), ConsoleColor.DarkGray);

        // 会话列表
        int row = 4;
        if (_sessions.Count == 0)
        {
            _screen.Write(2, row, "暂无匹配会话，按 F 调整筛选  |  按 T 切换类型  |  按 S 搜索", ConsoleColor.DarkGray);
        }
        else
        {
            foreach (var (label, start, count) in _dateGroups)
            {
                // 日期分组标题（黄色高亮）
                _screen.Write(0, row, $" ── {label} ({count}条) ──".PadRight(_width), ConsoleColor.DarkYellow);
                row++;

                for (int i = start; i < start + count && row < _height - 2; i++)
                {
                    var s = _sessions[i];
                    var sel = _selectedIds.Contains(s.Id);
                    var cur = i == _cursorIndex;
                    var main = s.IsMainSession;

                    // 背景色（光标行）
                    var bg = cur ? ConsoleColor.DarkGray : (ConsoleColor?)null;

                    // 第一行：复选框 + 类型图标 + 标题
                    var cb = sel ? " [*]" : " [ ]";
                    var icon = main ? "●" : "○";
                    var title = s.Title;
                    if (title.Length > _width - 12) title = title[..(_width - 15)] + "...";
                    var line1 = $"{cb} {icon} {title}";
                    var fg = sel && !cur ? ConsoleColor.Yellow : (ConsoleColor?)null;
                    _screen.Write(0, row, line1.PadRight(_width), fg, bg);
                    row++;

                    // 第二行：项目路径 + 时间 + 消息数（缩进+灰色）
                    var proj = string.IsNullOrEmpty(s.ProjectPath) ? "(全局)" : s.ProjectPath;
                    if (proj.Length > 50) proj = "..." + proj[^47..];
                    var meta = $"    {proj,-50} {s.FormattedTime}  {s.MessageCount}条消息";
                    _screen.Write(0, row, meta[..Math.Min(meta.Length, _width)].PadRight(_width), ConsoleColor.DarkGray, cur ? ConsoleColor.DarkGray : null);
                    row++;
                }
            }
        }

        // 填充空白行
        while (row < _height - 1)
        {
            _screen.Write(0, row, new string(' ', _width));
            row++;
        }

        // 底部操作栏（灰色）
        var help = " ↑↓移动  Space选中  A全选  D删除  Enter恢复  P预览  S搜索  F筛选  T类型  R排序  B备份  V收缩  Esc清选  F1帮助";
        _screen.Write(0, _height - 1, help[..Math.Min(help.Length, _width)].PadRight(_width), ConsoleColor.DarkGray);

        // 刷新到终端
        _screen.Flush(_prev);
        (_prev, _screen) = (_screen, _prev);
    }

    // ═════════════════════════════════════════════════
    //  模态对话框
    // ═════════════════════════════════════════════════

    private string ShowDialog(Dialog dlg)
    {
        GetScreenSize();
        _screen.Resize(_width, _height);
        _screen.Clear();

        // 复制主画面做背景
        foreach (var (label, start, count) in _dateGroups)
        {
            _screen.Write(0, start + 4, new string(' ', _width));
        }

        dlg.Render(_screen, _width, _height);
        _screen.Flush(null);

        while (true)
        {
            var key = Console.ReadKey(true);
            foreach (var (k, _) in dlg.Options)
            {
                if (key.KeyChar == k[0] || key.Key == char.ToUpper(k[0]) - 'A' + ConsoleKey.A)
                    return k;
            }
            if (key.Key == ConsoleKey.Escape) return "";
        }
    }

    // ═════════════════════════════════════════════════
    //  数据加载
    // ═════════════════════════════════════════════════

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
            beforeDate, _projectFilter, _sessionType, excludeSubAgents: true);

        _sessions = _dbService.GetSessions(
            _page * PageSize, PageSize,
            string.IsNullOrWhiteSpace(_searchKeyword) ? null : _searchKeyword,
            beforeDate, _projectFilter, _sessionType, excludeSubAgents: true);

        BuildDateGroups();
    }

    private void BuildDateGroups()
    {
        _dateGroups.Clear();
        string? cur = null;
        int start = 0;
        for (int i = 0; i < _sessions.Count; i++)
        {
            var lbl = _sessions[i].DateGroup;
            if (lbl != cur)
            {
                if (cur is not null) _dateGroups.Add((cur, start, i - start));
                cur = lbl; start = i;
            }
        }
        if (cur is not null) _dateGroups.Add((cur, start, _sessions.Count - start));
    }

    // ═════════════════════════════════════════════════
    //  输入处理
    // ═════════════════════════════════════════════════

    private void HandleInput()
    {
        try
        {
            var key = Console.ReadKey(true);
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    if (key.Modifiers.HasFlag(ConsoleModifiers.Shift) && _cursorIndex > 0) SelectRange(_cursorIndex, _cursorIndex - 1);
                    if (_cursorIndex > 0) _cursorIndex--;
                    break;
                case ConsoleKey.DownArrow:
                    if (key.Modifiers.HasFlag(ConsoleModifiers.Shift)) SelectRange(_cursorIndex, _cursorIndex + 1);
                    if (_cursorIndex < _sessions.Count - 1) _cursorIndex++;
                    break;
                case ConsoleKey.Spacebar: ToggleCurrentSelection(); break;
                case ConsoleKey.A when key.Modifiers == 0: ToggleSelectAll(); break;
                case ConsoleKey.Enter: ResumeSession(); break;
                case ConsoleKey.P when key.Modifiers == 0: PreviewSession(); break;
                case ConsoleKey.D when key.Modifiers == 0: DeleteSelectedSessions(); break;
                case ConsoleKey.S when key.Modifiers == 0: ShowSearchDialog(); break;
                case ConsoleKey.F when key.Modifiers == 0: ShowFilterDialog(); break;
                case ConsoleKey.T when key.Modifiers == 0: ToggleSessionType(); break;
                case ConsoleKey.R when key.Modifiers == 0: ToggleSortOrder(); break;
                case ConsoleKey.B when key.Modifiers == 0: ShowBackupMenu(); break;
                case ConsoleKey.V when key.Modifiers == 0: ShowVacuumDialog(); break;
                case ConsoleKey.F1: ShowHelpDialog(); break;
                case ConsoleKey.Escape when key.Modifiers.HasFlag(ConsoleModifiers.Control): _running = false; break;
                case ConsoleKey.Escape: ClearSelection(); break;
                case ConsoleKey.RightArrow:
                case ConsoleKey.PageDown: if ((_page + 1) * PageSize < _totalFilteredCount) { _page++; ResetPage(); } break;
                case ConsoleKey.LeftArrow:
                case ConsoleKey.PageUp: if (_page > 0) { _page--; ResetPage(); } break;
                case ConsoleKey.Home: _page = 0; ResetPage(); break;
                case ConsoleKey.End: _page = Math.Max(0, (_totalFilteredCount - 1) / PageSize); ResetPage(); break;
            }
        }
        catch { _running = false; }
    }

    private void ResetPage() { LoadData(); _cursorIndex = 0; SetStatus($"第 {_page + 1} 页"); }

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
        if (_selectedIds.Count == _sessions.Count && _sessions.Count > 0) _selectedIds.Clear();
        else foreach (var s in _sessions) _selectedIds.Add(s.Id);
    }

    private void SelectRange(int from, int to)
    {
        int s = Math.Min(from, to), e = Math.Max(from, to);
        for (int i = s; i <= e && i < _sessions.Count; i++) _selectedIds.Add(_sessions[i].Id);
    }

    private void ClearSelection() { _selectedIds.Clear(); SetStatus("已清除选中"); }

    private void ToggleSessionType()
    {
        _sessionType = _sessionType is null ? DefaultSessionType : null;
        _page = 0; _cursorIndex = 0; _selectedIds.Clear();
        LoadData();
        SetStatus(_sessionType is null ? "显示: 全部类型" : $"仅 {_sessionType}");
    }

    private void ToggleSortOrder()
    {
        _sortAscending = !_sortAscending;
        _sessions.Reverse();
        BuildDateGroups();
        SetStatus($"排序: 时间{(_sortAscending ? "↑" : "↓")}");
    }

    private void SetStatus(string msg) { _statusMessage = msg; _statusMessageTime = DateTime.Now; }

    // ═════════════════════════════════════════════════
    //  恢复会话
    // ═════════════════════════════════════════════════

    private void ResumeSession()
    {
        if (_cursorIndex >= _sessions.Count) return;
        var s = _sessions[_cursorIndex];

        Console.Clear();
        Console.WriteLine($"🚀 正在启动会话...");
        Console.WriteLine($"  ID:    {s.Id}");
        Console.WriteLine($"  标题:  {s.Title}");
        Console.WriteLine($"  项目:  {s.ProjectPath}");

        var dir = string.IsNullOrEmpty(s.ProjectPath) ? "." : s.ProjectPath;
        if (!Directory.Exists(dir))
        {
            Console.WriteLine($"  目录不存在: {dir}，使用当前目录");
            dir = ".";
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "opencode",
                Arguments = $"-s {s.Id}",
                WorkingDirectory = dir,
                UseShellExecute = true,
            });
            Console.WriteLine($"  ✅ opencode -s {s.Id} 已启动");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  启动失败: {ex.Message}");
            Console.WriteLine("  请确保 opencode 已安装并在 PATH 中");
        }
        PromptAnyKey();
    }

    // ═════════════════════════════════════════════════
    //  预览
    // ═════════════════════════════════════════════════

    private void PreviewSession()
    {
        if (_cursorIndex >= _sessions.Count) return;
        var s = _sessions[_cursorIndex];
        var (ok, content) = _dbService.GetSessionPreview(s.Id);

        Console.Clear();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"━━━ 会话预览 — {s.Title} ━━━");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine(ok ? content : $"[错误] {content}");
        PromptAnyKey();
    }

    // ═════════════════════════════════════════════════
    //  搜索
    // ═════════════════════════════════════════════════

    private void ShowSearchDialog()
    {
        Console.Clear();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("🔍 搜索会话");
        Console.ResetColor();
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

    // ═════════════════════════════════════════════════
    //  筛选
    // ═════════════════════════════════════════════════

    private void ShowFilterDialog()
    {
        Console.Clear();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("⏱ 筛选设置");
        Console.ResetColor();

        Console.WriteLine("\n[时间范围]");
        Console.WriteLine("  1. 全部    2. 近1月    3. 近6月");
        Console.Write("选择 (1-3): ");
        var ch = Console.ReadKey(true).KeyChar;
        _timeFilter = ch switch { '2' => "month", '3' => "half-year", _ => "all" };
        _config.TimeFilter = _timeFilter;

        if (_projectPaths.Count > 0)
        {
            Console.Clear();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("[项目目录]");
            Console.ResetColor();
            Console.WriteLine("  0. (全部)");
            for (int i = 0; i < _projectPaths.Count && i < 20; i++)
            {
                var d = _projectPaths[i].Length > 55 ? "..." + _projectPaths[i][^52..] : _projectPaths[i];
                Console.WriteLine($"  {i + 1}. {d}");
            }
            if (_projectPaths.Count > 20) Console.WriteLine($"  ... 还有 {_projectPaths.Count - 20} 个");
            Console.Write("选择编号 (Enter=全部): ");
            var input = Console.ReadLine()?.Trim();
            if (int.TryParse(input, out int idx) && idx > 0 && idx <= _projectPaths.Count)
                _projectFilter = _projectPaths[idx - 1];
            else _projectFilter = null;
        }

        Console.Clear();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("[会话类型]");
        Console.ResetColor();
        var types = _availableTypes.Count > 0 ? _availableTypes : new List<string> { "sisyphus", "explore" };
        Console.WriteLine("  0. 全部");
        for (int i = 0; i < types.Count; i++)
        {
            var t = types[i].Length > 40 ? types[i][..37] + "..." : types[i];
            Console.WriteLine($"  {i + 1}. {t}");
        }
        Console.Write("选择 (0=全部): ");
        var tc = Console.ReadKey(true).KeyChar;
        var ti = tc - '1';
        _sessionType = (ti >= 0 && ti < types.Count) ? types[ti] : null;

        _page = 0; _cursorIndex = 0; _selectedIds.Clear();
        LoadData();
        SetStatus("筛选完成");
    }

    // ═════════════════════════════════════════════════
    //  删除（模态对话框）
    // ═════════════════════════════════════════════════

    private void DeleteSelectedSessions()
    {
        if (_selectedIds.Count == 0) { SetStatus("没有选中的会话"); return; }

        var delList = _sessions.Where(s => _selectedIds.Contains(s.Id)).ToList();

        var dlg = new Dialog
        {
            Title = $"🗑️ 确认删除 — {_selectedIds.Count} 条",
            Options = { ("Y", "确认删除"), ("N", "取消"), ("B", "备份后删除") }
        };
        foreach (var s in delList.Take(12))
            dlg.Lines.Add($"  {(s.Title.Length > 48 ? s.Title[..45] + "..." : s.Title),-50} {s.MessageCount}条");
        if (delList.Count > 12)
            dlg.Lines.Add($"  ... 还有 {delList.Count - 12} 条");
        dlg.Lines.Add($"  ⚠️ 不可撤销  |  自动备份: {(_config.AutoBackup ? "ON" : "OFF")}");

        var result = ShowDialog(dlg);
        if (result == "N" || result == "") { SetStatus("已取消"); return; }

        bool doBackup = result == "B" || _config.AutoBackup;
        if (doBackup)
        {
            Console.Write("\n正在备份...");
            try
            {
                var bp = _backupService.CreateBackup();
                Console.Write($" 完成: {Path.GetFileName(bp)}");
            }
            catch (Exception ex)
            {
                Console.Write($" 备份失败: {ex.Message}");
                Console.Write(" 继续删除? (y/N): ");
                if (Console.ReadKey(true).Key != ConsoleKey.Y) { SetStatus("已取消"); return; }
            }
        }

        Console.WriteLine("\n正在删除...");
        try
        {
            var ids = _selectedIds.ToList();
            var cnt = _dbService.DeleteSessions(ids);
            LogService.Info($"删除 {cnt} 条: {string.Join(", ", ids)}");
            Console.WriteLine($"✅ 已删除 {cnt} 条");

            _selectedIds.Clear(); _page = 0; _cursorIndex = 0; LoadData();

            Console.Write("执行 VACUUM? (Y/n): ");
            if (Console.ReadKey(true).Key != ConsoleKey.N) ExecuteVacuum();
            else SetStatus($"已删除 {cnt} 条");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ 删除失败: {ex.Message}");
            SetStatus("删除失败");
        }
        PromptAnyKey();
    }

    // ═════════════════════════════════════════════════
    //  备份管理
    // ═════════════════════════════════════════════════

    private void ShowBackupMenu()
    {
        while (true)
        {
            Console.Clear();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("💾 备份管理");
            Console.ResetColor();
            Console.WriteLine("  1. 创建备份  2. 查看历史  3. 清理旧备份  0. 返回");
            Console.Write("选择: ");
            var c = Console.ReadKey(true).KeyChar;
            if (c == '1') { Console.Clear(); CreateBackupManually(); }
            else if (c == '2') { Console.Clear(); ListBackups(); }
            else if (c == '3') { Console.Clear(); CleanupBackups(); }
            else if (c == '0') return;
        }
    }

    private void CreateBackupManually()
    {
        Console.Write("备份中...");
        try
        {
            var path = _backupService.CreateBackup();
            Console.WriteLine($" 完成: {path}");
            LogService.Info($"手动备份: {path}");
        }
        catch (Exception ex) { Console.WriteLine($" 失败: {ex.Message}"); }
        PromptAnyKey();
    }

    private void ListBackups()
    {
        var list = _backupService.ListBackups();
        if (list.Count == 0) Console.WriteLine("暂无备份文件");
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"📋 备份列表 ({list.Count} 个)");
            Console.ResetColor();
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
        Console.Write("保留天数 (默认 30): ");
        Console.CursorVisible = true;
        var input = Console.ReadLine()?.Trim();
        Console.CursorVisible = false;
        int days = int.TryParse(input, out var d) && d > 0 ? d : 30;
        var cnt = _backupService.CleanupOldBackups(days);
        Console.WriteLine($"已清理 {cnt} 个旧备份");
        PromptAnyKey();
    }

    // ═════════════════════════════════════════════════
    //  VACUUM
    // ═════════════════════════════════════════════════

    private void ShowVacuumDialog()
    {
        Console.Clear();
        ExecuteVacuum();
        PromptAnyKey();
    }

    private void ExecuteVacuum()
    {
        Console.Write("正在执行 VACUUM...");
        var (ok, before, after, err) = _dbService.Vacuum();
        if (ok)
        {
            var saved = before - after;
            var pct = before > 0 ? (saved * 100.0 / before) : 0;
            Console.WriteLine($" 完成");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  压缩前: {FormatBytes(before)}");
            Console.WriteLine($"  压缩后: {FormatBytes(after)}");
            Console.WriteLine($"  节省:   {FormatBytes(saved)} ({pct:F1}%)");
            Console.ResetColor();
            LogService.Info($"VACUUM: {FormatBytes(before)} → {FormatBytes(after)}");
        }
        else
        {
            Console.WriteLine($" 失败: {err}");
        }
    }

    // ═════════════════════════════════════════════════
    //  帮助
    // ═════════════════════════════════════════════════

    private void ShowHelpDialog()
    {
        Console.Clear();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("📖 帮助");
        Console.ResetColor();
        Console.WriteLine(new string('─', 60));
        Console.WriteLine(@"
 [导航]
   ↑/↓          移动光标
   ←/→          翻页
   Home/End      首页/末页

 [选择]
   Space         切换选中
   A             全选/取消
   Shift+↑/↓     区间选中
   Esc           清除选中

 [操作]
   Enter         恢复会话 (opencode -s <id>)
   P             预览会话内容
   D             删除选中

 [筛选]
   S             搜索
   F             筛选(时间/项目/类型)
   T             切换类型(全部↔主对话)
   R             排序(时间↑↓)

 [管理]
   B             备份管理
   V             数据库收缩 (VACUUM)

 [系统]
   F1            帮助
   Q/Ctrl+Esc    退出

 [会话类型]
   ● 主对话(sisyphus)  ○ 其他(explore等)
   默认仅显示主对话，按T切换

 [命令行]
   --help       查看全部命令行参数");
        Console.WriteLine(new string('─', 60));
        PromptAnyKey();
    }

    // ═════════════════════════════════════════════════
    //  工具方法
    // ═════════════════════════════════════════════════

    private static void PromptAnyKey()
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("\n按任意键返回...");
        Console.ResetColor();
        Console.ReadKey(true);
    }

    private static void ShowLoading(string message, Action action)
    {
        Console.Clear();
        var frames = new[] { '|', '/', '-', '\\' };
        var done = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var spinner = Task.Run(() =>
        {
            int i = 0;
            while (!done) { Console.Write($"\r {message} {frames[i % 4]}  ({sw.Elapsed.TotalSeconds:F1}s)"); Thread.Sleep(100); i++; }
        });
        action();
        done = true;
        try { spinner.Wait(500); } catch { }
        Console.WriteLine($"\r {message} 完成! ({sw.Elapsed.TotalSeconds:F1}s)" + new string(' ', 10));
        Thread.Sleep(200);
    }

    internal static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB"
    };
}
