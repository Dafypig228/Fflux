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
    /// Gemini Live API WebSocket client — STT mode only.
    /// Streams raw 16kHz PCM chunks from the mic to Gemini's bidirectional endpoint
    /// and fires OnTranscript when speech is committed by the server-side VAD.
    ///
    /// Replaces the old batch approach (WAV → HTTP POST → 7s latency) with
    /// persistent WebSocket streaming → ~200–400ms latency.
    /// </summary>
    public class GeminiLiveService : IAsyncDisposable
    {
        private const string WsEndpoint =
            "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";
        private const string Model = "models/gemini-2.5-flash-native-audio-latest";

        private readonly string _apiKey;
        private ClientWebSocket? _ws;
        private CancellationTokenSource _cts = new();
        private Task? _receiveLoop;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private TaskCompletionSource<bool> _setupComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Watchdog: reconnect if transcription goes silent for >30s while audio is flowing
        private DateTime _lastAudioTime = DateTime.MinValue;
        private DateTime _lastTranscriptTime = DateTime.MinValue;
        private Task? _watchdog;

        public event Action<string>? OnTranscript;   // Final committed transcript
        public event Action<string>? OnError;
        public event Action? OnConnected;
        public event Action? OnDisconnected;

        public bool IsConnected => _ws?.State == WebSocketState.Open;

        public GeminiLiveService(string apiKey)
        {
            _apiKey = apiKey;
        }

        /// <summary>Connect to Gemini Live API and start the receive loop.</summary>
        public async Task ConnectAsync(CancellationToken ct = default)
        {
            await DisconnectAsync(); // clean up any previous connection

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ws = new ClientWebSocket();
            _setupComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var uri = new Uri($"{WsEndpoint}?key={_apiKey}");

            // Diagnostic: log the endpoint (key masked)
            string maskedUri = $"{WsEndpoint}?key=...{_apiKey[^4..]}";
            Debug.WriteLine($"[GeminiLive] Connecting to: {maskedUri}");
            OnError?.Invoke($"[Diag] Connecting to: {maskedUri}");

            try
            {
                // 10-second connect timeout — without this ConnectAsync hangs forever when
                // the server accepts TCP but never sends the 101 Switching Protocols response
                using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, connectTimeout.Token);

                await _ws.ConnectAsync(uri, linked.Token);
                Debug.WriteLine("[GeminiLive] WebSocket connected");

                // Send session setup — STT only (TEXT modality + input transcription)
                await SendRawAsync(BuildSetupMessage(), _cts.Token);

                _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);
                _watchdog = Task.Run(() => WatchdogLoopAsync(_cts.Token), _cts.Token);

                // Wait for server's setupComplete before signalling ready (timeout 5s)
                using var setupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await _setupComplete.Task.WaitAsync(setupTimeout.Token); }
                catch (OperationCanceledException) { Debug.WriteLine("[GeminiLive] setupComplete timeout — proceeding anyway"); }

                OnConnected?.Invoke();
            }
            catch (Exception ex)
            {
                string chain = DescribeException(ex);
                Debug.WriteLine($"[GeminiLive] Connect failed: {chain}");
                OnError?.Invoke($"Connect failed: {chain}");

                // Diagnostic: test plain HTTPS to the same host to distinguish network vs WS block
                try
                {
                    using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                    var resp = await http.GetAsync("https://generativelanguage.googleapis.com/");
                    OnError?.Invoke($"[Diag] HTTPS OK ({(int)resp.StatusCode}) — WS is specifically blocked");
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
            if (!_setupComplete.Task.IsCompleted) return; // Don't send before server ACKs setup
            _lastAudioTime = DateTime.UtcNow;

            // Build realtimeInput message — Gemini Live API expects mediaChunks array
            string base64 = Convert.ToBase64String(pcmData, 0, length);
            var msg = new
            {
                realtimeInput = new
                {
                    mediaChunks = new[]
                    {
                        new { mimeType = "audio/pcm;rate=16000", data = base64 }
                    }
                }
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
                Debug.WriteLine($"[GeminiLive] Send error: {ex.Message}");
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[32 * 1024]; // 32KB receive buffer

            try
            {
                while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
                {
                    var sb = new StringBuilder();
                    WebSocketReceiveResult result;

                    // Accumulate a full message (may span multiple frames)
                    do
                    {
                        result = await _ws.ReceiveAsync(buffer, ct);
                        if (result.MessageType == WebSocketMessageType.Close)
{
    string reason = _ws?.CloseStatusDescription ?? "Unknown";
    OnError?.Invoke($"Server closed: {reason}");
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
                Debug.WriteLine($"[GeminiLive] Receive error: {ex.Message}");
                OnError?.Invoke($"Stream error: {ex.Message}");
            }
            finally
            {
                OnDisconnected?.Invoke();
                Debug.WriteLine("[GeminiLive] Receive loop ended");
            }
        }

        private void ParseMessage(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;



                // Surface server errors
                if (root.TryGetProperty("error", out var errorEl))
                {
                    string msg = errorEl.TryGetProperty("message", out var m) ? m.GetString() ?? json : json;
                    Debug.WriteLine($"[GeminiLive] Server error: {msg}");
                    OnError?.Invoke($"Gemini Live error: {msg}");
                    return;
                }

                // Check for inputTranscription (what the user said — committed by server VAD)
                if (root.TryGetProperty("serverContent", out var content))
                {
                    // Discard model audio responses — we only want inputTranscription events
                    if (content.TryGetProperty("modelTurn", out _)) return;

                    if (content.TryGetProperty("inputTranscription", out var transcription) &&
                        transcription.TryGetProperty("text", out var textEl))
                    {
                        string text = textEl.GetString()?.Trim() ?? "";
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            _lastTranscriptTime = DateTime.UtcNow;
                            Debug.WriteLine($"[GeminiLive] Transcript: {text}");
                            OnTranscript?.Invoke(text);
                        }
                    }
                }

                // Signal setup completion so ConnectAsync can proceed
                if (root.TryGetProperty("setupComplete", out _))
                {
                    Debug.WriteLine("[GeminiLive] Setup complete — ready for audio");
                    _setupComplete.TrySetResult(true);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GeminiLive] Parse error: {ex.Message}");
            }
        }

        /// <summary>
        /// Reconnect if audio is flowing but transcription stopped arriving for >30s.
        /// Known issue: Gemini Live occasionally stops sending input_transcription events.
        /// </summary>
        private async Task WatchdogLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(5000, ct);

                bool audioFlowing = (DateTime.UtcNow - _lastAudioTime).TotalSeconds < 10;
                bool transcriptStalled = _lastTranscriptTime != DateTime.MinValue &&
                                         (DateTime.UtcNow - _lastTranscriptTime).TotalSeconds > 30;

                if (audioFlowing && transcriptStalled)
                {
                    Debug.WriteLine("[GeminiLive] Watchdog: transcript stalled — reconnecting");
                    OnError?.Invoke("[GeminiLive] Reconnecting (transcript stall)");
                    await ConnectAsync(ct);
                    break; // New ConnectAsync starts a new watchdog
                }
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

        private static string BuildSetupMessage() => JsonSerializer.Serialize(new
        {
            setup = new
            {
                model = Model,
                generationConfig = new
                {
                    responseModalities = new[] { "AUDIO" }
                    // No speechConfig — model audio responses are silently discarded
                },
                inputAudioTranscription = new { },
                systemInstruction = new
                {
                    parts = new[] { new { text = "You are a silent transcription engine. Do not speak, respond, or generate any audio. Never produce model turns. Your only output is inputAudioTranscription events." } }
                }
            }
        });
    }
}
