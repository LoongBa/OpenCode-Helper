namespace OpenCodeHelper.Models;

/// <summary>备份文件信息</summary>
public class BackupInfo
{
    /// <summary>备份文件完整路径</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>备份文件大小 (字节)</summary>
    public long SizeBytes { get; set; }

    /// <summary>创建时间 (从文件名解析)</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>文件名</summary>
    public string FileName => Path.GetFileName(FilePath);

    /// <summary>格式化的文件大小</summary>
    public string FormattedSize => SizeBytes switch
    {
        < 1024 => $"{SizeBytes} B",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:F1} KB",
        _ => $"{SizeBytes / (1024.0 * 1024.0):F1} MB"
    };

    /// <summary>格式化的创建时间</summary>
    public string FormattedTime => CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");
}
