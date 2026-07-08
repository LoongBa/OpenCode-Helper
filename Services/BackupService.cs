using OpenCodeHelper.Models;

namespace OpenCodeHelper.Services;

/// <summary>备份服务 — 负责数据库备份与恢复</summary>
public class BackupService
{
    private readonly string _dbPath;
    private readonly string _backupDir;

    /// <summary>备份目录</summary>
    public string BackupDirectory => _backupDir;

    public BackupService(string dbPath, string? customBackupDir = null)
    {
        _dbPath = dbPath;
        _backupDir = customBackupDir ?? Path.GetDirectoryName(dbPath) ?? ".";
        if (!Directory.Exists(_backupDir))
            Directory.CreateDirectory(_backupDir);
    }

    /// <summary>执行全库备份</summary>
    /// <returns>备份文件路径</returns>
    public string CreateBackup()
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var dbName = Path.GetFileNameWithoutExtension(_dbPath);
        var backupFileName = $"{dbName}_{timestamp}.db";
        var backupPath = Path.Combine(_backupDir, backupFileName);

        File.Copy(_dbPath, backupPath, overwrite: false);
        return backupPath;
    }

    /// <summary>列出所有历史备份文件</summary>
    public List<BackupInfo> ListBackups()
    {
        var result = new List<BackupInfo>();
        if (!Directory.Exists(_backupDir)) return result;

        var dbName = Path.GetFileNameWithoutExtension(_dbPath);
        var pattern = $"{dbName}_*.db";

        try
        {
            foreach (var file in Directory.GetFiles(_backupDir, pattern))
            {
                var fileInfo = new FileInfo(file);
                // 从文件名解析时间戳: {dbName}_yyyyMMdd_HHmmss.db
                var fileName = Path.GetFileNameWithoutExtension(file);
                var timestampPart = fileName.Replace($"{dbName}_", "");
                DateTime createdAt;
                if (DateTime.TryParseExact(timestampPart, "yyyyMMdd_HHmmss", null,
                    System.Globalization.DateTimeStyles.None, out createdAt))
                {
                    result.Add(new BackupInfo
                    {
                        FilePath = file,
                        SizeBytes = fileInfo.Length,
                        CreatedAt = createdAt
                    });
                }
            }
        }
        catch { }

        return result.OrderByDescending(b => b.CreatedAt).ToList();
    }

    /// <summary>恢复指定备份（将备份文件复制回原数据库路径）</summary>
    public (bool success, string error) RestoreBackup(string backupFilePath)
    {
        try
        {
            if (!File.Exists(backupFilePath))
                return (false, "备份文件不存在");

            // 备份当前数据库（作为安全措施）
            var currentBackup = CreateBackup();

            File.Copy(backupFilePath, _dbPath, overwrite: true);
            return (true, $"已恢复备份。原数据库已备份为: {Path.GetFileName(currentBackup)}");
        }
        catch (Exception ex)
        {
            return (false, $"恢复失败: {ex.Message}");
        }
    }

    /// <summary>清理备份文件 — 删除超过指定天数的备份</summary>
    public int CleanupOldBackups(int keepDays = 30)
    {
        int deleted = 0;
        var cutoff = DateTime.Now.AddDays(-keepDays);
        var backups = ListBackups();

        foreach (var backup in backups)
        {
            if (backup.CreatedAt < cutoff)
            {
                try
                {
                    File.Delete(backup.FilePath);
                    deleted++;
                }
                catch { }
            }
        }
        return deleted;
    }
}
