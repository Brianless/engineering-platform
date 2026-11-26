using Microsoft.Data.Sqlite;

namespace EngineeringPlatform.Gateway;

/// <summary>
/// SQLite 项目库持久化（%LOCALAPPDATA%\EngineeringPlatform\projects\default.db），
/// 落库完整工具载荷并返回 resultRef（projects/default.db#results/&lt;id&gt;），同时记录审计日志。
/// </summary>
internal sealed class ResultStore : IDisposable
{
    private readonly string _dbPath;
    private readonly object _sync = new();
    private string _resultsTable = "results";
    private string _auditTable = "audit_log";

    public ResultStore(string? dbPath = null)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _dbPath = !string.IsNullOrEmpty(dbPath)
            ? dbPath
            : Path.Combine(local, "EngineeringPlatform", "projects", "default.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        InitSchema();
    }

    private void InitSchema()
    {
        lock (_sync)
        {
            using var conn = Open();

            // 兼容旧版 Python mcp/ 创建的 default.db：若已有不兼容列结构，则自动隔离到 gateway_* 表，绝不破坏旧表也不崩溃
            _resultsTable = TableMatchesColumns(conn, "results", ["op_id", "tool", "fingerprint", "params_digest", "status", "payload", "created_utc"])
                ? "results"
                : (TableExists(conn, "results") ? "gateway_results" : "results");

            _auditTable = TableMatchesColumns(conn, "audit_log", ["ts_utc", "op_id", "tool", "params_digest", "ok", "elapsed_ms", "error_code"])
                ? "audit_log"
                : (TableExists(conn, "audit_log") ? "gateway_audit_log" : "audit_log");

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
CREATE TABLE IF NOT EXISTS {_resultsTable} (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    op_id TEXT NOT NULL,
    tool TEXT NOT NULL,
    fingerprint TEXT,
    params_digest TEXT,
    status TEXT NOT NULL,
    payload TEXT NOT NULL,
    created_utc TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS {_auditTable} (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ts_utc TEXT NOT NULL,
    op_id TEXT,
    tool TEXT,
    params_digest TEXT,
    ok INTEGER NOT NULL,
    elapsed_ms INTEGER NOT NULL,
    error_code TEXT
);";
            cmd.ExecuteNonQuery();
        }
    }

    private static bool TableExists(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name;";
        cmd.Parameters.AddWithValue("$name", table);
        return cmd.ExecuteScalar() != null;
    }

    private static bool TableMatchesColumns(SqliteConnection conn, string table, string[] required)
    {
        if (!TableExists(conn, table)) return false;
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            cols.Add(reader.GetString(1));
        }
        return required.All(cols.Contains);
    }

    public string SaveResult(string opId, string tool, string? fingerprint, string paramsDigest, string status, string payloadJson)
    {
        lock (_sync)
        {
            try
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
INSERT INTO {_resultsTable} (op_id, tool, fingerprint, params_digest, status, payload, created_utc)
VALUES ($op, $tool, $fp, $digest, $status, $payload, $ts);
SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("$op", opId);
                cmd.Parameters.AddWithValue("$tool", tool);
                cmd.Parameters.AddWithValue("$fp", fingerprint ?? "");
                cmd.Parameters.AddWithValue("$digest", paramsDigest);
                cmd.Parameters.AddWithValue("$status", status);
                cmd.Parameters.AddWithValue("$payload", payloadJson);
                cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O"));
                long id = (long)(cmd.ExecuteScalar() ?? 0L);
                return $"projects/default.db#{_resultsTable}/{id}";
            }
            catch
            {
                return $"memory#{opId}";
            }
        }
    }

    public void RecordAudit(string opId, string tool, string paramsDigest, bool ok, long elapsedMs, string? errorCode)
    {
        lock (_sync)
        {
            try
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
INSERT INTO {_auditTable} (ts_utc, op_id, tool, params_digest, ok, elapsed_ms, error_code)
VALUES ($ts, $op, $tool, $digest, $ok, $ms, $err);";
                cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("$op", opId);
                cmd.Parameters.AddWithValue("$tool", tool);
                cmd.Parameters.AddWithValue("$digest", paramsDigest);
                cmd.Parameters.AddWithValue("$ok", ok ? 1 : 0);
                cmd.Parameters.AddWithValue("$ms", elapsedMs);
                cmd.Parameters.AddWithValue("$err", (object?)errorCode ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            catch
            {
                // 审计写库失败不阻断 CAD 工具返回
            }
        }
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath};Default Timeout=3");
        conn.Open();
        return conn;
    }

    public void Dispose()
    {
        // 每操作短连接，释放池即可
        SqliteConnection.ClearAllPools();
    }
}
