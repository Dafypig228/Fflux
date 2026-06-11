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

            // Create the file only if it doesn't exist yet (first run)
            if (!File.Exists(_path)) Save();
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

TASK: Update the memory JSON based on this conversation.

CORE block (persona, human, relationships, preferences):
  - Update ONLY if the user revealed something new about themselves, their name, relationships, or preferences.

WORKING block (currentFocus, todayHighlights, recentTopics):
  - Update FREELY every turn. Always capture:
    - currentFocus: what the user is working on RIGHT NOW
    - recentTopics: add the main topic(s) from this conversation (keep last 5)
    - todayHighlights: add any notable events from this turn (keep last 5)

Return the COMPLETE updated JSON, or null ONLY if the conversation was pure small talk with zero useful information.

Return ONLY valid JSON or null. No explanation, no markdown.";

                string raw = await _llm.GenerateText(prompt, temperature: 0.35f);
                raw = raw.Trim();
                if (raw == "null" || string.IsNullOrEmpty(raw)) return;

                // Strip markdown fences if present
                if (raw.StartsWith("```"))
                {
                    raw = raw.Split('\n', 2).LastOrDefault() ?? raw;
                    if (raw.EndsWith("```")) raw = raw[..^3];
                    raw = raw.Trim();
                }

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
                Persona       = "Davos — persistent AI living on this PC. Passive sensors: Telegram (MTProto API), clipboard, file events, terminal history, notifications, event log, Chrome bridge, VS Code. Memory layers: CoreMemory (this file), DataLake (raw events), KnowledgeGraph (entities), SemanticMemory (vector), Hippocampus (failure lessons). Never opens apps to read data already in context.",
                Human         = "User details unknown. Learn from conversations and passive context.",
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
