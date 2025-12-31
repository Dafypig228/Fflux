using System;
using Microsoft.CognitiveServices.Speech;
using System.Diagnostics;
using System.Threading.Tasks;

namespace FluxCore
{
    public class AudioService : IDisposable
    {
        // Твой ключ и регион (оставляем как есть)
        private const string SPEECH_KEY = "GAxht5qWEmwDfZRlOeYLNbdR4ObAZIiPF7xfRlFI0WVnxJLPY3O7JQQJ99BLAC3pKaRXJ3w3AAAYACOG9yir";
        private const string SPEECH_REGION = "eastasia";

        private SpeechRecognizer? _recognizer;

        public event Action<string>? OnFinalText;
        public event Action<string>? OnPartialText;
        public event Action<string>? OnError;

        // ДОБАВИЛ ПАРАМЕТР: language (по умолчанию английский)
        public async void StartContinuousRecording(string language = "ru-RU")
        {
            if (_recognizer != null)
            {
                await Stop();
            }

            try
            {
                var config = SpeechConfig.FromSubscription(SPEECH_KEY, SPEECH_REGION);

                // ВАЖНО: Присваиваем язык из аргумента
                config.SpeechRecognitionLanguage = language;

                config.SetProfanity(ProfanityOption.Raw);

                _recognizer = new SpeechRecognizer(config);

                _recognizer.Recognizing += (s, e) => OnPartialText?.Invoke(e.Result.Text);

                _recognizer.Recognized += (s, e) =>
                {
                    if (e.Result.Reason == ResultReason.RecognizedSpeech && !string.IsNullOrWhiteSpace(e.Result.Text))
                    {
                        OnFinalText?.Invoke(e.Result.Text);
                    }
                };

                _recognizer.Canceled += (s, e) =>
                {
                    if (e.Reason != CancellationReason.EndOfStream)
                    {
                        OnError?.Invoke($"Azure Error: {e.ErrorDetails}");
                    }
                };

                await _recognizer.StartContinuousRecognitionAsync();
                Debug.WriteLine($"[AudioService] Azure Mic Started [{language}]");
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Mic Init Error: {ex.Message}");
            }
        }

        public async Task Stop()
        {
            if (_recognizer == null) return;
            try { await _recognizer.StopContinuousRecognitionAsync(); }
            catch { }
            finally { _recognizer?.Dispose(); _recognizer = null; }
        }

        public void Dispose()
        {
            _recognizer?.Dispose();
            _recognizer = null;
        }
    }
}