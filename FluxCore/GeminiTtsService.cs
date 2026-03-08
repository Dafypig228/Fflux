using System;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace FluxCore
{
    /// <summary>
    /// Gemini Live API TTS client.
    /// Sends text to a Gemini Live session configured for AUDIO output and streams
    /// the resulting 24kHz PCM audio to the speaker via NAudio.
    ///
    /// Uses the same Gemini API key — no new credentials required.
    /// Latency to first audio: ~200–400ms.
    /// </summary>
    public class GeminiTtsService : IAsyncDisposable
    {
        private const string WsEndpoint =
            "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";
        private const string Model = "models/gemini-2.5-flash-native-audio-latest";
        private const int OUTPUT_SAMPLE_RATE = 24000; // Gemini Live outputs 24kHz PCM

        private readonly string _apiKey;
        private readonly string _voiceName;
        private ClientWebSocket? _ws;
        private CancellationTokenSource _cts = new();
        private Task? _receiveLoop;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private TaskCompletionSource<bool> _setupComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Audio playback
        private WaveOutEvent? _waveOut;
        private BufferedWaveProvider? _audioBuffer;
        private readonly object _playbackLock = new();
        private bool _isPlaying;

        public event Action<string>? OnError;

        public GeminiTtsService(string apiKey, string voiceName = "Kore")
        {
            _apiKey = apiKey;
            _voiceName = voiceName;
        }

        /// <summary>Connect to Gemini Live API in audio output mode.</summary>
        public async Task ConnectAsync(CancellationToken ct = default)
        {
            await DisconnectAsync();

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ws = new ClientWebSocket();
            _setupComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var uri = new Uri($"{WsEndpoint}?key={_apiKey}");

            // Diagnostic: log endpoint (key masked)
            string maskedUri = $"{WsEndpoint}?key=...{_apiKey[^4..]}";
            Debug.WriteLine($"[GeminiTts] Connecting to: {maskedUri}");
            OnError?.Invoke($"[Diag] TTS connecting to: {maskedUri}");

            try
            {
                // 10-second connect timeout — prevents hanging if server accepts TCP but ignores upgrade
                using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, connectTimeout.Token);

                await _ws.ConnectAsync(uri, linked.Token);
                await SendRawAsync(BuildSetupMessage(), _cts.Token);
                _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);

                // Wait for server's setupComplete before returning
                using var setupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await _setupComplete.Task.WaitAsync(setupTimeout.Token); }
                catch (OperationCanceledException) { Debug.WriteLine("[GeminiTts] setupComplete timeout — proceeding anyway"); }

                Debug.WriteLine("[GeminiTts] Connected and ready");
            }
            catch (Exception ex)
            {
                string chain = DescribeException(ex);
                Debug.WriteLine($"[GeminiTts] Connect failed: {chain}");
                OnError?.Invoke($"TTS connect failed: {chain}");
            }
        }

        /// <summary>
        /// Speak the given text. Interrupts any current playback.
        /// </summary>
        public async Task SpeakAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            // Reconnect if needed
            if (_ws?.State != WebSocketState.Open)
                await ConnectAsync();

            // Ensure setup is acknowledged before sending content
            if (!_setupComplete.Task.IsCompleted)
            {
                using var setupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await _setupComplete.Task.WaitAsync(setupTimeout.Token); }
                catch (OperationCanceledException) { }
            }

            StopPlayback();

            // Initialize NAudio buffer for 24kHz mono PCM
            lock (_playbackLock)
            {
                _audioBuffer = new BufferedWaveProvider(new WaveFormat(OUTPUT_SAMPLE_RATE, 16, 1))
                {
                    BufferDuration = TimeSpan.FromSeconds(30),
                    DiscardOnBufferOverflow = true
                };
                _waveOut = new WaveOutEvent() { DesiredLatency = 100 };
                _waveOut.Init(_audioBuffer);
                _waveOut.Play();
                _isPlaying = true;
            }

            // Send text to model
            var msg = new
            {
                clientContent = new
                {
                    turns = new[]
                    {
                        new
                        {
                            role = "user",
                            parts = new[] { new { text = text } }
                        }
                    },
                    turnComplete = true
                }
            };

            await SendRawAsync(JsonSerializer.Serialize(msg), _cts.Token);
        }

        /// <summary>Stop current TTS playback immediately.</summary>
        public void StopPlayback()
        {
            lock (_playbackLock)
            {
                _isPlaying = false;
                try { _waveOut?.Stop(); } catch { }
                _waveOut?.Dispose();
                _waveOut = null;
                _audioBuffer = null;
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[64 * 1024];
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
                            string reason = _ws?.CloseStatusDescription ?? "Unknown";
                            OnError?.Invoke($"Server closed: {reason}");
                            return;
                        }

                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    }
                    while (!result.EndOfMessage);

                    ProcessAudioMessage(sb.ToString());
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GeminiTts] Receive error: {ex.Message}");
            }
        }

        private void ProcessAudioMessage(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Surface server errors
                if (root.TryGetProperty("error", out var errorEl))
                {
                    string msg = errorEl.TryGetProperty("message", out var m) ? m.GetString() ?? json : json;
                    Debug.WriteLine($"[GeminiTts] Server error: {msg}");
                    OnError?.Invoke($"TTS error: {msg}");
                    return;
                }

                // Signal setup complete so SpeakAsync can proceed
                if (root.TryGetProperty("setupComplete", out _))
                {
                    Debug.WriteLine("[GeminiTts] Setup complete");
                    _setupComplete.TrySetResult(true);
                    return;
                }

                if (!root.TryGetProperty("serverContent", out var content)) return;
                if (!content.TryGetProperty("modelTurn", out var turn)) return;
                if (!turn.TryGetProperty("parts", out var parts)) return;

                foreach (var part in parts.EnumerateArray())
                {
                    if (!part.TryGetProperty("inlineData", out var inlineData)) continue;
                    if (!inlineData.TryGetProperty("mimeType", out var mimeEl)) continue;
                    string mime = mimeEl.GetString() ?? "";
                    if (!mime.StartsWith("audio/pcm")) continue;
                    if (!inlineData.TryGetProperty("data", out var dataEl)) continue;

                    string base64 = dataEl.GetString() ?? "";
                    if (string.IsNullOrEmpty(base64)) continue;

                    byte[] pcm = Convert.FromBase64String(base64);

                    lock (_playbackLock)
                    {
                        if (_isPlaying && _audioBuffer != null)
                            _audioBuffer.AddSamples(pcm, 0, pcm.Length);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GeminiTts] Parse error: {ex.Message}");
            }
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
                Debug.WriteLine($"[GeminiTts] Send error: {ex.Message}");
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public async Task DisconnectAsync()
        {
            StopPlayback();
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

        private string BuildSetupMessage() => JsonSerializer.Serialize(new
        {
            setup = new
            {
                model = Model,
                generationConfig = new
                {
                    responseModalities = new[] { "AUDIO" },
                    speechConfig = new
                    {
                        voiceConfig = new
                        {
                            prebuiltVoiceConfig = new { voiceName = _voiceName }
                        }
                    }
                },
                systemInstruction = new
                {
                    parts = new[] { new { text = "You are a text-to-speech reader. Read the given text exactly as written. Do not add commentary, greetings, or extra words." } }
                }
            }
        });
    }
}
