using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
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
    ///   3. First run: auth dialog appears for phone → code → optional 2FA
    ///   4. Session saved to %APPDATA%\Davos\telegram.dat (raw bytes, no encryption)
    ///
    /// Uses the WTelegramClient Stream constructor to bypass AES file encryption,
    /// which caused "key not valid size" errors in file-based session mode.
    ///
    /// All connection events are surfaced via OnLog (informational) and OnError (failures).
    /// Wire both to the Logs panel to see exactly what WTelegramClient is doing.
    /// </summary>
    public class TelegramService : IDisposable
    {
        private Client? _client;
        private readonly string _davosDir;
        private readonly string _sessionDataPath;   // telegram.dat — raw session bytes
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

        /// <summary>Fired on connection errors and failures.</summary>
        public event Action<string>? OnError;

        /// <summary>Fired for informational log messages (connection progress, config keys, etc.).</summary>
        public event Action<string>? OnLog;

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
        /// Starts the Telegram client. Logs every step to OnLog so progress is visible
        /// in the Logs panel. Includes a 90-second timeout for the login phase.
        /// Run in background — blocks on first-run auth prompts.
        /// </summary>
        public async Task StartAsync()
        {
            StatusMessage = "Connecting…";
            Log("[Telegram] StartAsync — building session stream");

            // Build session stream from disk if available, else start fresh
            var stream = new PersistOnFlushStream(_sessionDataPath);
            if (File.Exists(_sessionDataPath))
            {
                try
                {
                    byte[] saved = File.ReadAllBytes(_sessionDataPath);
                    stream.Write(saved, 0, saved.Length);
                    stream.Position = 0;
                    Log($"[Telegram] Loaded existing session ({saved.Length} bytes)");
                }
                catch (Exception ex)
                {
                    Log($"[Telegram] Cannot load session: {ex.Message} — starting fresh");
                }
            }
            else
            {
                Log("[Telegram] No session file found — fresh auth will be needed");
            }

            try
            {
                Log("[Telegram] Creating WTelegramClient (Stream mode, no AES encryption)…");
                _client           = new Client(ConfigFunc, stream);
                _client.OnUpdate += OnUpdate;

                Log("[Telegram] Calling LoginUserIfNeeded (90 s timeout)…");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                await _client.LoginUserIfNeeded().WaitAsync(cts.Token);

                // Persist the fully-authenticated session to disk immediately
                stream.SaveNow();

                IsConnected   = true;
                StatusMessage = "Connected";
                Log("[Telegram] Connected ✓ — session saved to telegram.dat");
            }
            catch (OperationCanceledException)
            {
                _client?.Dispose();
                _client = null;
                // Delete any partial session so next startup starts fresh
                try { File.Delete(_sessionDataPath); } catch { }
                StatusMessage = "Error: Connection timed out (90 s) — check network/firewall";
                OnError?.Invoke("[Telegram] Connection timed out after 90 s. Is Telegram accessible from your network?");
            }
            catch (Exception ex)
            {
                _client?.Dispose();
                _client = null;
                // Delete partial session — a new one will be created on next auth
                try { File.Delete(_sessionDataPath); } catch { }
                SetError(ex);
            }
        }

        private string ConfigFunc(string what)
        {
            // Log every key WTelegramClient requests (mask sensitive values for privacy)
            string tag = what switch
            {
                "api_id"            => $"api_id={_apiId}",
                "api_hash"          => "api_hash=<masked>",
                "session_key"       => "session_key=<masked>",
                "phone_number"      => "phone_number (will show auth dialog)",
                "verification_code" => "verification_code (will show auth dialog)",
                "password"          => "password/2FA (will show auth dialog)",
                _                   => $"key={what}"
            };
            Log($"[Telegram] ConfigFunc: {tag}");

            return what switch
            {
                "api_id"            => _apiId.ToString(),
                "api_hash"          => _apiHash,
                "session_key"       => _apiHash,   // AES key for session encryption; api_hash is 32 hex chars = 32 UTF-8 bytes = valid AES-256
                // session_pathname is never called when Stream is passed to ctor
                "phone_number"      => PromptUser("Telegram phone (+1234567890): "),
                "verification_code" => PromptUser("Telegram verification code: "),
                "password"          => PromptUser("2FA password (Enter to skip): "),
                _                   => ""
            };
        }

        private string PromptUser(string prompt)
        {
            Log($"[Telegram] Showing auth dialog: \"{prompt}\"");
            if (AuthPrompt != null) return AuthPrompt(prompt);
            // Debug fallback — only works if a console is attached
            Console.Write(prompt);
            return Console.ReadLine()?.Trim() ?? "";
        }

        private void Log(string msg)
        {
            System.Diagnostics.Debug.WriteLine(msg);
            OnLog?.Invoke(msg);
        }

        private void SetError(Exception ex)
        {
            var msgs = new List<string>();
            for (var e = ex; e != null; e = e.InnerException) msgs.Add(e.Message);
            string fullMsg = string.Join(" → ", msgs);
            StatusMessage = $"Error: {fullMsg}";
            System.Diagnostics.Debug.WriteLine($"[Telegram] Error: {ex}");
            OnError?.Invoke($"[Telegram error] {fullMsg}");
        }

        /// <summary>
        /// Deletes all telegram* files so the next StartAsync() begins fresh (auth dialog appears).
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

            // Skip broadcast channels (PeerChannel) — only DMs (PeerUser) and small groups (PeerChat)
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
                PeerUser   _    => senderName,
                PeerChat   pc   => $"Group_{pc.chat_id}",
                PeerChannel pch => $"Channel_{pch.channel_id}",
                _               => "Unknown"
            };

            var tgMsg = new TgMessage(senderName, chatName, msg.message, msg.Date);
            lock (_lock)
            {
                _recent.Add(tgMsg);
                if (_recent.Count > MAX_RECENT) _recent.RemoveAt(0);
            }

            DataLake?.Write("telegram", msg.message, new { sender = senderName, chat = chatName });

            if (Memory != null)
                _ = Memory.Save($"[Telegram] {senderName} in {chatName}: {msg.message}", "Telegram");

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

        private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";

        private record TgMessage(string Sender, string Chat, string Text, DateTime When);

        public void Dispose()
        {
            if (_client != null)
            {
                _client.OnUpdate -= OnUpdate;
                _client.Dispose();
            }
        }

        // ====================================================================
        // PersistOnFlushStream — MemoryStream that auto-saves to disk on Flush.
        // Bypasses WTelegramClient's internal AES file encryption entirely.
        // ====================================================================

        private sealed class PersistOnFlushStream : MemoryStream
        {
            private readonly string _path;

            public PersistOnFlushStream(string path) : base() { _path = path; }

            public override void Flush()
            {
                base.Flush();
                SaveNow();
            }

            public void SaveNow()
            {
                try { File.WriteAllBytes(_path, ToArray()); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Telegram] Session save failed: {ex.Message}");
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) SaveNow();
                base.Dispose(disposing);
            }
        }
    }
}
