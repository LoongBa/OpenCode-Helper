using System.Text.Json;

namespace OpenCodeHelper.Models;

/// <summary>应用程序配置</summary>
public class AppConfig
{
    /// <summary>自定义数据库路径 (null 则使用默认路径)</summary>
    public string? CustomDbPath { get; set; }

    /// <summary>删除前自动备份</summary>
    public bool AutoBackup { get; set; } = true;

    /// <summary>自定义备份目录 (null 则使用数据库同目录)</summary>
    public string? BackupDirectory { get; set; }

    /// <summary>时间筛选: all / month / half-year / custom</summary>
    public string TimeFilter { get; set; } = "all";

    /// <summary>自定义时间筛选的截止日期</summary>
    public DateTime? CustomFilterDate { get; set; }

    /// <summary>加载配置文件</summary>
    public static AppConfig Load(string configPath)
    {
        try
        {
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                return JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.AppConfig) ?? new AppConfig();
            }
        }
        catch
        {
            // 忽略配置加载错误，使用默认值
        }
        return new AppConfig();
    }

    /// <summary>保存配置文件</summary>
    public void Save(string configPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(this, AppConfigJsonContext.Default.AppConfig);
            File.WriteAllText(configPath, json);
        }
        catch
        {
            // 忽略保存错误
        }
    }
}
