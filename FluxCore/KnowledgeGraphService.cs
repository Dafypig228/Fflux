using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluxCore.LLM;
using Microsoft.Data.Sqlite;

namespace FluxCore
{
    /// <summary>
    /// Extracts people, topics, and projects from raw data lake events
    /// and stores them as a lightweight SQLite knowledge graph (KG_Node + KG_Edge).
    ///
    /// Batch extraction runs every 30 minutes in background — never per-event.
    /// LLM call is cheap (one batch per run), conservative (only clear references).
    ///
    /// File: %APPDATA%\Davos\knowledge_graph.db
    /// </summary>
    public class KnowledgeGraphService : IDisposable
    {
        private readonly ILLMService _llm;
        private readonly DataLakeService? _lake;
        private readonly string _connStr;
        private readonly System.Threading.Timer _timer;
        private long _lastExtractedId = 0;
        private readonly object _lock = new();

        public KnowledgeGraphService(ILLMService llm, DataLakeService? lake = null)
        {
            _llm  = llm;
            _lake = lake;

            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Davos");
            Directory.CreateDirectory(dir);
            _connStr = $"Data Source={Path.Combine(dir, "knowledge_graph.db")}";

            InitSchema();

            // Run extraction every 30 minutes; first run after 5 min
            _timer = new System.Threading.Timer(_ => _ = ExtractBatchAsync(),
                null,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(30));
        }

        // ── Schema ────────────────────────────────────────────────────────────────

        private void InitSchema()
        {
            try
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS KG_Node (
                        id      INTEGER PRIMARY KEY AUTOINCREMENT,
                        type    TEXT    NOT NULL,     -- person | topic | project | event
                        name    TEXT    NOT NULL UNIQUE,
                        props   TEXT,                 -- JSON {notes, handle, lastSeen}
                        seen_n  INTEGER DEFAULT 1,
                        updated TEXT    NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS KG_Edge (
                        from_id INTEGER REFERENCES KG_Node(id),
                        to_id   INTEGER REFERENCES KG_Node(id),
                        rel     TEXT    NOT NULL,     -- knows | mentioned | workedOn | involves
                        weight  REAL    DEFAULT 1.0,
                        PRIMARY KEY (from_id, to_id, rel)
                    );
                    CREATE TABLE IF NOT EXISTS KG_Meta (
                        key   TEXT PRIMARY KEY,
                        value TEXT
                    );";
                cmd.ExecuteNonQuery();

                // Load last extracted ID
                using var meta = conn.CreateCommand();
                meta.CommandText = "SELECT value FROM KG_Meta WHERE key = 'last_id'";
                var v = meta.ExecuteScalar();
                if (v is string s && long.TryParse(s, out long id))
                    _lastExtractedId = id;
            }
            catch { }
        }

        // ── Batch extraction ─────────────────────────────────────────────────────

        private async Task ExtractBatchAsync()
        {
            if (_lake == null) return;
            try
            {
                // Pull new events since last extraction
                var rows = _lake.QueryRows(
                    $"SELECT id, content FROM events WHERE id > {_lastExtractedId}" +
                    " ORDER BY id ASC LIMIT 200");
                if (rows.Count == 0) return;

                long maxId = 0;
                var texts  = new StringBuilder();
                foreach (var (ts, content) in rows)
                {
                    // ts field in QueryRows is the id (first column from our SQL), content is second
                    texts.AppendLine(content.Length > 500 ? content[..500] : content);
                }

                // One cheap LLM call for the whole batch
                string prompt = $@"Extract entities from this collection of events/messages.
Return a JSON array only. Each item: {{""type"": ""person""|""topic""|""project"", ""name"": ""string"", ""relations"": [{{""to"": ""name"", ""rel"": ""knows""|""mentioned""|""workedOn""|""involves""}}]}}

Be conservative — only extract clear, specific references.
If nothing clear, return: []

Text batch:
{texts}

Return ONLY valid JSON array or []. No explanation.";

                string raw = await _llm.GenerateText(prompt, temperature: 0.1f);
                raw = raw.Trim();
                if (raw.StartsWith("```")) raw = raw.Split('\n', 2)[1];
                if (raw.EndsWith("```"))   raw = raw[..^3];

                var items = JsonSerializer.Deserialize<List<KgExtractItem>>(raw,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (items == null || items.Count == 0) return;

                // Upsert nodes + edges
                lock (_lock)
                {
                    using var conn = Open();
                    using var tx   = conn.BeginTransaction();
                    string now     = DateTime.UtcNow.ToString("o");

                    foreach (var item in items)
                    {
                        if (string.IsNullOrWhiteSpace(item.Name)) continue;
                        long nodeId = UpsertNode(conn, item.Type ?? "topic", item.Name, now);

                        if (item.Relations != null)
                        {
                            foreach (var rel in item.Relations)
                            {
                                if (string.IsNullOrWhiteSpace(rel.To)) continue;
                                long toId = UpsertNode(conn, "topic", rel.To, now);
                                UpsertEdge(conn, nodeId, toId, rel.Rel ?? "mentioned");
                            }
                        }
                    }

                    // Save last extracted ID — use the row count from the query
                    // (rows[^1].ts contains the id as the first SELECT column)
                    if (rows.Count > 0 && long.TryParse(rows[^1].ts, out long lastId))
                        maxId = lastId;

                    using var upd = conn.CreateCommand();
                    upd.CommandText =
                        "INSERT OR REPLACE INTO KG_Meta (key, value) VALUES ('last_id', $v)";
                    upd.Parameters.AddWithValue("$v", maxId.ToString());
                    upd.ExecuteNonQuery();
                    tx.Commit();
                }

                _lastExtractedId = maxId;
            }
            catch { }
        }

        private long UpsertNode(SqliteConnection conn, string type, string name, string now)
        {
            using var sel = conn.CreateCommand();
            sel.CommandText = "SELECT id FROM KG_Node WHERE name = $n";
            sel.Parameters.AddWithValue("$n", name);
            var existing = sel.ExecuteScalar();
            if (existing is long id)
            {
                using var up = conn.CreateCommand();
                up.CommandText = "UPDATE KG_Node SET seen_n = seen_n + 1, updated = $t WHERE id = $id";
                up.Parameters.AddWithValue("$t", now);
                up.Parameters.AddWithValue("$id", id);
                up.ExecuteNonQuery();
                return id;
            }

            using var ins = conn.CreateCommand();
            ins.CommandText =
                "INSERT INTO KG_Node (type, name, updated) VALUES ($ty, $n, $t); SELECT last_insert_rowid();";
            ins.Parameters.AddWithValue("$ty", type);
            ins.Parameters.AddWithValue("$n",  name);
            ins.Parameters.AddWithValue("$t",  now);
            return (long)(ins.ExecuteScalar() ?? 0L);
        }

        private void UpsertEdge(SqliteConnection conn, long fromId, long toId, string rel)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO KG_Edge (from_id, to_id, rel, weight)
                VALUES ($f, $t, $r, 1.0)
                ON CONFLICT (from_id, to_id, rel)
                DO UPDATE SET weight = weight + 0.5";
            cmd.Parameters.AddWithValue("$f", fromId);
            cmd.Parameters.AddWithValue("$t", toId);
            cmd.Parameters.AddWithValue("$r", rel);
            cmd.ExecuteNonQuery();
        }

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns a summary string for a named person or topic.
        /// Example: "Asqar: person | mentioned 12 times | connected to: FluxCore, gaming"
        /// </summary>
        public string GetNodeSummary(string name)
        {
            try
            {
                using var conn = Open();
                using var cmd  = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT id, type, seen_n FROM KG_Node WHERE name = $n COLLATE NOCASE";
                cmd.Parameters.AddWithValue("$n", name);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return "";
                long id    = r.GetInt64(0);
                string type = r.GetString(1);
                int seen   = r.GetInt32(2);

                using var edges = conn.CreateCommand();
                edges.CommandText =
                    @"SELECT n.name FROM KG_Edge e JOIN KG_Node n ON e.to_id = n.id
                      WHERE e.from_id = $id ORDER BY e.weight DESC LIMIT 5";
                edges.Parameters.AddWithValue("$id", id);
                var related = new List<string>();
                using var er = edges.ExecuteReader();
                while (er.Read()) related.Add(er.GetString(0));

                string relStr = related.Count > 0 ? " | related: " + string.Join(", ", related) : "";
                return $"{name}: {type} | seen {seen}x{relStr}";
            }
            catch { return ""; }
        }

        /// <summary>Returns the top N most-mentioned topics/people.</summary>
        public string GetTopTopics(int n = 5)
        {
            try
            {
                using var conn = Open();
                using var cmd  = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT name, type, seen_n FROM KG_Node ORDER BY seen_n DESC LIMIT $n";
                cmd.Parameters.AddWithValue("$n", n);
                var sb = new StringBuilder("=== TOP TOPICS ===\n");
                int i  = 1;
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    sb.AppendLine($"  {i++}. {r.GetString(0)} [{r.GetString(1)}] (×{r.GetInt32(2)})");
                return sb.ToString();
            }
            catch { return ""; }
        }

        /// <summary>
        /// Smart context for a query — returns relevant nodes + their connections.
        /// </summary>
        public string GetGraphContext(string query)
        {
            try
            {
                // Simple keyword search against node names
                string kw = "%" + query.Replace(" ", "%") + "%";
                using var conn = Open();
                using var cmd  = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT name, type, seen_n FROM KG_Node WHERE name LIKE $q ORDER BY seen_n DESC LIMIT 10";
                cmd.Parameters.AddWithValue("$q", kw);
                var sb    = new StringBuilder();
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    sb.AppendLine(GetNodeSummary(r.GetString(0)));
                return sb.Length > 0 ? "=== KNOWLEDGE GRAPH ===\n" + sb : "";
            }
            catch { return ""; }
        }

        /// <summary>
        /// Returns all node names ordered by frequency (most-mentioned first, max 1000).
        /// Used by MemoryEngine for GraphRAG entity matching against retrieved chunks.
        /// </summary>
        public IEnumerable<string> GetAllNodeNames()
        {
            try
            {
                using var conn = Open();
                using var cmd  = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT name FROM KG_Node ORDER BY seen_n DESC LIMIT 1000";
                var names = new List<string>();
                using var r = cmd.ExecuteReader();
                while (r.Read()) names.Add(r.GetString(0));
                return names;
            }
            catch { return Enumerable.Empty<string>(); }
        }

        private SqliteConnection Open()
        {
            var conn = new SqliteConnection(_connStr);
            conn.Open();
            return conn;
        }

        public void Dispose() => _timer.Dispose();

        // ── Internal DTO ─────────────────────────────────────────────────────────

        private class KgExtractItem
        {
            public string?             Type      { get; set; }
            public string?             Name      { get; set; }
            public List<KgRelation>?   Relations { get; set; }
        }
        private class KgRelation
        {
            public string? To  { get; set; }
            public string? Rel { get; set; }
        }
    }
}
