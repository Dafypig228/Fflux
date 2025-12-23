using System;
using Microsoft.CognitiveServices.Speech;

namespace FluxCore
{
    // Этот класс занимается ТОЛЬКО звуком.
    // Если ты сломаешь интерфейс, запись звука не пострадает.
    public class AudioService : IDisposable
    {
        // 👇 ТВОИ ДАННЫЕ
        private const string SPEECH_KEY = "GAxht5qWEmwDfZRlOeYLNbdR4ObAZIiPF7xfRlFI0WVnxJLPY3O7JQQJ99BLAC3pKaRXJ3w3AAAYACOG9yir";
        private const string SPEECH_REGION = "eastasia";

        private SpeechRecognizer? _recognizer;

        // События, на которые может подписаться окно
        public event Action<string>? OnPartialText; // Текст "на лету"
        public event Action<string>? OnFinalText;   // Готовая фраза
        public event Action<string>? OnError;

        public async void StartContinuousRecording()
        {
            if (_recognizer != null) return; // Уже работает

            try
            {
                var config = SpeechConfig.FromSubscription(SPEECH_KEY, SPEECH_REGION);
                config.SpeechRecognitionLanguage = "ru-RU";
                config.SetProfanity(ProfanityOption.Raw); // Без цензуры

                // Настройки для высокой точности
                config.SetServiceProperty("speechSegmentationSilenceTimeoutMs", "1500", ServicePropertyChannel.UriQueryParameter);

                _recognizer = new SpeechRecognizer(config);

                _recognizer.Recognizing += (s, e) => OnPartialText?.Invoke(e.Result.Text);

                _recognizer.Recognized += (s, e) =>
                {
                    if (e.Result.Reason == ResultReason.RecognizedSpeech && !string.IsNullOrWhiteSpace(e.Result.Text))
                    {
                        OnFinalText?.Invoke(e.Result.Text);
                    }
                };

                _recognizer.Canceled += (s, e) => OnError?.Invoke($"Ошибка: {e.ErrorDetails}");

                await _recognizer.StartContinuousRecognitionAsync();
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Критическая ошибка аудио: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _recognizer?.StopContinuousRecognitionAsync().Wait();
            _recognizer?.Dispose();
        }
    }
}