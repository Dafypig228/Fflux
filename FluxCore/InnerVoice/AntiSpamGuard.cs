using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FluxCore.InnerVoice
{
    /// <summary>
    /// Guards against Davos becoming a spam bot or surveillance tool.
    ///
    /// Three layers of protection:
    ///   1. DND detection: night hours, gaming, intense coding → queue messages
    ///   2. AFK stack collapsing: user absent > 30 min → queue, collapse on return
    ///   3. Rate limiting: max 3 messages per hour, min 15 min between messages
    ///
    /// When blocked, messages go to the queue (Davos still "wants" to say things —
    /// they're just held back). On user return the queue collapses gracefully.
    /// </summary>
    public class AntiSpamGuard
    {
        private readonly AppSettings _settings;
        private readonly SensoryCortex _cortex;

        private DateTime _lastUserMessageTime = DateTime.UtcNow;

        private static readonly HashSet<string> _gamingProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            // Launchers
            "steam", "epicgameslauncher", "gog galaxy", "Battle.net", "upc", "riotclientservices",
            // Specific games (common)
            "cs2", "csgo", "valorant", "dota2", "LeagueOfLegends",
            "Minecraft", "Minecraft.Windows", "javaw",
            "GenshinImpact", "genshinimpact",
            "Cyberpunk2077", "witcher3", "witcher2",
            "RocketLeague", "fortnite", "ApexLegends",
            "RainbowSix", "r6s", "rainbow6",
            "overwatch", "overwatch2",
            "elden_ring", "eldenring", "sekiro",
            "pathofexile", "PathOfExile",
            // Generic patterns handled in IsGaming() substring check
        };

        private static readonly HashSet<string> _intenseCodingApps = new(StringComparer.OrdinalIgnoreCase)
        {
            "code",           // VS Code
            "devenv",         // Visual Studio
            "rider",          // JetBrains Rider
            "idea",           // IntelliJ IDEA
            "pycharm",
            "webstorm",
            "clion",
            "goland",
            "datagrip",
            "vim",
            "nvim",
            "emacs",
        };

        public AntiSpamGuard(AppSettings settings, SensoryCortex cortex)
        {
            _settings = settings;
            _cortex   = cortex;
        }

        /// <summary>
        /// Evaluate whether Davos should send a message right now.
        /// Returns Allow, Queue (defer + save), or Suppress (discard).
        /// </summary>
        public GuardDecision Evaluate(InnerState state, string messageText)
        {
            // 1. Night DND (user-configured quiet hours)
            int hour = DateTime.Now.Hour;
            bool inNightDnd = _settings.DndStartHour > _settings.DndEndHour
                ? (hour >= _settings.DndStartHour || hour < _settings.DndEndHour)  // e.g. 23–08 crosses midnight
                : (hour >= _settings.DndStartHour && hour < _settings.DndEndHour); // e.g. 01–06

            if (inNightDnd)
                return GuardDecision.Queue("Night DND active");

            // 2. Gaming DND
            if (IsGaming())
                return GuardDecision.Queue("User is gaming");

            // 3. Intense coding (focused dev app active + no chat for 20+ min)
            if (IsIntenseCoding())
                return GuardDecision.Queue("User in deep work");

            // 4. AFK (no interaction for 30+ min)
            if (IsUserAFK())
                return GuardDecision.Queue("User is AFK");

            // 5. Rate limit: max 3 per hour, min 15 min between messages
            if (!RateAllows(state))
                return GuardDecision.Suppress("Rate limit");

            return GuardDecision.Allow();
        }

        /// <summary>
        /// Called when the user sends any message.
        /// Returns a collapsed summary of queued messages (if any), or null.
        /// Clears the queue.
        /// </summary>
        public string? FlushQueue(InnerState state)
        {
            if (state.MessageQueue.Count == 0) return null;

            var msgs = state.MessageQueue.ToList();
            state.MessageQueue.Clear();
            state.Save();

            return msgs.Count switch
            {
                1 => msgs[0].Text,
                2 or 3 => CollapseMessages(msgs, brief: true),
                _ => "Hey, I had a few thoughts while you were away — want the highlights?"
            };
        }

        /// <summary>
        /// Notify the guard that the user just sent a message.
        /// Resets the AFK clock.
        /// </summary>
        public void NotifyUserMessage() => _lastUserMessageTime = DateTime.UtcNow;

        // ── Status queries ────────────────────────────────────────────────────────

        public bool IsUserAFK()         => (DateTime.UtcNow - _lastUserMessageTime).TotalMinutes > 30;
        public bool IsUserInDnd()       => IsNightDnd() || IsGaming() || IsIntenseCoding();
        public TimeSpan TimeSinceUser() => DateTime.UtcNow - _lastUserMessageTime;

        // ── Private helpers ───────────────────────────────────────────────────────

        private bool IsNightDnd()
        {
            int hour = DateTime.Now.Hour;
            return _settings.DndStartHour > _settings.DndEndHour
                ? (hour >= _settings.DndStartHour || hour < _settings.DndEndHour)
                : (hour >= _settings.DndStartHour && hour < _settings.DndEndHour);
        }

        private bool IsGaming()
        {
            try
            {
                string processes = _cortex.GetRunningProcesses();
                foreach (var line in processes.Split('\n'))
                    foreach (var g in _gamingProcesses)
                        if (line.Contains(g, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { /* SensoryCortex can occasionally fail */ }
            return false;
        }

        private bool IsIntenseCoding()
        {
            try
            {
                string activeWindow = _cortex.GetActiveWindow();
                bool focusedOnIde = false;
                foreach (var app in _intenseCodingApps)
                    if (activeWindow.Contains(app, StringComparison.OrdinalIgnoreCase))
                    {
                        focusedOnIde = true;
                        break;
                    }
                return focusedOnIde && (DateTime.UtcNow - _lastUserMessageTime).TotalMinutes > 20;
            }
            catch { return false; }
        }

        private static bool RateAllows(InnerState state)
        {
            var now = DateTime.UtcNow;

            // Reset hour window if expired
            if ((now - state.HourWindowStart).TotalHours >= 1.0)
            {
                state.MessagesThisHour = 0;
                state.HourWindowStart  = now;
            }

            // Max 3 per hour
            if (state.MessagesThisHour >= 3) return false;

            // Min 15 min between messages
            if ((now - state.LastMessageSent).TotalMinutes < 15) return false;

            return true;
        }

        private static string CollapseMessages(List<PendingMessage> msgs, bool brief)
        {
            if (!brief || msgs.Count <= 3)
            {
                var sb = new StringBuilder("While you were away, I was thinking:\n");
                foreach (var m in msgs)
                    sb.AppendLine($"• {m.Text}");
                return sb.ToString().TrimEnd();
            }
            return $"I had {msgs.Count} thoughts while you were away — want me to share them?";
        }
    }

    public class GuardDecision
    {
        public bool   Allowed     { get; private init; }
        public bool   ShouldQueue { get; private init; }
        public string Reason      { get; private init; } = "";

        public static GuardDecision Allow()           => new() { Allowed = true,  ShouldQueue = false };
        public static GuardDecision Queue(string r)   => new() { Allowed = false, ShouldQueue = true,  Reason = r };
        public static GuardDecision Suppress(string r)=> new() { Allowed = false, ShouldQueue = false, Reason = r };
    }
}
