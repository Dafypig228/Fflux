using System;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FluxCore
{
    /// <summary>
    /// ElevenLabs Scribe v2 Realtime WebSocket client — STT mode.
    /// Streams raw 16kHz PCM chunks from the mic and fires OnTranscript
    /// when the server-side VAD commits a speech segment (~150ms latency).
    ///
    /// Replaces GeminiLiveService for STT. Drop-in replacement — same public surface.
    /// </summary>
    public class ElevenLabsLiveService : IAsyncDisposable
    {
        private const string WsBase =
            "wss://api.elevenlabs.io/v1/speech-to-text/realtime?commit_strategy=vad";

        private readonly string _apiKey;
        private readonly string _language;
        private ClientWebSocket? _ws;
        private CancellationTokenSource _cts = new();
        private Task? _receiveLoop;
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public event Action<string>? OnTranscript;   // Final committed transcript (VAD end-of-speech)
        public event Action<string>? OnPartialText;  // Interim partial transcript (optional)
        public event Action<string>? OnError;
        public event Action? OnConnected;
        public event Action? OnDisconnected;

        public bool IsConnected => _ws?.State == WebSocketState.Open;

        public ElevenLabsLiveService(string apiKey = "d4740d591ffc07571bfe294a6384696710c49ff8d37158a3fce1e166296cf6fe", string language = "ru")
        {
            _apiKey = apiKey;
            _language = language;
        }

        /// <summary>Connect to ElevenLabs Scribe Realtime and start the receive loop.</summary>
        public async Task ConnectAsync(CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                OnError?.Invoke("ElevenLabs API key is empty — enter it in the Settings panel.");
                return;
            }

            await DisconnectAsync();

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ws = new ClientWebSocket();

            // Auth via header — must be set before ConnectAsync
            _ws.Options.SetRequestHeader("xi-api-key", _apiKey);

            string endpoint = WsBase;
            if (!string.IsNullOrWhiteSpace(_language))
                endpoint += $"&language_code={Uri.EscapeDataString(_language)}";

            var uri = new Uri(endpoint);

            Debug.WriteLine($"[ElevenLabs] Connecting to: {endpoint}");

            try
            {
                using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, connectTimeout.Token);

                await _ws.ConnectAsync(uri, linked.Token);
                Debug.WriteLine("[ElevenLabs] WebSocket connected — waiting for session_started");

                _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);

                // OnConnected is fired from ParseMessage when session_started arrives
            }
            catch (Exception ex)
            {
                string chain = DescribeException(ex);
                Debug.WriteLine($"[ElevenLabs] Connect failed: {chain}");
                OnError?.Invoke($"Connect failed: {chain}");

                try
                {
                    using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                    var resp = await http.GetAsync("https://api.elevenlabs.io/");
                    OnError?.Invoke($"[Diag] HTTPS OK ({(int)resp.StatusCode}) — WS blocked");
                }
                catch (Exception httpEx)
                {
                    OnError?.Invoke($"[Diag] HTTPS also failed: {DescribeException(httpEx)}");
                }
            }
        }

        /// <summary>
        /// Send a raw PCM audio chunk (16-bit, 16kHz, mono).
        /// Called directly from NAudio's DataAvailable event — must be fast.
        /// </summary>
        public async Task SendAudioChunkAsync(byte[] pcmData, int length)
        {
            if (_ws?.State != WebSocketState.Open) return;

            string base64 = Convert.ToBase64String(pcmData, 0, length);
            var msg = new
            {
                message_type = "input_audio_chunk",
                audio_base_64 = base64,
                commit = false,
                sample_rate = 16000
            };

            await SendRawAsync(JsonSerializer.Serialize(msg), _cts.Token);
        }

        private async Task SendRawAsync(string json, CancellationToken ct)
        {
            if (_ws?.State != WebSocketState.Open) return;

            await _sendLock.WaitAsync(ct);
            try
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ElevenLabs] Send error: {ex.Message}");
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[32 * 1024];

            try
            {
                while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
                {
                    var sb = new StringBuilder();
                    WebSocketReceiveResult result;

                    do
                    {
                        result = await _ws.ReceiveAsync(buffer, ct);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            var closeCode = _ws?.CloseStatus;
                            string reason = _ws?.CloseStatusDescription ?? "";
                            OnError?.Invoke($"Server closed [{closeCode}]: {(string.IsNullOrEmpty(reason) ? "no description" : reason)}");
                            return;
                        }
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    }
                    while (!result.EndOfMessage);

                    ParseMessage(sb.ToString());
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ElevenLabs] Receive error: {ex.Message}");
                OnError?.Invoke($"Stream error: {DescribeException(ex)}");
            }
            finally
            {
                OnDisconnected?.Invoke();
                Debug.WriteLine("[ElevenLabs] Receive loop ended");
            }
        }

        private void ParseMessage(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("message_type", out var typeEl)) return;
                string msgType = typeEl.GetString() ?? "";

                switch (msgType)
                {
                    case "session_started":
                        Debug.WriteLine("[ElevenLabs] Session started — ready for audio");
                        OnConnected?.Invoke();
                        break;

                    case "committed_transcript":
                        if (root.TryGetProperty("text", out var textEl))
                        {
                            string text = textEl.GetString()?.Trim() ?? "";
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                Debug.WriteLine($"[ElevenLabs] Transcript: {text}");
                                OnTranscript?.Invoke(text);
                            }
                        }
                        break;

                    case "partial_transcript":
                        if (root.TryGetProperty("text", out var partialEl))
                        {
                            string partial = partialEl.GetString()?.Trim() ?? "";
                            if (!string.IsNullOrWhiteSpace(partial))
                                OnPartialText?.Invoke(partial);
                        }
                        break;

                    case "error":
                        string errMsg = root.TryGetProperty("message", out var em) ? em.GetString() ?? json : json;
                        Debug.WriteLine($"[ElevenLabs] Server error: {errMsg}");
                        OnError?.Invoke($"ElevenLabs error: {errMsg}");
                        break;

                    default:
                        Debug.WriteLine($"[ElevenLabs] Unhandled message type: {msgType}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ElevenLabs] Parse error: {ex.Message}");
            }
        }

        public async Task DisconnectAsync()
        {
            _cts.Cancel();

            if (_ws != null && _ws.State == WebSocketState.Open)
            {
                try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnect", CancellationToken.None); }
                catch { }
            }

            _ws?.Dispose();
            _ws = null;

            if (_receiveLoop != null)
            {
                try { await _receiveLoop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
                _receiveLoop = null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync();
            _sendLock.Dispose();
            _cts.Dispose();
        }

        private static string DescribeException(Exception ex)
        {
            var sb = new StringBuilder();
            int depth = 0;
            Exception? cur = ex;
            while (cur != null)
            {
                if (depth++ > 0) sb.Append(" → ");
                sb.Append($"[{cur.GetType().Name}] {cur.Message}");
                if (cur is System.Net.Sockets.SocketException se)
                    sb.Append($" (SocketError={se.SocketErrorCode}, Native={se.NativeErrorCode})");
                if (cur is System.Net.WebSockets.WebSocketException we)
                    sb.Append($" (WsError={we.WebSocketErrorCode})");
                cur = cur.InnerException;
            }
            return sb.ToString();
        }
    }
}
