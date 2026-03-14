using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FluxCore
{
    /// <summary>
    /// Append-only raw event store — every piece of collected data lands here.
    /// Never deletes rows. Queryable by exact SQL.
    /// Backed by SQLite at %APPDATA%\Davos\datalake.db
    ///
    /// Source tags: clipboard | file | notification | chrome | vscode |
    ///              telegram | terminal | git | eventlog | chat | task | inner_voice
    /// </summary>
    public class DataLakeService : IDisposable
    {
        private readonly string _connectionString;
        private readonly object _writeLock = new();

        public DataLakeService()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Davos");
            Directory.CreateDirectory(dir);
            string dbPath = Path.Combine(dir, "datalake.db");
            _connectionString = $"Data Source={dbPath}";
            Initialize();
        }

        private void Initialize()
        {
            try
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS events (
                        id      INTEGER PRIMARY KEY AUTOINCREMENT,
                        source  TEXT    NOT NULL,
                        ts      TEXT    NOT NULL,
                        content TEXT    NOT NULL,
                        meta    TEXT
                    );
                    CREATE INDEX IF NOT EXISTS idx_source_ts ON events(source, ts DESC);";
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        private SqliteConnection Open()
        {
            var conn = new SqliteConnection(_connectionString);
            conn.Open();
            return conn;
        }

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>
        /// Appends one event to the data lake. Thread-safe. Never throws.
        /// Returns the inserted row ID so callers can pass it to SaveChunked for traceability.
        /// </summary>
        public long Write(string source, string content, object? meta = null)
        {
            if (string.IsNullOrEmpty(content)) return 0;
            string metaJson = meta != null ? JsonSerializer.Serialize(meta) : "";
            // Truncate very large payloads to keep DB manageable
            if (content.Length > 32_000) content = content[..32_000] + "…";

            lock (_writeLock)
            {
                try
                {
                    using var conn = Open();
                    using var cmd  = conn.CreateCommand();
                    cmd.CommandText =
                        "INSERT INTO events (source, ts, content, meta) VALUES ($s, $t, $c, $m)";
                    cmd.Parameters.AddWithValue("$s", source);
                    cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
                    cmd.Parameters.AddWithValue("$c", content);
                    cmd.Parameters.AddWithValue("$m", metaJson);
                    cmd.ExecuteNonQuery();

                    using var rowIdCmd = conn.CreateCommand();
                    rowIdCmd.CommandText = "SELECT last_insert_rowid()";
                    return (long)(rowIdCmd.ExecuteScalar() ?? 0L);
                }
                catch { return 0; /* background — never throw */ }
            }
        }

        /// <summary>
        /// Returns the N most recent events for a source, formatted for LLM context.
        /// </summary>
        public string GetRecent(string source, int count = 10)
        {
            var rows = QueryRows(
                $"SELECT ts, content FROM events WHERE source = '{Esc(source)}'" +
                $" ORDER BY ts DESC LIMIT {count}");
            if (rows.Count == 0) return "";
            var sb = new StringBuilder();
            sb.AppendLine($"=== LAKE [{source.ToUpper()}] (last {rows.Count}) ===");
            foreach (var (ts, content) in rows)
            {
                var time = ParseUtc(ts).ToString("HH:mm");
                var line = content.Replace('\n', ' ');
                sb.AppendLine($"  [{time}] {Trim(line, 200)}");
            }
            return sb.ToString();
        }

        /// <summary>Returns events from a given UTC time onward for a source.</summary>
        public string GetSince(string source, DateTime sinceUtc)
        {
            string sinceStr = sinceUtc.ToString("o");
            var rows = QueryRows(
                $"SELECT ts, content FROM events WHERE source = '{Esc(source)}'" +
                $" AND ts >= '{sinceStr}' ORDER BY ts ASC");
            if (rows.Count == 0) return "";
            var sb = new StringBuilder();
            sb.AppendLine($"=== LAKE [{source.ToUpper()}] since {sinceUtc.ToLocalTime():HH:mm} ===");
            foreach (var (ts, content) in rows)
            {
                var time = ParseUtc(ts).ToString("HH:mm");
                sb.AppendLine($"  [{time}] {Trim(content.Replace('\n', ' '), 200)}");
            }
            return sb.ToString();
        }

        /// <summary>Executes raw SQL SELECT, returns (ts, content) rows.</summary>
        public List<(string ts, string content)> QueryRows(string sql)
        {
            var result = new List<(string, string)>();
            lock (_writeLock)
            {
                try
                {
                    using var conn = Open();
                    using var cmd  = conn.CreateCommand();
                    cmd.CommandText = sql;
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                        result.Add((reader.GetString(0),
                                    reader.IsDBNull(1) ? "" : reader.GetString(1)));
                }
                catch { }
            }
            return result;
        }

        /// <summary>Total event count for a source.</summary>
        public long Count(string source)
        {
            lock (_writeLock)
            {
                try
                {
                    using var conn = Open();
                    using var cmd  = conn.CreateCommand();
                    cmd.CommandText =
                        $"SELECT COUNT(*) FROM events WHERE source = '{Esc(source)}'";
                    return (long)(cmd.ExecuteScalar() ?? 0L);
                }
                catch { return 0; }
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static string Esc(string s) => s.Replace("'", "''");
        private static string Trim(string s, int max) =>
            s.Length <= max ? s : s[..max] + "…";
        private static DateTime ParseUtc(string iso)
        {
            try { return DateTime.Parse(iso, null,
                System.Globalization.DateTimeStyles.RoundtripKind).ToLocalTime(); }
            catch { return DateTime.Now; }
        }

        public void Dispose() { /* connections are opened/closed per-call */ }
    }
}
