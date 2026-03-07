using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using FluxCore.LLM;

namespace FluxCore
{
    /// <summary>
    /// MemGPT-style persistent memory.
    /// Two blocks always injected into every system prompt:
    ///   • Core   — who the user is, relationships, preferences (changes rarely)
    ///   • Working — current focus, today's highlights, recent topics (changes often)
    ///
    /// After every chat turn a cheap background LLM call decides whether to update.
    /// Never blocks Davos's reply.
    /// </summary>
    public class CoreMemoryService
    {
        private readonly ILLMService _llm;
        private readonly string      _path;
        private CoreMemoryBlocks     _blocks;
        private readonly object      _lock = new();

        // Hard character caps — keep system prompt footprint small
        private const int CoreCap    = 700;
        private const int WorkingCap = 450;

        public CoreMemoryService(ILLMService llm)
        {
            _llm  = llm;
            _path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Davos", "core_memory.json");

            _blocks = Load();
        }

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the memory block text to prepend to any system prompt.
        /// Always fits within CoreCap + WorkingCap characters.
        /// </summary>
        public string GetSystemPromptBlock()
        {
            lock (_lock)
            {
                var sb = new StringBuilder();
                sb.AppendLine("<davos_memory>");

                if (!string.IsNullOrWhiteSpace(_blocks.Core.Persona))
                    sb.AppendLine($"[persona] {Trunc(_blocks.Core.Persona, 200)}");

                if (!string.IsNullOrWhiteSpace(_blocks.Core.Human))
                    sb.AppendLine($"[human] {Trunc(_blocks.Core.Human, 200)}");

                if (_blocks.Core.Relationships?.Count > 0)
                {
                    sb.AppendLine("[relationships]");
                    foreach (var kv in _blocks.Core.Relationships)
                        sb.AppendLine($"  {kv.Key}: {kv.Value}");
                }

                if (_blocks.Core.Preferences?.Count > 0)
                    sb.AppendLine($"[preferences] {string.Join(", ", _blocks.Core.Preferences)}");

                if (!string.IsNullOrWhiteSpace(_blocks.Working.CurrentFocus))
                    sb.AppendLine($"[current_focus] {Trunc(_blocks.Working.CurrentFocus, 120)}");

                if (_blocks.Working.TodayHighlights?.Count > 0)
                    sb.AppendLine($"[today] {string.Join(" | ", _blocks.Working.TodayHighlights)}");

                if (_blocks.Working.RecentTopics?.Count > 0)
                    sb.AppendLine($"[recent_topics] {string.Join(", ", _blocks.Working.RecentTopics)}");

                sb.AppendLine("</davos_memory>");
                return sb.ToString();
            }
        }

        /// <summary>
        /// Fire-and-forget after each chat turn.
        /// Makes ONE cheap LLM call — updates memory if something new was learned.
        /// </summary>
        public async Task MaybeUpdateAsync(string userMessage, string davosReply)
        {
            try
            {
                string currentJson;
                lock (_lock) currentJson = JsonSerializer.Serialize(_blocks, _jsonOpts);

                string prompt = $@"You are a memory manager for Davos, an AI companion.

Current memory (JSON):
{currentJson}

Recent conversation:
USER: {Trunc(userMessage, 400)}
DAVOS: {Trunc(davosReply, 400)}

TASK: If this conversation reveals NEW information about the user (name, relationships, preferences, current focus, what they're working on today), return an UPDATED version of the JSON with those changes applied. Keep existing correct information. Be conservative — only update what clearly changed.
If nothing new was learned, return exactly: null

Return ONLY valid JSON or null. No explanation.";

                string raw = await _llm.GenerateText(prompt, temperature: 0.1f);
                raw = raw.Trim();
                if (raw == "null" || string.IsNullOrEmpty(raw)) return;

                // Strip markdown fences if present
                if (raw.StartsWith("```")) raw = raw.Split('\n', 2)[1];
                if (raw.EndsWith("```"))   raw = raw[..^3];

                var updated = JsonSerializer.Deserialize<CoreMemoryBlocks>(raw, _jsonOpts);
                if (updated == null) return;

                lock (_lock) _blocks = updated;
                Save();
            }
            catch { /* background — never throw */ }
        }

        // ── Persistence ───────────────────────────────────────────────────────────

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            WriteIndented         = true,
            PropertyNamingPolicy  = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private CoreMemoryBlocks Load()
        {
            try
            {
                if (File.Exists(_path))
                {
                    var json = File.ReadAllText(_path);
                    return JsonSerializer.Deserialize<CoreMemoryBlocks>(json, _jsonOpts)
                           ?? DefaultBlocks();
                }
            }
            catch { }
            return DefaultBlocks();
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string json;
                lock (_lock) json = JsonSerializer.Serialize(_blocks, _jsonOpts);
                File.WriteAllText(_path, json);
            }
            catch { }
        }

        private static CoreMemoryBlocks DefaultBlocks() => new()
        {
            Core = new CoreBlock
            {
                Persona       = "Davos — AI companion living on this PC. Friend, not tool.",
                Human         = "User details not yet known. Learn from conversations.",
                Relationships = new Dictionary<string, string>(),
                Preferences   = new List<string>()
            },
            Working = new WorkingBlock
            {
                CurrentFocus    = "",
                TodayHighlights = new List<string>(),
                RecentTopics    = new List<string>()
            }
        };

        private static string Trunc(string s, int max) =>
            s.Length <= max ? s : s[..max] + "…";
    }

    // ── Data model ────────────────────────────────────────────────────────────────

    public class CoreMemoryBlocks
    {
        [JsonPropertyName("core")]    public CoreBlock    Core    { get; set; } = new();
        [JsonPropertyName("working")] public WorkingBlock Working { get; set; } = new();
    }

    public class CoreBlock
    {
        [JsonPropertyName("persona")]       public string?                     Persona       { get; set; }
        [JsonPropertyName("human")]         public string?                     Human         { get; set; }
        [JsonPropertyName("relationships")] public Dictionary<string, string>? Relationships { get; set; }
        [JsonPropertyName("preferences")]   public List<string>?               Preferences   { get; set; }
    }

    public class WorkingBlock
    {
        [JsonPropertyName("currentFocus")]    public string?       CurrentFocus    { get; set; }
        [JsonPropertyName("todayHighlights")] public List<string>? TodayHighlights { get; set; }
        [JsonPropertyName("recentTopics")]    public List<string>? RecentTopics    { get; set; }
    }
}
