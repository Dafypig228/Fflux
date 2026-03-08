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
    }

    public class MemoryService
    {
        private readonly string _dbPath;
        private readonly ILLMService _llm;
        private List<MemoryItem> _cachedMemories = new List<MemoryItem>(); // Cache for fast search

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
            using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
            {
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
            }
            // Load cache on startup
            _ = LoadCacheAsync();
        }

        private async Task LoadCacheAsync()
        {
            _cachedMemories.Clear();
            using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
            {
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText = "SELECT Id, Content, SourceApp, Timestamp, Embedding FROM Memories";

                using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        var item = new MemoryItem
                        {
                            Id = reader.GetInt64(0),
                            Content = reader.GetString(1),
                            SourceApp = reader.IsDBNull(2) ? "Unknown" : reader.GetString(2),
                            Timestamp = DateTime.Parse(reader.GetString(3))
                        };

                        if (!reader.IsDBNull(4))
                        {
                            byte[] blob = (byte[])reader["Embedding"];
                            item.Embedding = DeserializeEmbedding(blob);
                        }

                        _cachedMemories.Add(item);
                    }
                }
            }
        }

        public async Task Save(string content, string appName)
        {
            if (string.IsNullOrWhiteSpace(content)) return;

            // 1. Generate Embedding
            float[] embedding = await _llm.GetEmbeddingAsync(content);

            // 2. Save to DB
            using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
            {
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO Memories (Content, SourceApp, Timestamp, Embedding) VALUES ($c, $a, $t, $e)";
                command.Parameters.AddWithValue("$c", content);
                command.Parameters.AddWithValue("$a", appName);
                command.Parameters.AddWithValue("$t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                command.Parameters.AddWithValue("$e", SerializeEmbedding(embedding));

                await command.ExecuteNonQueryAsync();
            }

            // 3. Add to Cache
            _cachedMemories.Add(new MemoryItem
            {
                Content = content,
                SourceApp = appName,
                Timestamp = DateTime.Now,
                Embedding = embedding
            });
        }

        // --- SUMMARIZER & STATISTICS ---
        public async Task<string> GetDailySummary(DateTime date)
        {
            string dateStr = date.ToString("yyyy-MM-dd");

            // 1. Check if summary exists
            using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
            {
                await connection.OpenAsync();
                var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT Summary FROM DailySummaries WHERE Date = $d";
                cmd.Parameters.AddWithValue("$d", dateStr);
                var res = await cmd.ExecuteScalarAsync();
                if (res != null) return res.ToString();
            }

            // 2. If not, generate it
            var memories = _cachedMemories.Where(m => m.Timestamp.Date == date.Date).ToList();
            if (memories.Count == 0) return "No memories for this day.";

            var fullText = string.Join("\n", memories.Select(m => $"[{m.TimeSpanString()}] <{m.SourceApp}> {m.Content}"));
            
            // Ask Gemini to summarize
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

            // 3. Save Summary
            using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
            {
                await connection.OpenAsync();
                var cmd = connection.CreateCommand();
                cmd.CommandText = "INSERT INTO DailySummaries (Date, Summary) VALUES ($d, $s)";
                cmd.Parameters.AddWithValue("$d", dateStr);
                cmd.Parameters.AddWithValue("$s", summary);
                await cmd.ExecuteNonQueryAsync();
            }
            
            return summary;
        }

        // --- VECTOR SEARCH ---
        public async Task<List<string>> SearchRelevant(string query, int limit = 5)
        {
            if (_cachedMemories.Count == 0) await LoadCacheAsync();
            
            // 1. Get query embedding
            float[] queryEmbedding = await _llm.GetEmbeddingAsync(query);
            if (queryEmbedding.Length == 0) return new List<string>();

            // 2. Cosine Similarity in Memory
            var scored = _cachedMemories
                .Where(m => m.Embedding != null && m.Embedding.Length > 0)
                .Select(m => new { Item = m, Score = CosineSimilarity(queryEmbedding, m.Embedding) })
                .OrderByDescending(x => x.Score)
                .Take(limit)
                .ToList();

            return scored.Select(x => $"[MEM] ({x.Score:F2}) {x.Item.Content}").ToList();
        }

        // --- LEGACY GET RECENT (Fallback) ---
        public async Task<List<string>> GetRecent(DateTime sessionStart)
        {
             // Simply return last 10 from cache
             return _cachedMemories
                .OrderByDescending(m => m.Timestamp)
                .Take(10)
                .Select(m => $"[{m.TimeSpanString()}] <{m.SourceApp}> {m.Content}")
                .ToList();
        }

        // =========================================
        // MEMORY ENHANCEMENT V2
        // =========================================
        
        private string _cachedSessionSummary = "";
        private DateTime _lastSummaryTime = DateTime.MinValue;
        
        /// <summary>
        /// Generates a rolling summary of what happened THIS session.
        /// Auto-refreshes every 10 minutes.
        /// </summary>
        public async Task<string> GetSessionSummary(DateTime sessionStart)
        {
            // Check if we need to refresh (every 10 mins)
            if ((DateTime.Now - _lastSummaryTime).TotalMinutes < 10 && !string.IsNullOrEmpty(_cachedSessionSummary))
                return _cachedSessionSummary;
            
            // Get all memories from this session
            var sessionMemories = _cachedMemories
                .Where(m => m.Timestamp >= sessionStart)
                .OrderBy(m => m.Timestamp)
                .ToList();
            
            if (sessionMemories.Count == 0) return "No activity this session.";
            
            // Sample: Take first 5, middle 5, last 10 to avoid token explosion
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
        
        /// <summary>
        /// SMART CONTEXT: Blends Recent + Relevant + Session Summary into one context block.
        /// This is the main method to call before each AI request.
        /// </summary>
        public async Task<string> GetSmartContext(string userQuery, DateTime sessionStart)
        {
            var contextParts = new List<string>();
            
            // 1. SESSION SUMMARY (What happened today/this session)
            string sessionSummary = await GetSessionSummary(sessionStart);
            if (!string.IsNullOrEmpty(sessionSummary))
                contextParts.Add($"[SESSION CONTEXT]: {sessionSummary}");
            
            // 2. QUERY-RELEVANT MEMORIES (Semantic search)
            var relevantMemories = await SearchRelevant(userQuery, 3);
            if (relevantMemories.Count > 0)
                contextParts.Add($"[RELEVANT MEMORIES]:\n{string.Join("\n", relevantMemories)}");
            
            // 3. RECENT CONTEXT (Last 5 interactions)
            var recentMemories = _cachedMemories
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
        
        /// <summary>
        /// Returns recent memories for display in the Memory Tab.
        /// </summary>
        public async Task<List<MemoryItem>> GetRecentMemoriesAsync(int limit = 50)
        {
            if (_cachedMemories.Count == 0) await LoadCacheAsync();
            
            return _cachedMemories
                .OrderByDescending(m => m.Timestamp)
                .Take(limit)
                .ToList();
        }

        /// <summary>
        /// Returns session statistics for the Stats Tab.
        /// </summary>
        public async Task<SessionStats> GetSessionStatsAsync(DateTime sessionStart)
        {
            if (_cachedMemories.Count == 0) await LoadCacheAsync();
            
            var sessionMemories = _cachedMemories.Where(m => m.Timestamp >= sessionStart).ToList();
            
            // Top apps by frequency
            var topApps = sessionMemories
                .GroupBy(m => m.SourceApp)
                .OrderByDescending(g => g.Count())
                .Take(5)
                .Select(g => $"{g.Key} ({g.Count()})")
                .ToList();
            
            // Get or generate session summary
            string summary = await GetSessionSummary(sessionStart);
            
            return new SessionStats
            {
                TotalMemories = _cachedMemories.Count,
                SessionMemories = sessionMemories.Count,
                TopApps = topApps,
                SessionSummary = summary
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
           var dotProduct = 0.0f;
           var normA = 0.0f;
           var normB = 0.0f;
           for (int i = 0; i < vecA.Length; i++)
           {
               dotProduct += vecA[i] * vecB[i];
               normA += vecA[i] * vecA[i];
               normB += vecB[i] * vecB[i];
           }
           if (normA == 0 || normB == 0) return 0;
           return dotProduct / (float)(Math.Sqrt(normA) * Math.Sqrt(normB));
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