using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TL;
using WTelegram;

namespace FluxCore
{
    /// <summary>
    /// Receives Telegram messages in real-time via WTelegramClient (MTProto).
    ///
    /// Setup (one-time):
    ///   1. Get api_id + api_hash from https://my.telegram.org
    ///   2. Set TelegramEnabled=true, TelegramApiId, TelegramApiHash in AppSettings
    ///   3. First run: prompts for phone → verification code → optional 2FA password
    ///   4. Session saved to %APPDATA%\Davos\telegram.session — no re-auth after
    ///
    /// Messages are stored in MemoryService (semantic search) and DataLake (SQL).
    /// Context available via GetRecentMessages() for BuildDynamicContext.
    /// </summary>
    public class TelegramService : IDisposable
    {
        private Client? _client;
        private readonly string _sessionPath;
        private readonly int _apiId;
        private readonly string _apiHash;

        // In-memory ring buffer of recent messages
        private readonly List<TgMessage> _recent = new();
        private readonly object _lock = new();
        private const int MAX_RECENT = 50;

        // Optional services — set after construction
        public DataLakeService? DataLake { get; set; }
        public MemoryService?   Memory   { get; set; }

        public bool IsConnected { get; private set; }

        public TelegramService(int apiId, string apiHash)
        {
            _apiId       = apiId;
            _apiHash     = apiHash;
            _sessionPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Davos", "telegram.session");
        }

        /// <summary>
        /// Starts the Telegram client. Run in background — blocks on first-run auth.
        /// Subsequent starts use the saved session and are instant.
        /// </summary>
        public async Task StartAsync()
        {
            try
            {
                _client = new Client(ConfigFunc);
                _client.OnUpdate += OnUpdate;
                await _client.LoginUserIfNeeded();
                IsConnected = true;
                System.Diagnostics.Debug.WriteLine("[Telegram] Connected ✓");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Telegram] Start error: {ex.Message}");
            }
        }

        private string ConfigFunc(string what) => what switch
        {
            "api_id"            => _apiId.ToString(),
            "api_hash"          => _apiHash,
            "session_pathname"  => _sessionPath,
            "phone_number"      => PromptUser("Telegram phone (+1234567890): "),
            "verification_code" => PromptUser("Telegram verification code: "),
            "password"          => PromptUser("2FA password (Enter to skip): "),
            _                   => ""
        };

        // Console I/O runs fine on background threads in WPF
        private static string PromptUser(string prompt)
        {
            Console.Write(prompt);
            return Console.ReadLine()?.Trim() ?? "";
        }

        private async Task OnUpdate(IObject arg)
        {
            if (arg is not UpdatesBase updates) return;

            // UpdatesBase.Users is populated from the server for this batch
            var users = updates.Users;

            foreach (var update in updates.UpdateList)
            {
                try
                {
                    if (update is UpdateNewMessage { message: TL.Message msg })
                        await HandleMessageAsync(msg, users);
                }
                catch { }
            }
        }

        private Task HandleMessageAsync(TL.Message msg, Dictionary<long, User> users)
        {
            if (string.IsNullOrEmpty(msg.message)) return Task.CompletedTask;

            // Resolve sender name
            string senderName = "Unknown";
            if (msg.from_id is PeerUser pu && users.TryGetValue(pu.user_id, out var user))
            {
                senderName = $"{user.first_name} {user.last_name}".Trim();
                if (string.IsNullOrEmpty(senderName)) senderName = user.username ?? $"user_{pu.user_id}";
            }

            // Resolve chat name
            string chatName = msg.peer_id switch
            {
                PeerUser   _   => senderName,              // DM
                PeerChat   pc  => $"Group_{pc.chat_id}",
                PeerChannel pch => $"Channel_{pch.channel_id}",
                _              => "Unknown"
            };

            var when  = msg.Date; // TL.Message.Date is already DateTime (UTC) in WTelegramClient
            var tgMsg = new TgMessage(senderName, chatName, msg.message, when);

            lock (_lock)
            {
                _recent.Add(tgMsg);
                if (_recent.Count > MAX_RECENT) _recent.RemoveAt(0);
            }

            // Persist to data lake (fire-and-forget — never block incoming updates)
            DataLake?.Write("telegram", msg.message,
                new { sender = senderName, chat = chatName });

            // Store in semantic memory
            if (Memory != null)
            {
                string memContent = $"[Telegram] {senderName} in {chatName}: {msg.message}";
                _ = Memory.Save(memContent, "Telegram");
            }

            return Task.CompletedTask;
        }

        /// <summary>Returns recent messages formatted for LLM context injection.</summary>
        public string GetRecentMessages(int count = 10)
        {
            lock (_lock)
            {
                if (_recent.Count == 0) return "";
                var sb = new StringBuilder("=== TELEGRAM (recent) ===\n");
                foreach (var m in _recent.TakeLast(count))
                    sb.AppendLine($"  [{m.When:HH:mm}] {m.Sender} ({m.Chat}): {Trim(m.Text, 200)}");
                return sb.ToString();
            }
        }

        private static string Trim(string s, int max) =>
            s.Length <= max ? s : s[..max] + "…";

        private record TgMessage(string Sender, string Chat, string Text, DateTime When);

        public void Dispose()
        {
            if (_client != null)
            {
                _client.OnUpdate -= OnUpdate;
                _client.Dispose();
            }
        }
    }
}
