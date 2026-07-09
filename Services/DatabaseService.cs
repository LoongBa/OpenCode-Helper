using Microsoft.Data.Sqlite;
using OpenCodeHelper.Models;

namespace OpenCodeHelper.Services;

/// <summary>数据库服务 — 负责 SQLite 连接、查询、删除、收缩操作</summary>
public class DatabaseService : IDisposable
{
    private readonly string _dbPath;
    private SqliteConnection? _connection;
    private long _dbFileSize;

    public string DbPath => _dbPath;
    public long DbFileSize => _dbFileSize;
    public bool IsConnected => _connection is not null;

    /// <summary>获取默认的 OpenCode 数据库路径</summary>
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

    private void RefreshFileSize()
    {
        try { _dbFileSize = File.Exists(_dbPath) ? new FileInfo(_dbPath).Length : 0; }
        catch { _dbFileSize = 0; }
    }

    // ══════════════════════════════════════════════
    //  生命周期
    // ══════════════════════════════════════════════

    /// <summary>校验数据库文件是否存在且包含 session 表</summary>
    public (bool success, string error) ValidateDatabase()
    {
        if (!File.Exists(_dbPath))
            return (false, $"数据库文件不存在: {_dbPath}");

        try
        {
            using var testConn = new SqliteConnection($"Data Source={_dbPath}");
            testConn.Open();
            using var cmd = testConn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='session'";
            if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
                return (false, "数据库结构中缺少 session 表");
            testConn.Close();
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, $"数据库打开失败: {ex.Message}");
        }
    }

    /// <summary>打开数据库连接（关闭已有连接）</summary>
    public bool Open()
    {
        try
        {
            // 关闭旧连接
            if (_connection is not null)
            {
                try { _connection.Close(); _connection.Dispose(); }
                catch { /* 忽略关闭旧连接的错误 */ }
            }
            _connection = new SqliteConnection($"Data Source={_dbPath}");
            _connection.Open();
            return true;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        _connection?.Close();
        _connection?.Dispose();
    }

    // ══════════════════════════════════════════════
    //  Unix 时间戳工具（数据库存储为毫秒）
    // ══════════════════════════════════════════════

    private static long ToUnixMillis(DateTime dt) =>
        new DateTimeOffset(dt).ToUnixTimeMilliseconds();

    private static DateTime FromUnixMillis(long millis) =>
        DateTimeOffset.FromUnixTimeMilliseconds(millis).LocalDateTime;

    // ══════════════════════════════════════════════
    //  查询
    // ══════════════════════════════════════════════

    /// <summary>获取会话总数</summary>
    public int GetTotalSessionCount()
    {
        if (_connection is null) return 0;
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM session";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        catch { return 0; }
    }

    /// <summary>分页查询会话列表（真实 Schema）</summary>
    public List<Session> GetSessions(int offset, int limit, string? searchKeyword = null,
        DateTime? beforeDate = null, string? projectPath = null, string? sessionType = null,
        bool excludeSubAgents = true)
    {
        var result = new List<Session>();
        if (_connection is null) return result;

        try
        {
            var where = new List<string>();
            var pars = new List<(string, object?)>();

            if (!string.IsNullOrWhiteSpace(searchKeyword))
            {
                where.Add("(s.title LIKE @kw OR s.directory LIKE @kw)");
                pars.Add(("@kw", $"%{searchKeyword}%"));
            }

            if (beforeDate.HasValue)
            {
                where.Add("s.time_updated < @before");
                pars.Add(("@before", ToUnixMillis(beforeDate.Value)));
            }

            if (!string.IsNullOrWhiteSpace(projectPath))
            {
                where.Add("s.directory = @dir");
                pars.Add(("@dir", projectPath));
            }

            if (!string.IsNullOrWhiteSpace(sessionType))
            {
                where.Add("s.agent LIKE @agent");
                pars.Add(("@agent", $"%{sessionType}%"));
            }

            if (excludeSubAgents)
            {
                where.Add("s.parent_id IS NULL");
            }

            var whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

            var sql = $@"
                SELECT s.id, s.title, s.directory, s.time_updated,
                       (SELECT COUNT(*) FROM session_message sm WHERE sm.session_id = s.id) AS msg_count,
                       (SELECT COALESCE(SUM(LENGTH(sm.data)), 0) FROM session_message sm WHERE sm.session_id = s.id) AS size_bytes,
                       COALESCE(s.agent, '') AS agent
                FROM session s
                {whereSql}
                ORDER BY s.time_updated DESC
                LIMIT @lim OFFSET @off";

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@lim", limit);
            cmd.Parameters.AddWithValue("@off", offset);
            foreach (var (n, v) in pars)
                cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                result.Add(new Session
                {
                    Id = r.GetString(0),
                    Title = r.IsDBNull(1) ? "(无标题)" : r.GetString(1),
                    ProjectPath = r.IsDBNull(2) ? "" : r.GetString(2),
                    LastUpdatedAt = r.IsDBNull(3) ? DateTime.MinValue : FromUnixMillis(r.GetInt64(3)),
                    MessageCount = r.IsDBNull(4) ? 0 : r.GetInt32(4),
                    SizeBytes = r.IsDBNull(5) ? 0 : r.GetInt64(5),
                    Agent = r.IsDBNull(6) ? "" : r.GetString(6)
                });
            }

            return result;
        }
        catch (Microsoft.Data.Sqlite.SqliteException) when (result.Count == 0)
        {
            // agent 列不存在时降级
            return GetSessionsFallback(offset, limit, searchKeyword, beforeDate, projectPath);
        }
        catch { return result; }
    }

    /// <summary>降级查询 — 不含 agent 列的旧版数据库</summary>
    private List<Session> GetSessionsFallback(int offset, int limit, string? searchKeyword = null,
        DateTime? beforeDate = null, string? projectPath = null)
    {
        var result = new List<Session>();
        if (_connection is null) return result;
        try
        {
            var where = new List<string>();
            var pars = new List<(string, object?)>();

            if (!string.IsNullOrWhiteSpace(searchKeyword))
            {
                where.Add("(s.title LIKE @kw OR s.directory LIKE @kw)");
                pars.Add(("@kw", $"%{searchKeyword}%"));
            }
            if (beforeDate.HasValue)
            {
                where.Add("s.time_updated < @before");
                pars.Add(("@before", ToUnixMillis(beforeDate.Value)));
            }
            if (!string.IsNullOrWhiteSpace(projectPath))
            {
                where.Add("s.directory = @dir");
                pars.Add(("@dir", projectPath));
            }

            var whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
            var sql = $@"
                SELECT s.id, s.title, s.directory, s.time_updated,
                       (SELECT COUNT(*) FROM session_message sm WHERE sm.session_id = s.id) AS msg_count,
                       (SELECT COALESCE(SUM(LENGTH(sm.data)), 0) FROM session_message sm WHERE sm.session_id = s.id) AS size_bytes
                FROM session s
                {whereSql}
                ORDER BY s.time_updated DESC
                LIMIT @lim OFFSET @off";

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@lim", limit);
            cmd.Parameters.AddWithValue("@off", offset);
            foreach (var (n, v) in pars)
                cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                result.Add(new Session
                {
                    Id = r.GetString(0),
                    Title = r.IsDBNull(1) ? "(无标题)" : r.GetString(1),
                    ProjectPath = r.IsDBNull(2) ? "" : r.GetString(2),
                    LastUpdatedAt = r.IsDBNull(3) ? DateTime.MinValue : FromUnixMillis(r.GetInt64(3)),
                    MessageCount = r.IsDBNull(4) ? 0 : r.GetInt32(4),
                    SizeBytes = r.IsDBNull(5) ? 0 : r.GetInt64(5),
                    Agent = string.Empty
                });
            }
            return result;
        }
        catch { return result; }
    }

    /// <summary>按筛选条件统计会话总数</summary>
    public int GetFilteredCount(string? searchKeyword = null,
        DateTime? beforeDate = null, string? projectPath = null, string? sessionType = null,
        bool excludeSubAgents = true)
    {
        if (_connection is null) return 0;
        try
        {
            var where = new List<string>();
            var pars = new List<(string, object?)>();

            if (!string.IsNullOrWhiteSpace(searchKeyword))
            {
                where.Add("(s.title LIKE @kw OR s.directory LIKE @kw)");
                pars.Add(("@kw", $"%{searchKeyword}%"));
            }
            if (beforeDate.HasValue)
            {
                where.Add("s.time_updated < @before");
                pars.Add(("@before", ToUnixMillis(beforeDate.Value)));
            }
            if (!string.IsNullOrWhiteSpace(projectPath))
            {
                where.Add("s.directory = @dir");
                pars.Add(("@dir", projectPath));
            }
            if (!string.IsNullOrWhiteSpace(sessionType))
            {
                where.Add("s.agent LIKE @agent");
                pars.Add(("@agent", $"%{sessionType}%"));
            }

            if (excludeSubAgents)
            {
                where.Add("s.parent_id IS NULL");
            }

            var whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
            var sql = $"SELECT COUNT(*) FROM session s {whereSql}";

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (n, v) in pars)
                cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);

            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        catch { return 0; }
    }

    /// <summary>获取所有不重复的项目路径列表</summary>
    public List<string> GetProjectPaths()
    {
        var result = new List<string>();
        if (_connection is null) return result;
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT directory FROM session WHERE directory IS NOT NULL AND directory != '' ORDER BY directory";
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add(r.GetString(0));
        }
        catch { }
        return result;
    }

    /// <summary>获取所有不重复的会话类型（agent）列表</summary>
    public List<string> GetSessionTypes()
    {
        var result = new List<string>();
        if (_connection is null) return result;
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT agent FROM session WHERE agent IS NOT NULL AND agent != '' ORDER BY agent";
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add(r.GetString(0));
        }
        catch { }
        return result;
    }

    /// <summary>获取会话摘要（预览会话内容）</summary>
    public (bool success, string content) GetSessionPreview(string sessionId)
    {
        if (_connection is null) return (false, "数据库未连接");
        try
        {
            using var convCmd = _connection.CreateCommand();
            convCmd.CommandText = "SELECT title, directory, time_updated FROM session WHERE id = @id";
            convCmd.Parameters.AddWithValue("@id", sessionId);
            using var cr = convCmd.ExecuteReader();
            if (!cr.Read()) return (false, "会话不存在");

            var title = cr.IsDBNull(0) ? "(无标题)" : cr.GetString(0);
            var dir = cr.IsDBNull(1) ? "" : cr.GetString(1);
            var updated = cr.IsDBNull(2) ? DateTime.MinValue : FromUnixMillis(cr.GetInt64(2));
            cr.Close();

            using var msgCmd = _connection.CreateCommand();
            msgCmd.CommandText = @"
                SELECT type, data, time_created FROM session_message
                WHERE session_id = @id ORDER BY seq ASC LIMIT 20";
            msgCmd.Parameters.AddWithValue("@id", sessionId);

            var preview = new System.Text.StringBuilder();
            preview.AppendLine("━━━ 会话摘要 ━━━");
            preview.AppendLine($"标题: {title}");
            preview.AppendLine($"项目: {dir}");
            preview.AppendLine($"最后更新: {updated:yyyy-MM-dd HH:mm:ss}");
            preview.AppendLine("━━━━━━━━━━━━━━━");

            using var mr = msgCmd.ExecuteReader();
            int idx = 0;
            while (mr.Read())
            {
                idx++;
                var role = mr.IsDBNull(0) ? "unknown" : mr.GetString(0);
                var data = mr.IsDBNull(1) ? "" : mr.GetString(1);
                var ts = mr.IsDBNull(2) ? DateTime.MinValue : FromUnixMillis(mr.GetInt64(2));

                var truncated = data.Length > 200 ? data[..200] + "..." : data;
                truncated = truncated.Replace("\r\n", " ").Replace("\n", " ");

                preview.AppendLine($"[{idx}] {role} ({ts:HH:mm:ss}):");
                preview.AppendLine($"    {truncated}");
                preview.AppendLine();
            }
            if (idx == 0) preview.AppendLine("(无消息内容)");
            return (true, preview.ToString());
        }
        catch (Exception ex) { return (false, $"获取预览失败: {ex.Message}"); }
    }

    // ══════════════════════════════════════════════
    //  操作
    // ══════════════════════════════════════════════

    /// <summary>批量删除会话（事务）</summary>
    public int DeleteSessions(List<string> sessionIds)
    {
        if (_connection is null || sessionIds.Count == 0) return 0;
        using var tx = _connection.BeginTransaction();
        try
        {
            // 构建 IN 子句参数
            var msgParams = string.Join(",", sessionIds.Select((_, i) => $"@id{i}"));
            using var dm = _connection.CreateCommand();
            dm.CommandText = $"DELETE FROM session_message WHERE session_id IN ({msgParams})";
            for (int i = 0; i < sessionIds.Count; i++)
                dm.Parameters.AddWithValue($"@id{i}", sessionIds[i]);
            dm.ExecuteNonQuery();

            using var ds = _connection.CreateCommand();
            ds.CommandText = $"DELETE FROM session WHERE id IN ({msgParams})";
            for (int i = 0; i < sessionIds.Count; i++)
                ds.Parameters.AddWithValue($"@id{i}", sessionIds[i]);
            var deleted = ds.ExecuteNonQuery();

            tx.Commit();
            RefreshFileSize();
            return deleted;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>命令行模式：删除指定 Unix 时间前的所有会话</summary>
    public int PurgeSessionsBefore(DateTime cutoffDate)
    {
        if (_connection is null) return 0;
        try
        {
            var cutoff = ToUnixMillis(cutoffDate);
            using var sel = _connection.CreateCommand();
            sel.CommandText = "SELECT id FROM session WHERE time_updated < @cutoff";
            sel.Parameters.AddWithValue("@cutoff", cutoff);
            var ids = new List<string>();
            using var r = sel.ExecuteReader();
            while (r.Read()) ids.Add(r.GetString(0));
            return ids.Count > 0 ? DeleteSessions(ids) : 0;
        }
        catch { return 0; }
    }

    /// <summary>执行 VACUUM 收缩数据库</summary>
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
        catch (Exception ex) { return (false, beforeSize, _dbFileSize, ex.Message); }
    }
}
