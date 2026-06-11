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
    ///   1. Semantic search     — MemoryService vector similarity (query-rewritten + time-weighted)
    ///   2. GraphRAG pivot      — extract entity names from retrieved chunks, look up in KG
    ///   3. Data lake SQL       — exact keyword matches in recent events
    ///   4. Merge + truncate    → unified string for LLM context
    ///
    /// Keeps existing MemoryService for embeddings — wraps, doesn't replace it.
    /// </summary>
    public class MemoryEngine
    {
        private readonly MemoryService?         _memory;
        private readonly DataLakeService?       _lake;
        private readonly KnowledgeGraphService? _kg;

        // Budget limits per retrieval call
        private const int MaxSemanticResults = 5;
        private const int MaxLakeRows        = 10;
        private const int MaxOutputChars     = 3000;

        // GraphRAG: KG node name cache to avoid a DB query on every search call
        private HashSet<string> _kgNodeCache = new(StringComparer.OrdinalIgnoreCase);
        private DateTime        _kgCacheTime = DateTime.MinValue;
        private readonly object _kgCacheLock = new();

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

            // ── Step 1: Semantic search (includes query rewrite + time-weighted scoring) ──
            List<string> semanticHits = new();
            try
            {
                if (_memory != null)
                {
                    semanticHits = await _memory.SearchRelevant(query, limit: MaxSemanticResults);
                    if (semanticHits.Count > 0)
                        parts.Add("=== SEMANTIC MEMORY ===\n  " +
                                  string.Join("\n  ", semanticHits));
                }
            }
            catch { }

            // ── Step 2: GraphRAG pivot — extract entities from chunks, then KG lookup ─────
            if (_kg != null)
            {
                var entityNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (semanticHits.Count > 0)
                {
                    // Strip the [MEM] (cos:x.xx) prefix to get raw chunk text
                    var chunkTexts = semanticHits
                        .Select(h => Regex.Replace(h, @"^\[MEM\].*?\)\s*", ""))
                        .ToList();

                    var kgNodeNames = GetCachedNodeNames();
                    foreach (string chunk in chunkTexts)
                        foreach (string name in kgNodeNames)
                            if (chunk.Contains(name, StringComparison.OrdinalIgnoreCase))
                                entityNames.Add(name);
                }

                // Also add top keywords from raw query
                foreach (string kw in ExtractKeywords(query).Take(3))
                    entityNames.Add(kw);

                // KG lookup for each matched entity
                foreach (string entity in entityNames.Take(5))
                {
                    try
                    {
                        string kgResult = _kg.GetGraphContext(entity);
                        if (!string.IsNullOrEmpty(kgResult))
                            parts.Add(kgResult);
                    }
                    catch { }
                }
            }

            // ── Step 3: Data lake — recent events matching keywords ────────────────────
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
            => _lake?.GetRecent(source, count) ?? "";

        /// <summary>Top topics from knowledge graph for a system prompt snippet.</summary>
        public string GetTopTopicsContext(int n = 5)
            => _kg?.GetTopTopics(n) ?? "";

        // ── Helpers ───────────────────────────────────────────────────────────────

        private HashSet<string> GetCachedNodeNames()
        {
            lock (_kgCacheLock)
            {
                if ((DateTime.Now - _kgCacheTime).TotalMinutes < 5 && _kgNodeCache.Count > 0)
                    return _kgNodeCache;

                var names = _kg?.GetAllNodeNames() ?? Enumerable.Empty<string>();
                _kgNodeCache = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
                _kgCacheTime = DateTime.Now;
                return _kgNodeCache;
            }
        }

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
