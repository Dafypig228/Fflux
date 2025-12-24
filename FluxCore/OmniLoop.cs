using System;
using System.Threading.Tasks;

namespace FluxCore
{
    public class OmniLoop
    {
        private readonly SensoryCortex _senses;
        private readonly NeuralLink _brain;

        // Кэшированные данные (Последний известный слепок реальности)
        public string LastImageBase64 { get; private set; } = "";
        public string LastUiTree { get; private set; } = "";

        public OmniLoop(SensoryCortex senses, NeuralLink brain)
        {
            _senses = senses;
            _brain = brain;
        }

        // Вызывается таймером (Heartbeat)
        public void Pulse()
        {
            // 1. Проверяем зрение (Delta check внутри)
            string img = _senses.CaptureScreenBase64(out bool changed);

            if (changed && !string.IsNullOrEmpty(img))
            {
                LastImageBase64 = img;
                // Если картинка изменилась, сканируем и UI, чтобы данные были синхронны
                LastUiTree = _senses.ScanActiveWindowDeep();

                // ТУТ МОЖНО ВСТАВИТЬ "Anomaly Detection"
                // Если в LastUiTree есть слово "Error", можно вызвать _brain.ProcessSignal автоматически
            }
        }

        // Прямой запрос от пользователя
        public async Task<string> UserQuery(string text)
        {
            // Если экран не менялся, используем кэш (LastImageBase64) - Экономия и Скорость!
            // Если кэша нет, делаем принудительный снимок
            if (string.IsNullOrEmpty(LastImageBase64))
            {
                LastImageBase64 = _senses.CaptureScreenBase64(out _);
                LastUiTree = _senses.ScanActiveWindowDeep();
            }

            // Обновляем UI данные прямо перед ответом для точности мыши
            string freshUi = _senses.ScanActiveWindowDeep();

            return await _brain.ProcessSignal(text, freshUi, LastImageBase64);
        }
    }
}