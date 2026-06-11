using System;
using System.IO;
using System.Text.Json;

namespace FluxCore
{
    /// <summary>
    /// Persistent settings for Davos UI.
    /// Saved to %APPDATA%\Davos\settings.json
    /// </summary>
    public class AppSettings
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Davos", "settings.json");

        // Window appearance
        public double WindowOpacity { get; set; } = 0.95;
        public double BlurRadius { get; set; } = 10;
        
        // Window position (optional - for remembering location)
        public double WindowTop { get; set; } = 100;
        public double WindowLeft { get; set; } = 100;
        
        // Behavior
        public bool RequireWakeWord { get; set; } = true;
        public string WakeWord { get; set; } = "Davos";
        public bool AutoMinimizeOnComplete { get; set; } = true;

        // Validation
        public string ValidationDepth { get; set; } = "Normal"; // Fast, Normal, Thorough

        // Model configuration
        public bool EnableLocalModel { get; set; } = false;
        public string LocalModelUrl { get; set; } = "http://localhost:8080";
        public string LocalModelId { get; set; } = "deepseek-r1:1.5b";

        // STT (ElevenLabs Scribe v2 Realtime)
        public string ElevenLabsApiKey { get; set; } = "";
        public bool SttRussian { get; set; } = true;
        public bool SttEnglish { get; set; } = true;
        public bool SttKazakh  { get; set; } = true;

        /// <summary>Builds the comma-separated language_code string for ElevenLabs URL.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string SttLanguageCodes
        {
            get
            {
                var codes = new System.Collections.Generic.List<string>();
                if (SttRussian) codes.Add("ru");
                if (SttEnglish) codes.Add("en");
                if (SttKazakh)  codes.Add("kk");
                return string.Join(",", codes);
            }
        }

        // TTS (Gemini Live API audio output)
        public bool TtsEnabled { get; set; } = false;
        public string TtsVoice { get; set; } = "Kore"; // Aoede, Charon, Fenrir, Kore, Puck

        // Telegram (WTelegramClient — get api_id + api_hash from https://my.telegram.org)
        public bool TelegramEnabled { get; set; } = false;
        public int    TelegramApiId   { get; set; } = 0;
        public string TelegramApiHash { get; set; } = "";
        /// <summary>Chat/user IDs to monitor. Empty list = all DMs and groups (no channels).</summary>
        public System.Collections.Generic.List<long> TelegramChatIds { get; set; } = new();

        // Inner Voice (autonomous companion loop)
        /// <summary>Enable the autonomous inner voice loop (requires Telegram to be configured).</summary>
        public bool InnerVoiceEnabled { get; set; } = false;
        /// <summary>
        /// The user's own Telegram user ID — Davos sends autonomous messages here.
        /// Paste your numeric Telegram user ID (visible in @userinfobot or the chat picker).
        /// </summary>
        public long TelegramOwnerChatId { get; set; } = 0;
        /// <summary>
        /// Channel ID that Davos posts to as admin/creator.
        /// When non-zero, Davos's autonomous messages appear as the channel (channel name + avatar),
        /// not your personal name. Get the numeric ID from the chat picker (type = "Channel").
        /// Set to 0 to fall back to DM mode (TelegramOwnerChatId).
        /// </summary>
        public long TelegramOwnerChannelId { get; set; } = 0;
        /// <summary>Daily character budget for inner monologue LLM calls (~4 chars per token). 0 = unlimited.</summary>
        public int InnerVoiceBudgetDailyChars { get; set; } = 500_000;
        /// <summary>Do-not-disturb start hour (24h local time, inclusive). Default: 23:00.</summary>
        public int DndStartHour { get; set; } = 23;
        /// <summary>Do-not-disturb end hour (24h local time, exclusive). Default: 08:00.</summary>
        public int DndEndHour { get; set; } = 8;

        /// <summary>
        /// Load settings from disk, or return defaults if file doesn't exist.
        /// </summary>
        public static AppSettings Load()
        {
            // Migrate settings from old FluxCore directory
            string oldDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FluxCore");
            string newDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Davos");
            if (Directory.Exists(oldDir) && !Directory.Exists(newDir))
                Directory.Move(oldDir, newDir);

            try
            {
                if (File.Exists(SettingsPath))
                {
                    string json = File.ReadAllText(SettingsPath);
                    return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Settings] Load error: {ex.Message}");
            }
            return new AppSettings();
        }

        /// <summary>
        /// Save settings to disk.
        /// </summary>
        public void Save()
        {
            try
            {
                string? dir = Path.GetDirectoryName(SettingsPath);
                if (dir != null && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Settings] Save error: {ex.Message}");
            }
        }
    }
}
