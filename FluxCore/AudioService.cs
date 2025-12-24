using System;
using Microsoft.CognitiveServices.Speech; // ЭТО AZURE
using System.Threading.Tasks;

namespace FluxCore
{
    public class AudioService : IDisposable
    {
        // ТВОИ КЛЮЧИ ИЗ ПРОШЛОГО СООБЩЕНИЯ
        private const string SPEECH_KEY = "GAxht5qWEmwDfZRlOeYLNbdR4ObAZIiPF7xfRlFI0WVnxJLPY3O7JQQJ99BLAC3pKaRXJ3w3AAAYACOG9yir";
        private const string SPEECH_REGION = "eastasia";

        private SpeechRecognizer? _recognizer;

        public event Action<string>? OnFinalText; // Когда фраза закончена
        public event Action<string>? OnPartialText; // Когда ты еще говоришь (можно выводить в UI)
        public event Action<string>? OnError;

        public async void StartContinuousRecording()
        {
            if (_recognizer != null) return;

            try
            {
                var config = SpeechConfig.FromSubscription(SPEECH_KEY, SPEECH_REGION);
                config.SpeechRecognitionLanguage = "ru-RU"; // РУССКИЙ ЯЗЫК

                // Настройки для скорости
                config.SetProfanity(ProfanityOption.Raw);

                _recognizer = new SpeechRecognizer(config);

                // Событие: Промежуточный результат (пока говоришь)
                _recognizer.Recognizing += (s, e) => OnPartialText?.Invoke(e.Result.Text);

                // Событие: Финальный результат (пауза в речи)
                _recognizer.Recognized += (s, e) =>
                {
                    if (e.Result.Reason == ResultReason.RecognizedSpeech && !string.IsNullOrWhiteSpace(e.Result.Text))
                    {
                        OnFinalText?.Invoke(e.Result.Text);
                    }
                };

                _recognizer.Canceled += (s, e) => OnError?.Invoke($"Azure Error: {e.ErrorDetails}");

                await _recognizer.StartContinuousRecognitionAsync();
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Mic Init Error: {ex.Message}");
            }
        }

        public async void Stop()
        {
            if (_recognizer != null)
            {
                await _recognizer.StopContinuousRecognitionAsync();
            }
        }

        public void Dispose()
        {
            _recognizer?.Dispose();
        }
    }
}