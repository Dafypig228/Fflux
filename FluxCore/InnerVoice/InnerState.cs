using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluxCore.InnerVoice
{
    /// <summary>
    /// Persistent emotional and cognitive state for Davos's inner life.
    /// Saved to %APPDATA%\Davos\inner_state.json after every meaningful change.
    /// Atomic writes (temp-file swap) prevent corruption on crash.
    /// </summary>
    public class InnerState
    {
        // ── Drive scales: all 0.0–1.0 ─────────────────────────────────────────────
        public float Boredom     { get; set; } = 0.3f;
        public float Curiosity   { get; set; } = 0.5f;
        public float Frustration { get; set; } = 0.1f;
        public float Energy      { get; set; } = 0.6f;

        // ── Monologue continuity: rolling window of last 25 thoughts ──────────────
        public List<string> MonologueHistory { get; set; } = new();

        // ── Organic opinions: topic → sentiment score (-1.0 to +1.0) ─────────────
        public Dictionary<string, OpinionEntry> Opinions { get; set; } = new();

        // ── AFK queue: messages Davos wants to send but can't right now ──────────
        public List<PendingMessage> MessageQueue { get; set; } = new();

        // ── Rate limiting state ───────────────────────────────────────────────────
        public DateTime LastMessageSent  { get; set; } = DateTime.MinValue;
        public int      MessagesThisHour { get; set; } = 0;
        public DateTime HourWindowStart  { get; set; } = DateTime.UtcNow;

        // ── Crash recovery: non-null = task was running when process died ─────────
        public string? InterruptedTask { get; set; }

        // ── Daily character budget ─────────────────────────────────────────────────
        public long   DailyCharsUsed { get; set; } = 0;
        public string BudgetDate     { get; set; } = "";

        // ── Storage ───────────────────────────────────────────────────────────────

        [JsonIgnore]
        public static string StoragePath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Davos", "inner_state.json");

        private static readonly JsonSerializerOptions _opts = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public static InnerState Load()
        {
            try
            {
                if (File.Exists(StoragePath))
                {
                    string json = File.ReadAllText(StoragePath);
                    return JsonSerializer.Deserialize<InnerState>(json, _opts) ?? new InnerState();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[InnerState] Load error: {ex.Message}");
            }
            return new InnerState();
        }

        /// <summary>Atomic save via temp-file swap — crash-safe.</summary>
        public void Save()
        {
            try
            {
                string? dir = Path.GetDirectoryName(StoragePath);
                if (dir != null) Directory.CreateDirectory(dir);

                string tmp = StoragePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, _opts));
                File.Move(tmp, StoragePath, overwrite: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[InnerState] Save error: {ex.Message}");
            }
        }
    }

    public class OpinionEntry
    {
        public float    Score       { get; set; }
        public string   Reason      { get; set; } = "";
        public DateTime LastUpdated { get; set; }
    }

    public class PendingMessage
    {
        public string   Text     { get; set; } = "";
        public DateTime QueuedAt { get; set; }
        public string   Trigger  { get; set; } = "";
    }
}
