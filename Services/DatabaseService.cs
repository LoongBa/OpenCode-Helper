using Microsoft.Data.Sqlite;
using OpenCodeHelper.Models;

namespace OpenCodeHelper.Services;

/// <summary>数据库服务 — 负责 SQLite 连接、查询、删除、收缩操作</summary>
public class DatabaseService : IDisposable
{
    private readonly string _dbPath;
    private SqliteConnection? _connection;
    private long _dbFileSize;

    /// <summary>数据库文件路径</summary>
    public string DbPath => _dbPath;

    /// <summary>数据库文件大小 (字节)</summary>
    public long DbFileSize => _dbFileSize;

    /// <summary>数据库是否已连接</summary>
    public bool IsConnected => _connection is not null;

    /// <summary>
    /// 获取默认的 OpenCode 数据库路径
    /// </summary>
    public static string GetDefaultDbPath()
    {
        var userProfile = Environment.GetEnvironmentVariable("USERPROFILE")
            ?? Environment.GetEnvironmentVariable("HOME")
            ?? "C:\\Users\\Default";
        return Path.Combine(userProfile, ".local", "share", "opencode", "opencode.db");
    }

    public DatabaseService(string? customDbPath = null)
    {
        _dbPath = customDbPath ?? GetDefaultDbPath();
        RefreshFileSize();
    }

    /// <summary>刷新数据库文件大小记录</summary>
    private void RefreshFileSize()
    {
        try
        {
            _dbFileSize = File.Exists(_dbPath) ? new FileInfo(_dbPath).Length : 0;
        }
        catch
        {
            _dbFileSize = 0;
        }
    }

    /// <summary>校验数据库文件是否存在且有效</summary>
    public (bool success, string error) ValidateDatabase()
    {
        if (!File.Exists(_dbPath))
            return (false, $"数据库文件不存在: {_dbPath}");

        try
        {
            // 尝试打开并执行一个简单查询验证完整性
            using var testConn = new SqliteConnection($"Data Source={_dbPath}");
            testConn.Open();

            using var cmd = testConn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table'";
            cmd.ExecuteScalar();

            // 运行 PRAGMA integrity_check
            cmd.CommandText = "PRAGMA integrity_check";
            var result = cmd.ExecuteScalar()?.ToString();
            if (result != "ok")
                return (false, $"数据库完整性检查失败: {result}");

            testConn.Close();
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, $"数据库打开失败: {ex.Message}");
        }
    }

    /// <summary>打开数据库连接</summary>
    public bool Open()
    {
        try
        {
            _connection = new SqliteConnection($"Data Source={_dbPath}");
            _connection.Open();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>获取会话总数</summary>
    public int GetTotalSessionCount()
    {
        if (_connection is null) return 0;
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM conversations";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>分页查询会话列表</summary>
    public List<Session> GetSessions(int offset, int limit, string? searchKeyword = null,
        DateTime? beforeDate = null, string? projectPath = null, string? sessionType = null)
    {
        var result = new List<Session>();
        if (_connection is null) return result;

        try
        {
            var whereClauses = new List<string>();
            var parameters = new List<(string name, object? value)>();

            if (!string.IsNullOrWhiteSpace(searchKeyword))
            {
                whereClauses.Add("(c.title LIKE @keyword OR c.project_path LIKE @keyword)");
                parameters.Add(("@keyword", $"%{searchKeyword}%"));
            }

            if (beforeDate.HasValue)
            {
                whereClauses.Add("c.updated_at < @beforeDate");
                parameters.Add(("@beforeDate", beforeDate.Value.ToString("yyyy-MM-dd HH:mm:ss")));
            }

            if (!string.IsNullOrWhiteSpace(projectPath))
            {
                whereClauses.Add("c.project_path = @projectPath");
                parameters.Add(("@projectPath", projectPath));
            }

            if (!string.IsNullOrWhiteSpace(sessionType))
            {
                whereClauses.Add("c.type = @sessionType");
                parameters.Add(("@sessionType", sessionType));
            }

            var whereSql = whereClauses.Count > 0
                ? "WHERE " + string.Join(" AND ", whereClauses)
                : string.Empty;

            // 先尝试带 type 列查询，如果列不存在则回退到不含 type 的查询
            var sql = $@"
                SELECT c.id, c.title, c.project_path, c.updated_at,
                       (SELECT COUNT(*) FROM messages m WHERE m.conversation_id = c.id) AS msg_count,
                       (SELECT COALESCE(SUM(LENGTH(content)), 0) FROM messages m WHERE m.conversation_id = c.id) AS size_bytes,
                       COALESCE(c.type, '') AS type
                FROM conversations c
                {whereSql}
                ORDER BY c.updated_at DESC
                LIMIT @limit OFFSET @offset";

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@limit", limit);
            cmd.Parameters.AddWithValue("@offset", offset);

            foreach (var (name, value) in parameters)
            {
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new Session
                {
                    Id = reader.GetString(0),
                    Title = reader.IsDBNull(1) ? "(无标题)" : reader.GetString(1),
                    ProjectPath = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    LastUpdatedAt = reader.IsDBNull(3) ? DateTime.MinValue : reader.GetDateTime(3),
                    MessageCount = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                    SizeBytes = reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
                    Type = reader.IsDBNull(6) ? "" : reader.GetString(6)
                });
            }

            return result;
        }
        catch (Microsoft.Data.Sqlite.SqliteException) when (result.Count == 0)
        {
            // type 列不存在，降级查询
            return GetSessionsLegacy(offset, limit, searchKeyword, beforeDate, projectPath);
        }
        catch
        {
            return result;
        }
    }

    /// <summary>降级查询 — 不含 type 列的旧版数据库</summary>
    private List<Session> GetSessionsLegacy(int offset, int limit, string? searchKeyword = null,
        DateTime? beforeDate = null, string? projectPath = null)
    {
        var result = new List<Session>();
        if (_connection is null) return result;
        try
        {
            var whereClauses = new List<string>();
            var parameters = new List<(string name, object? value)>();

            if (!string.IsNullOrWhiteSpace(searchKeyword))
            {
                whereClauses.Add("(c.title LIKE @keyword OR c.project_path LIKE @keyword)");
                parameters.Add(("@keyword", $"%{searchKeyword}%"));
            }
            if (beforeDate.HasValue)
            {
                whereClauses.Add("c.updated_at < @beforeDate");
                parameters.Add(("@beforeDate", beforeDate.Value.ToString("yyyy-MM-dd HH:mm:ss")));
            }
            if (!string.IsNullOrWhiteSpace(projectPath))
            {
                whereClauses.Add("c.project_path = @projectPath");
                parameters.Add(("@projectPath", projectPath));
            }

            var whereSql = whereClauses.Count > 0
                ? "WHERE " + string.Join(" AND ", whereClauses)
                : string.Empty;

            var sql = $@"
                SELECT c.id, c.title, c.project_path, c.updated_at,
                       (SELECT COUNT(*) FROM messages m WHERE m.conversation_id = c.id) AS msg_count,
                       (SELECT COALESCE(SUM(LENGTH(content)), 0) FROM messages m WHERE m.conversation_id = c.id) AS size_bytes
                FROM conversations c
                {whereSql}
                ORDER BY c.updated_at DESC
                LIMIT @limit OFFSET @offset";

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@limit", limit);
            cmd.Parameters.AddWithValue("@offset", offset);
            foreach (var (name, value) in parameters)
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new Session
                {
                    Id = reader.GetString(0),
                    Title = reader.IsDBNull(1) ? "(无标题)" : reader.GetString(1),
                    ProjectPath = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    LastUpdatedAt = reader.IsDBNull(3) ? DateTime.MinValue : reader.GetDateTime(3),
                    MessageCount = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                    SizeBytes = reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
                    Type = string.Empty
                });
            }
            return result;
        }
        catch { return result; }
    }

    /// <summary>按筛选条件统计会话总数</summary>
    public int GetFilteredCount(string? searchKeyword = null,
        DateTime? beforeDate = null, string? projectPath = null, string? sessionType = null)
    {
        if (_connection is null) return 0;

        try
        {
            var whereClauses = new List<string>();
            var parameters = new List<(string name, object? value)>();

            if (!string.IsNullOrWhiteSpace(searchKeyword))
            {
                whereClauses.Add("(c.title LIKE @keyword OR c.project_path LIKE @keyword)");
                parameters.Add(("@keyword", $"%{searchKeyword}%"));
            }

            if (beforeDate.HasValue)
            {
                whereClauses.Add("c.updated_at < @beforeDate");
                parameters.Add(("@beforeDate", beforeDate.Value.ToString("yyyy-MM-dd HH:mm:ss")));
            }

            if (!string.IsNullOrWhiteSpace(projectPath))
            {
                whereClauses.Add("c.project_path = @projectPath");
                parameters.Add(("@projectPath", projectPath));
            }

            if (!string.IsNullOrWhiteSpace(sessionType))
            {
                whereClauses.Add("c.type = @sessionType");
                parameters.Add(("@sessionType", sessionType));
            }

            var whereSql = whereClauses.Count > 0
                ? "WHERE " + string.Join(" AND ", whereClauses)
                : string.Empty;

            var sql = $"SELECT COUNT(*) FROM conversations c {whereSql}";

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>获取所有不重复的会话类型列表</summary>
    public List<string> GetSessionTypes()
    {
        var result = new List<string>();
        if (_connection is null) return result;

        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT type FROM conversations WHERE type IS NOT NULL AND type != '' ORDER BY type";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(reader.GetString(0));
            }
        }
        catch
        {
            // type 列不存在时返回空列表
        }

        return result;
    }

    /// <summary>获取所有不重复的项目路径列表</summary>
    public List<string> GetProjectPaths()
    {
        var result = new List<string>();
        if (_connection is null) return result;

        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT project_path FROM conversations WHERE project_path IS NOT NULL AND project_path != '' ORDER BY project_path";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(reader.GetString(0));
            }
        }
        catch { }

        return result;
    }

    /// <summary>批量删除会话（事务）</summary>
    /// <returns>删除条数</returns>
    public int DeleteSessions(List<string> sessionIds)
    {
        if (_connection is null || sessionIds.Count == 0) return 0;

        using var transaction = _connection.BeginTransaction();
        try
        {
            int deletedCount = 0;
            foreach (var id in sessionIds)
            {
                // 删除关联消息
                using var delMsg = _connection.CreateCommand();
                delMsg.CommandText = "DELETE FROM messages WHERE conversation_id = @id";
                delMsg.Parameters.AddWithValue("@id", id);
                delMsg.ExecuteNonQuery();

                // 删除会话
                using var delConv = _connection.CreateCommand();
                delConv.CommandText = "DELETE FROM conversations WHERE id = @id";
                delConv.Parameters.AddWithValue("@id", id);
                deletedCount += delConv.ExecuteNonQuery();
            }

            transaction.Commit();
            RefreshFileSize();
            return deletedCount;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>获取会话摘要（预览会话内容）</summary>
    public (bool success, string content) GetSessionPreview(string sessionId)
    {
        if (_connection is null) return (false, "数据库未连接");

        try
        {
            // 获取会话基本信息
            using var convCmd = _connection.CreateCommand();
            convCmd.CommandText = "SELECT title, project_path, updated_at FROM conversations WHERE id = @id";
            convCmd.Parameters.AddWithValue("@id", sessionId);
            using var convReader = convCmd.ExecuteReader();
            if (!convReader.Read())
                return (false, "会话不存在");

            var title = convReader.IsDBNull(0) ? "(无标题)" : convReader.GetString(0);
            var projectPath = convReader.IsDBNull(1) ? "" : convReader.GetString(1);
            var updatedAt = convReader.IsDBNull(2) ? DateTime.MinValue : convReader.GetDateTime(2);
            convReader.Close();

            // 获取消息摘要（最多取 20 条，每条截取前 200 字符）
            using var msgCmd = _connection.CreateCommand();
            msgCmd.CommandText = @"
                SELECT role, content, created_at FROM messages
                WHERE conversation_id = @id
                ORDER BY created_at ASC LIMIT 20";
            msgCmd.Parameters.AddWithValue("@id", sessionId);

            var preview = new System.Text.StringBuilder();
            preview.AppendLine($"━━━ 会话摘要 ━━━");
            preview.AppendLine($"标题: {title}");
            preview.AppendLine($"项目: {projectPath}");
            preview.AppendLine($"最后更新: {updatedAt:yyyy-MM-dd HH:mm:ss}");
            preview.AppendLine($"━━━━━━━━━━━━━━━");

            using var msgReader = msgCmd.ExecuteReader();
            int msgIndex = 0;
            while (msgReader.Read())
            {
                msgIndex++;
                var role = msgReader.IsDBNull(0) ? "unknown" : msgReader.GetString(0);
                var content = msgReader.IsDBNull(1) ? "" : msgReader.GetString(1);
                var createdAt = msgReader.IsDBNull(2) ? DateTime.MinValue : msgReader.GetDateTime(2);

                // 截取内容前 200 字符
                var truncated = content.Length > 200 ? content[..200] + "..." : content;
                // 移除换行符以保持预览整洁
                truncated = truncated.Replace("\r\n", " ").Replace("\n", " ");

                preview.AppendLine($"[{msgIndex}] {role} ({createdAt:HH:mm:ss}):");
                preview.AppendLine($"    {truncated}");
                preview.AppendLine();
            }

            if (msgIndex == 0)
                preview.AppendLine("(无消息内容)");

            return (true, preview.ToString());
        }
        catch (Exception ex)
        {
            return (false, $"获取预览失败: {ex.Message}");
        }
    }

    /// <summary>执行 VACUUM 收缩数据库</summary>
    /// <returns>(成功, 压缩前大小, 压缩后大小, 错误信息)</returns>
    public (bool success, long beforeSize, long afterSize, string error) Vacuum()
    {
        if (_connection is null)
            return (false, _dbFileSize, _dbFileSize, "数据库未连接");

        var beforeSize = _dbFileSize;
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "VACUUM;";
            cmd.ExecuteNonQuery();

            RefreshFileSize();
            return (true, beforeSize, _dbFileSize, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, beforeSize, _dbFileSize, ex.Message);
        }
    }

    /// <summary>命令行模式：删除指定日期前的所有会话</summary>
    public int PurgeSessionsBefore(DateTime cutoffDate)
    {
        if (_connection is null) return 0;

        try
        {
            // 先查出要删除的 ID
            using var selectCmd = _connection.CreateCommand();
            selectCmd.CommandText = "SELECT id FROM conversations WHERE updated_at < @cutoff";
            selectCmd.Parameters.AddWithValue("@cutoff", cutoffDate.ToString("yyyy-MM-dd HH:mm:ss"));
            var ids = new List<string>();
            using var reader = selectCmd.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(reader.GetString(0));
            }

            if (ids.Count == 0) return 0;
            return DeleteSessions(ids);
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose()
    {
        _connection?.Close();
        _connection?.Dispose();
    }
}
