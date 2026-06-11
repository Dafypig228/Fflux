using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace FluxCore
{
    /// <summary>
    /// Audio Service — real-time STT via ElevenLabs Scribe v2 Realtime WebSocket.
    ///
    /// Streams 16kHz PCM chunks from NAudio to ElevenLabs server-side VAD → ~150ms latency.
    /// </summary>
    public class AudioService : IDisposable
    {
        private readonly ElevenLabsLiveService _live;
        private WaveInEvent? _waveIn;
        private bool _isRecording;
        private bool _speechActive;
        private int _consecutiveFailures;

        private const int SAMPLE_RATE = 16000;
        private const int BITS_PER_SAMPLE = 16;
        private const int CHANNELS = 1;
        private const float VOLUME_THRESHOLD = 0.02f;

        public event Action<string>? OnFinalText;
        public event Action<string>? OnPartialText;
        public event Action<string>? OnError;
        public event Action? OnConnected;

        public AudioService(string apiKey, string language = "ru")
        {
            _live = new ElevenLabsLiveService(apiKey, language);
            _live.OnTranscript += text => OnFinalText?.Invoke(text);
            _live.OnError += err => OnError?.Invoke(err);
            _live.OnConnected += () =>
            {
                _consecutiveFailures = 0;
                Debug.WriteLine("[AudioService] Live API connected");
                OnConnected?.Invoke();
            };
            _live.OnDisconnected += () =>
            {
                Debug.WriteLine("[AudioService] Live API disconnected");
                if (_isRecording)
                {
                    _consecutiveFailures++;
                    if (_consecutiveFailures > 3)
                    {
                        OnError?.Invoke("ElevenLabs STT: connection rejected repeatedly. Check API key in Settings.");
                        return;
                    }
                    // Delay before retry to avoid flooding the server
                    _ = Task.Delay(3000).ContinueWith(t =>
                    {
                        if (_isRecording) _ = _live.ConnectAsync();
                    });
                }
            };
        }


        public async Task StartContinuousRecording(string language = "auto")
        {
            if (_isRecording) return;

            try
            {
                await Stop();

                // Connect to ElevenLabs Scribe Realtime
                await _live.ConnectAsync();

                // Start NAudio mic — 16kHz/16-bit/mono matches ElevenLabs format exactly
                _waveIn = new WaveInEvent
                {
                    WaveFormat = new WaveFormat(SAMPLE_RATE, BITS_PER_SAMPLE, CHANNELS),
                    BufferMilliseconds = 50
                };

                _waveIn.DataAvailable += OnDataAvailable;
                _waveIn.RecordingStopped += (s, e) =>
                {
                    if (e.Exception != null)
                        OnError?.Invoke($"Recording error: {e.Exception.Message}");
                };

                _waveIn.StartRecording();
                _isRecording = true;

                Debug.WriteLine("[AudioService] Streaming mic started → ElevenLabs Scribe");
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Mic Init Error: {ex.Message}");
                Debug.WriteLine($"[AudioService] Init error: {ex}");
            }
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded == 0) return;

            // Check peak volume to show "listening" indicator
            float maxVolume = 0;
            for (int i = 0; i < e.BytesRecorded - 1; i += 2)
            {
                short sample = BitConverter.ToInt16(e.Buffer, i);
                float vol = Math.Abs(sample / 32768f);
                if (vol > maxVolume) maxVolume = vol;
            }

            if (maxVolume > VOLUME_THRESHOLD)
            {
                if (!_speechActive)
                {
                    _speechActive = true;
                    OnPartialText?.Invoke("🎤 listening...");
                }
            }
            else
            {
                _speechActive = false;
            }

            // Send raw PCM chunk to ElevenLabs (no WAV header, no buffering)
            // Fire-and-forget is safe here: chunks are small (~1600 bytes at 50ms) and ordered
            _ = _live.SendAudioChunkAsync(e.Buffer, e.BytesRecorded);
        }

        public async Task Stop()
        {
            _isRecording = false;
            _speechActive = false;

            try { _waveIn?.StopRecording(); } catch { }
            _waveIn?.Dispose();
            _waveIn = null;

            await _live.DisconnectAsync();
        }

        public void Dispose()
        {
            Stop().GetAwaiter().GetResult();
            _live.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
