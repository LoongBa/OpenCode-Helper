namespace OpenCodeHelper.Models;

/// <summary>OpenCode 会话数据模型</summary>
public class Session
{
    /// <summary>会话 ID (主键)</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>会话标题</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>项目路径</summary>
    public string ProjectPath { get; set; } = string.Empty;

    /// <summary>Agent 类型 (Sisyphus / explore / general 等)</summary>
    public string Agent { get; set; } = string.Empty;

    /// <summary>最后更新时间</summary>
    public DateTime LastUpdatedAt { get; set; }

    /// <summary>消息条数</summary>
    public int MessageCount { get; set; }

    /// <summary>占用大小 (字节)</summary>
    public long SizeBytes { get; set; }

    /// <summary>占用大小的格式化字符串</summary>
    public string FormattedSize => SizeBytes switch
    {
        < 1024 => $"{SizeBytes} B",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:F1} KB",
        _ => $"{SizeBytes / (1024.0 * 1024.0):F1} MB"
    };

    /// <summary>最后更新时间的友好显示</summary>
    public string FormattedTime => LastUpdatedAt.ToString("yyyy-MM-dd HH:mm");

    /// <summary>日期分组标签（纯中文）</summary>
    public string DateGroup => GetDateGroup(LastUpdatedAt);

    /// <summary>是否为 sisyphus 系列主对话</summary>
    public bool IsMainSession =>
        string.IsNullOrEmpty(Agent) ||
        Agent.Contains("sisyphus", StringComparison.OrdinalIgnoreCase);

    /// <summary>根据日期计算分组标签</summary>
    public static string GetDateGroup(DateTime dt)
    {
        var today = DateTime.Today;
        if (dt.Date == today) return "今天";
        if (dt.Date == today.AddDays(-1)) return "昨天";
        if (dt.Date >= today.AddDays(-7)) return "本周";
        if (dt.Date >= today.AddDays(-30)) return "本月";
        if (dt.Year == today.Year) return dt.ToString("M月");
        return dt.ToString("yyyy年M月");
    }
}
