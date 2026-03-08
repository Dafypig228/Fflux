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
    ///   4. Session saved to %APPDATA%\Davos\telegram.dat — no re-auth after
    ///
    /// Session storage uses the WTelegramClient Stream constructor overload, which bypasses
    /// the library's internal AES encryption of the session file (the source of the
    /// "Specified key is not a valid size" CryptographicException in file mode).
    ///
    /// Messages are stored in MemoryService (semantic search) and DataLake (SQL).
    /// Context available via GetRecentMessages() for BuildDynamicContext.
    /// </summary>
    public class TelegramService : IDisposable
    {
        private Client? _client;
        private readonly string _davosDir;
        private readonly string _sessionDataPath;   // telegram.dat — raw session bytes, no encryption
        private readonly int    _apiId;
        private readonly string _apiHash;

        // In-memory ring buffer of recent messages
        private readonly List<TgMessage> _recent = new();
        private readonly object _lock = new();
        private const int MAX_RECENT = 50;

        // Optional services — set after construction
        public DataLakeService? DataLake { get; set; }
        public MemoryService?   Memory   { get; set; }

        public bool   IsConnected   { get; private set; }
        public int    MessageCount  => _recent.Count;
        public string StatusMessage { get; private set; } = "Not started";

        /// <summary>Fired on connection errors. Carries the full error string.</summary>
        public event Action<string>? OnError;

        /// <summary>
        /// Called when WTelegramClient needs phone/code/password from the user.
        /// Set this to a WPF dialog callback; falls back to Console.ReadLine if null.
        /// </summary>
        public Func<string, string>? AuthPrompt { get; set; }

        public TelegramService(int apiId, string apiHash)
        {
            _apiId           = apiId;
            _apiHash         = apiHash;
            _davosDir        = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Davos");
            _sessionDataPath = Path.Combine(_davosDir, "telegram.dat");
        }

        /// <summary>
        /// Starts the Telegram client using stream-based session storage.
        /// Stream mode bypasses WTelegramClient's AES session-file encryption entirely,
        /// so no CryptographicException can occur regardless of api_hash length.
        /// Run in background — blocks on first-run auth.
        /// </summary>
        public async Task StartAsync()
        {
            StatusMessage = "Connecting…";
            try
            {
                // Build a self-persisting, unencrypted session stream.
                // WTelegramClient reads/writes raw bytes to it — no AES key derivation from api_hash.
                var stream = new AutoSaveStream(_sessionDataPath);
                if (File.Exists(_sessionDataPath))
                {
                    byte[] saved = File.ReadAllBytes(_sessionDataPath);
                    stream.Write(saved, 0, saved.Length);
                    stream.Position = 0; // rewind so WTelegramClient can read the saved session
                }

                _client           = new Client(ConfigFunc, stream);
                _client.OnUpdate += OnUpdate;
                await _client.LoginUserIfNeeded();
                IsConnected   = true;
                StatusMessage = "Connected";
                System.Diagnostics.Debug.WriteLine("[Telegram] Connected ✓");
            }
            catch (Exception ex)
            {
                _client?.Dispose();
                _client = null;
                SetError(ex);
            }
        }

        private string ConfigFunc(string what) => what switch
        {
            "api_id"            => _apiId.ToString(),
            "api_hash"          => _apiHash,
            // "session_pathname" is NOT used when a sessionStore Stream is passed to Client ctor
            "phone_number"      => PromptUser("Telegram phone (+1234567890): "),
            "verification_code" => PromptUser("Telegram verification code: "),
            "password"          => PromptUser("2FA password (Enter to skip): "),
            _                   => ""
        };

        private string PromptUser(string prompt)
        {
            if (AuthPrompt != null) return AuthPrompt(prompt);
            // Debug fallback — only works if a console is attached
            Console.Write(prompt);
            return Console.ReadLine()?.Trim() ?? "";
        }

        private void SetError(Exception ex)
        {
            var msgs = new System.Collections.Generic.List<string>();
            for (var e = ex; e != null; e = e.InnerException) msgs.Add(e.Message);
            string fullMsg = string.Join(" → ", msgs);
            StatusMessage = $"Error: {fullMsg}";
            System.Diagnostics.Debug.WriteLine($"[Telegram] Start error: {ex}");
            OnError?.Invoke($"[Telegram error] {fullMsg}");
        }

        /// <summary>
        /// Deletes all telegram* files (telegram.dat, any legacy telegram.session files, etc.)
        /// so the next StartAsync() begins a fresh session with the auth dialog.
        /// </summary>
        internal void DeleteSessionFiles()
        {
            try
            {
                if (Directory.Exists(_davosDir))
                    foreach (var f in Directory.GetFiles(_davosDir, "telegram*"))
                        try { File.Delete(f); } catch { }
            }
            catch { }
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

            // Skip broadcast channels (PeerChannel) — only keep DMs (PeerUser) and small groups (PeerChat)
            if (msg.peer_id is PeerChannel) return Task.CompletedTask;

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

        // =====================================================================
        // AutoSaveStream — MemoryStream that persists to disk on every Flush().
        // Passed to the WTelegramClient Client constructor so session bytes are
        // stored as raw (unencrypted) data, bypassing the AES file encryption
        // that caused "Specified key is not a valid size" on every startup.
        // =====================================================================

        private sealed class AutoSaveStream : MemoryStream
        {
            private readonly string _path;

            public AutoSaveStream(string path) : base() { _path = path; }

            public override void Flush()
            {
                base.Flush();
                try { File.WriteAllBytes(_path, ToArray()); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Telegram] Session save failed: {ex.Message}");
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    try { File.WriteAllBytes(_path, ToArray()); } catch { }
                base.Dispose(disposing);
            }
        }
    }
}
