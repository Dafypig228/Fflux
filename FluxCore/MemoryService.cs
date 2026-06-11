using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using FluxCore.LLM;

namespace FluxCore
{
    public class MemoryItem
    {
        public long Id { get; set; }
        public string Content { get; set; }
        public string SourceApp { get; set; }
        public DateTime Timestamp { get; set; }
        public float[] Embedding { get; set; }
        public string? SourceUri { get; set; }
    }

    public class MemoryService
    {
        private readonly string _dbPath;
        private readonly ILLMService _llm;
        private List<MemoryItem> _cachedMemories = new List<MemoryItem>();
        private readonly object _cacheLock = new();

        public MemoryService(ILLMService llm)
        {
            _llm = llm;

            // Stable path in %APPDATA%\Davos — does not change between Debug/Release builds
            string appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Davos");
            Directory.CreateDirectory(appData);
            string newDb = Path.Combine(appData, "davos_memory.db");

            // Migrate old databases from BaseDirectory if they exist
            string oldDb1 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "flux_memory_v2.db");
            string oldDb2 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "davos_memory.db");
            if (!File.Exists(newDb))
            {
                if (File.Exists(oldDb2)) File.Move(oldDb2, newDb);
                else if (File.Exists(oldDb1)) File.Move(oldDb1, newDb);
            }

            _dbPath = newDb;
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText =
            @"
                CREATE TABLE IF NOT EXISTS Memories (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Content TEXT NOT NULL,
                    SourceApp TEXT,
                    Timestamp TEXT,
                    Embedding BLOB
                );
                CREATE TABLE IF NOT EXISTS DailySummaries (
                    Date TEXT PRIMARY KEY,
                    Summary TEXT
                );
            ";
            command.ExecuteNonQuery();

            // Idempotent schema migrations — swallow "duplicate column" errors
            foreach (string colDef in new[]
            {
                "embedding_dims INTEGER DEFAULT 768",
                "source_uri TEXT"
            })
            {
                try
                {
                    using var alter = connection.CreateCommand();
                    alter.CommandText = $"ALTER TABLE Memories ADD COLUMN {colDef}";
                    alter.ExecuteNonQuery();
                }
                catch (SqliteException) { /* column already exists — fine */ }
            }

            // Load cache on startup
            _ = LoadCacheAsync();
        }

        private async Task LoadCacheAsync()
        {
            var fresh = new List<MemoryItem>();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText =
                "SELECT Id, Content, SourceApp, Timestamp, Embedding, source_uri FROM Memories";

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var item = new MemoryItem
                {
                    Id        = reader.GetInt64(0),
                    Content   = reader.GetString(1),
                    SourceApp = reader.IsDBNull(2) ? "Unknown" : reader.GetString(2),
                    Timestamp = DateTime.Parse(reader.GetString(3)),
                    SourceUri = reader.IsDBNull(5) ? null : reader.GetString(5),
                };

                if (!reader.IsDBNull(4))
                    item.Embedding = DeserializeEmbedding((byte[])reader["Embedding"]);

                fresh.Add(item);
            }

            lock (_cacheLock) { _cachedMemories = fresh; }
        }

        // ── Public save API ───────────────────────────────────────────────────────

        /// <summary>Saves a single memory entry (backwards-compatible).</summary>
        public Task Save(string content, string appName)
            => SaveCore(content, appName, null);

        /// <summary>
        /// Chunks content before embedding. Each chunk is stored with a source_uri
        /// pointing back to the DataLake event that produced this content.
        /// </summary>
        public async Task SaveChunked(string content, string appName, long? dataLakeEventId = null)
        {
            if (string.IsNullOrWhiteSpace(content)) return;
            string? uri = dataLakeEventId.HasValue ? $"datalake:{dataLakeEventId}" : null;
            var chunks = TextChunker.Chunk(content, appName);
            foreach (var chunk in chunks)
                await SaveCore(chunk, appName, uri);
        }

        private async Task SaveCore(string content, string appName, string? sourceUri)
        {
            if (string.IsNullOrWhiteSpace(content)) return;

            float[] embedding = await _llm.GetEmbeddingAsync(content);

            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO Memories (Content, SourceApp, Timestamp, Embedding, embedding_dims, source_uri)" +
                " VALUES ($c, $a, $t, $e, $d, $u)";
            command.Parameters.AddWithValue("$c", content);
            command.Parameters.AddWithValue("$a", appName);
            command.Parameters.AddWithValue("$t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            command.Parameters.AddWithValue("$e", SerializeEmbedding(embedding));
            command.Parameters.AddWithValue("$d", embedding.Length);
            command.Parameters.AddWithValue("$u", (object?)sourceUri ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();

            var item = new MemoryItem
            {
                Content   = content,
                SourceApp = appName,
                Timestamp = DateTime.Now,
                Embedding = embedding,
                SourceUri = sourceUri,
            };
            lock (_cacheLock) { _cachedMemories.Add(item); }
        }

        // ── Vector search ─────────────────────────────────────────────────────────

        /// <summary>
        /// Searches memory with:
        ///   - Fix 3: query rewriting (LLM rewrites NL query → search keywords)
        ///   - Fix 4: time-weighted scoring (cos*0.7 + recency*0.3, half-life 7h)
        /// </summary>
        public async Task<List<string>> SearchRelevant(string query, int limit = 5)
        {
            List<MemoryItem> snapshot;
            lock (_cacheLock) { snapshot = new List<MemoryItem>(_cachedMemories); }

            if (snapshot.Count == 0) { await LoadCacheAsync(); lock (_cacheLock) { snapshot = new List<MemoryItem>(_cachedMemories); } }

            // Fix 3: rewrite natural-language query to search keywords
            string searchQuery = await RewriteQueryAsync(query);

            float[] queryEmbedding = await _llm.GetEmbeddingAsync(searchQuery);
            if (queryEmbedding.Length == 0) return new List<string>();

            // Fix 4: time-weighted scoring — half-life 7 hours
            double decayConstant = Math.Log(2.0) / 7.0; // λ ≈ 0.099 → decay to 0.5 after 7h

            var scored = snapshot
                .Where(m => m.Embedding != null && m.Embedding.Length == queryEmbedding.Length)
                .Select(m => {
                    float  cos     = CosineSimilarity(queryEmbedding, m.Embedding);
                    double hours   = (DateTime.Now - m.Timestamp).TotalHours;
                    double recency = Math.Exp(-decayConstant * hours);
                    double final   = (cos * 0.7) + (recency * 0.3);
                    return new { Item = m, Final = final, Cos = cos };
                })
                .OrderByDescending(x => x.Final)
                .Take(limit)
                .ToList();

            return scored.Select(x => $"[MEM] (cos:{x.Cos:F2}) {x.Item.Content}").ToList();
        }

        /// <summary>
        /// Fix 3: Rewrites a natural-language query into search-optimized keywords.
        /// Skips rewriting for very short queries (already keywords).
        /// Falls back to original query on any error.
        /// </summary>
        private async Task<string> RewriteQueryAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return query;
            if (query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 3) return query;

            string prompt =
                "Rewrite the following question as a short sequence of search keywords for semantic vector search. " +
                "Output ONLY the keywords, no explanation, no punctuation.\n" +
                "Examples:\n" +
                "Q: Did Alex send the database credentials? → keywords: Alex database credentials password postgres login\n" +
                "Q: What was I working on last Tuesday? → keywords: project task code work Tuesday\n" +
                $"Q: {query} → keywords:";

            try
            {
                string raw = await _llm.GenerateText(prompt, temperature: 0.1f);
                string rewritten = raw.Trim().TrimStart(':').Trim();
                if (string.IsNullOrWhiteSpace(rewritten)) return query;
                System.Diagnostics.Debug.WriteLine($"[QueryRewrite] '{query}' → '{rewritten}'");
                return rewritten;
            }
            catch { return query; }
        }

        // --- SUMMARIZER & STATISTICS ---
        public async Task<string> GetDailySummary(DateTime date)
        {
            string dateStr = date.ToString("yyyy-MM-dd");

            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Summary FROM DailySummaries WHERE Date = $d";
            cmd.Parameters.AddWithValue("$d", dateStr);
            var res = await cmd.ExecuteScalarAsync();
            if (res != null) return res.ToString();

            List<MemoryItem> memories;
            lock (_cacheLock) { memories = _cachedMemories.Where(m => m.Timestamp.Date == date.Date).ToList(); }
            if (memories.Count == 0) return "No memories for this day.";

            var fullText = string.Join("\n", memories.Select(m => $"[{m.TimeSpanString()}] <{m.SourceApp}> {m.Content}"));

            string prompt = $@"
ANALYZE THESE LOGS AND CREATE A SUMMARY.
DATE: {dateStr}

LOGS:
{fullText}

OUTPUT FORMAT:
1. SUMMARY: [Brief narrative of what happened]
2. TOPICS: [List of main topics discussed/worked on]
3. STATISTICS: [Mention main apps used and time periods if visible]
";

            string summary = await _llm.GenerateText(prompt);

            using var conn2 = new SqliteConnection($"Data Source={_dbPath}");
            await conn2.OpenAsync();
            var cmd2 = conn2.CreateCommand();
            cmd2.CommandText = "INSERT INTO DailySummaries (Date, Summary) VALUES ($d, $s)";
            cmd2.Parameters.AddWithValue("$d", dateStr);
            cmd2.Parameters.AddWithValue("$s", summary);
            await cmd2.ExecuteNonQueryAsync();

            return summary;
        }

        // --- LEGACY GET RECENT (Fallback) ---
        public Task<List<string>> GetRecent(DateTime sessionStart)
        {
            List<MemoryItem> snapshot;
            lock (_cacheLock) { snapshot = new List<MemoryItem>(_cachedMemories); }
            return Task.FromResult(
                snapshot
                    .OrderByDescending(m => m.Timestamp)
                    .Take(10)
                    .Select(m => $"[{m.TimeSpanString()}] <{m.SourceApp}> {m.Content}")
                    .ToList());
        }

        // =========================================
        // MEMORY ENHANCEMENT V2
        // =========================================

        private string _cachedSessionSummary = "";
        private DateTime _lastSummaryTime = DateTime.MinValue;

        public async Task<string> GetSessionSummary(DateTime sessionStart)
        {
            if ((DateTime.Now - _lastSummaryTime).TotalMinutes < 10 && !string.IsNullOrEmpty(_cachedSessionSummary))
                return _cachedSessionSummary;

            List<MemoryItem> sessionMemories;
            lock (_cacheLock) { sessionMemories = _cachedMemories.Where(m => m.Timestamp >= sessionStart).OrderBy(m => m.Timestamp).ToList(); }
            if (sessionMemories.Count == 0) return "No activity this session.";

            var sample = new List<MemoryItem>();
            if (sessionMemories.Count <= 20)
            {
                sample = sessionMemories;
            }
            else
            {
                sample.AddRange(sessionMemories.Take(5));
                int midpoint = sessionMemories.Count / 2;
                sample.AddRange(sessionMemories.Skip(midpoint - 2).Take(5));
                sample.AddRange(sessionMemories.TakeLast(10));
            }

            var logText = string.Join("\n", sample.Select(m => $"{m.Timestamp:HH:mm} [{m.SourceApp}]: {m.Content.Substring(0, Math.Min(m.Content.Length, 100))}"));

            string prompt = $@"
SUMMARIZE THIS SESSION LOG IN 2-3 SENTENCES.
Focus on: What did the user DO? What apps did they use? What topics were discussed?

LOG:
{logText}

SUMMARY:";

            _cachedSessionSummary = await _llm.GenerateText(prompt);
            _lastSummaryTime = DateTime.Now;
            return _cachedSessionSummary;
        }

        public async Task<string> GetSmartContext(string userQuery, DateTime sessionStart)
        {
            var contextParts = new List<string>();

            string sessionSummary = await GetSessionSummary(sessionStart);
            if (!string.IsNullOrEmpty(sessionSummary))
                contextParts.Add($"[SESSION CONTEXT]: {sessionSummary}");

            var relevantMemories = await SearchRelevant(userQuery, 3);
            if (relevantMemories.Count > 0)
                contextParts.Add($"[RELEVANT MEMORIES]:\n{string.Join("\n", relevantMemories)}");

            List<MemoryItem> snapshot;
            lock (_cacheLock) { snapshot = new List<MemoryItem>(_cachedMemories); }
            var recentMemories = snapshot
                .OrderByDescending(m => m.Timestamp)
                .Take(5)
                .Select(m => $"[{m.TimeSpanString()}] <{m.SourceApp}> {m.Content.Substring(0, Math.Min(m.Content.Length, 80))}")
                .ToList();
            if (recentMemories.Count > 0)
                contextParts.Add($"[RECENT ACTIVITY]:\n{string.Join("\n", recentMemories)}");

            return string.Join("\n\n", contextParts);
        }

        // =========================================
        // INTERACTION AGENT SUPPORT
        // =========================================

        public Task<List<MemoryItem>> GetRecentMemoriesAsync(int limit = 50)
        {
            List<MemoryItem> snapshot;
            lock (_cacheLock) { snapshot = new List<MemoryItem>(_cachedMemories); }
            return Task.FromResult(snapshot.OrderByDescending(m => m.Timestamp).Take(limit).ToList());
        }

        public async Task<SessionStats> GetSessionStatsAsync(DateTime sessionStart)
        {
            List<MemoryItem> sessionMemories;
            lock (_cacheLock) { sessionMemories = _cachedMemories.Where(m => m.Timestamp >= sessionStart).ToList(); }

            var topApps = sessionMemories
                .GroupBy(m => m.SourceApp)
                .OrderByDescending(g => g.Count())
                .Take(5)
                .Select(g => $"{g.Key} ({g.Count()})")
                .ToList();

            string summary = await GetSessionSummary(sessionStart);

            List<MemoryItem> all;
            lock (_cacheLock) { all = new List<MemoryItem>(_cachedMemories); }
            return new SessionStats
            {
                TotalMemories   = all.Count,
                SessionMemories = sessionMemories.Count,
                TopApps         = topApps,
                SessionSummary  = summary
            };
        }

        // --- UTILS ---
        private float[] DeserializeEmbedding(byte[] bytes)
        {
            var floats = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
            return floats;
        }

        private byte[] SerializeEmbedding(float[] embedding)
        {
            var bytes = new byte[embedding.Length * 4];
            Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        private float CosineSimilarity(float[] vecA, float[] vecB)
        {
            if (vecA.Length != vecB.Length) return 0;
            float dot = 0, normA = 0, normB = 0;
            for (int i = 0; i < vecA.Length; i++)
            {
                dot   += vecA[i] * vecB[i];
                normA += vecA[i] * vecA[i];
                normB += vecB[i] * vecB[i];
            }
            if (normA == 0 || normB == 0) return 0;
            return dot / (float)(Math.Sqrt(normA) * Math.Sqrt(normB));
        }
    }

    public static class DateExt
    {
        public static string TimeSpanString(this MemoryItem m) => m.Timestamp.ToString("HH:mm");
    }

    public class SessionStats
    {
        public int TotalMemories { get; set; }
        public int SessionMemories { get; set; }
        public List<string> TopApps { get; set; } = new List<string>();
        public string SessionSummary { get; set; } = "";
    }
}
