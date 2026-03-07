using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FluxCore
{
    /// <summary>
    /// Composite retrieval engine — single entry point for all historical data.
    ///
    /// Retrieval pipeline per query:
    ///   1. Knowledge graph   — find matching entities (who/what/project)
    ///   2. Data lake SQL     — pull exact recent events mentioning those entities
    ///   3. Semantic search   — MemoryService vector similarity (existing)
    ///   4. Merge + deduplicate → unified string for LLM context
    ///
    /// Keeps existing MemoryService for embeddings — wraps, doesn't replace it.
    /// </summary>
    public class MemoryEngine
    {
        private readonly MemoryService?       _memory;
        private readonly DataLakeService?     _lake;
        private readonly KnowledgeGraphService? _kg;

        // Budget limits per retrieval call
        private const int MaxSemanticResults = 5;
        private const int MaxLakeRows        = 10;
        private const int MaxOutputChars     = 3000;

        public MemoryEngine(
            MemoryService?          memory = null,
            DataLakeService?        lake   = null,
            KnowledgeGraphService?  kg     = null)
        {
            _memory = memory;
            _lake   = lake;
            _kg     = kg;
        }

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>
        /// Unified retrieval for a natural-language query.
        /// Returns a formatted context block ready for injection into an LLM prompt.
        /// </summary>
        public async Task<string> RetrieveAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return "";

            var parts = new List<string>();

            // 1. Knowledge graph — entity summary
            try
            {
                var entities = ExtractKeywords(query);
                foreach (var entity in entities.Take(3))
                {
                    string kgResult = _kg?.GetGraphContext(entity) ?? "";
                    if (!string.IsNullOrEmpty(kgResult))
                        parts.Add(kgResult);
                }
            }
            catch { }

            // 2. Data lake — recent events matching keywords
            try
            {
                if (_lake != null)
                {
                    var keywords = ExtractKeywords(query);
                    var seen     = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var sb       = new StringBuilder();

                    foreach (string source in new[] { "telegram", "clipboard", "chrome", "notification", "terminal" })
                    {
                        var rows = _lake.QueryRows(
                            $"SELECT ts, content FROM events WHERE source = '{source}'" +
                            $" ORDER BY ts DESC LIMIT {MaxLakeRows}");

                        var matched = rows
                            .Where(r => keywords.Any(kw =>
                                r.content.Contains(kw, StringComparison.OrdinalIgnoreCase)))
                            .Take(3);

                        foreach (var (ts, content) in matched)
                        {
                            string key = content[..Math.Min(50, content.Length)];
                            if (!seen.Add(key)) continue;
                            string time = TryParseTime(ts).ToString("HH:mm");
                            sb.AppendLine($"  [{source}@{time}] {Trim(content.Replace('\n', ' '), 200)}");
                        }
                    }

                    if (sb.Length > 0)
                        parts.Add("=== DATA LAKE MATCHES ===\n" + sb);
                }
            }
            catch { }

            // 3. Semantic memory search (existing MemoryService vector search)
            try
            {
                if (_memory != null)
                {
                    var semanticList = await _memory.SearchRelevant(query, limit: MaxSemanticResults);
                    if (semanticList != null && semanticList.Count > 0)
                        parts.Add("=== SEMANTIC MEMORY ===\n  " +
                                  string.Join("\n  ", semanticList));
                }
            }
            catch { }

            if (parts.Count == 0) return "";

            string combined = string.Join("\n", parts);
            return combined.Length > MaxOutputChars
                ? combined[..MaxOutputChars] + "\n[…memory truncated]"
                : combined;
        }

        /// <summary>
        /// Quick synchronous retrieval from data lake by source + count.
        /// No LLM calls, no semantic search. Use for context building.
        /// </summary>
        public string GetRecentBySource(string source, int count = 5)
        {
            return _lake?.GetRecent(source, count) ?? "";
        }

        /// <summary>Top topics from knowledge graph for a system prompt snippet.</summary>
        public string GetTopTopicsContext(int n = 5)
        {
            return _kg?.GetTopTopics(n) ?? "";
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Extracts candidate keywords from a natural language query.
        /// Removes stop words, returns meaningful tokens.
        /// </summary>
        private static List<string> ExtractKeywords(string query)
        {
            var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "the", "a", "an", "is", "are", "was", "were", "be", "been", "being",
                "have", "has", "had", "do", "does", "did", "will", "would", "could",
                "should", "may", "might", "shall", "can", "to", "of", "in", "on",
                "at", "by", "for", "with", "about", "what", "who", "how", "when",
                "where", "why", "which", "this", "that", "these", "those", "i", "me",
                "my", "you", "your", "he", "she", "it", "we", "they", "them", "their"
            };

            return Regex.Split(query.ToLower(), @"\W+")
                .Where(w => w.Length >= 3 && !stopWords.Contains(w))
                .Distinct()
                .ToList();
        }

        private static string Trim(string s, int max) =>
            s.Length <= max ? s : s[..max] + "…";

        private static DateTime TryParseTime(string iso)
        {
            try { return DateTime.Parse(iso, null,
                System.Globalization.DateTimeStyles.RoundtripKind).ToLocalTime(); }
            catch { return DateTime.Now; }
        }
    }
}
