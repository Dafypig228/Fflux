using System.Threading.Tasks;

namespace FluxCore
{
    // 1. NEURAL LINK (Связь с API)
    public class NeuralLink
    {
        public GeminiService Brain { get; private set; }

        public NeuralLink(string apiKey)
        {
            // Инициализируем твой умный сервис
            Brain = new GeminiService(apiKey);
        }
    }

    // 2. SENSORY CORTEX (Органы чувств)
    // Объединяет экран и контекст
    public class SensoryCortex
    {
        private ContextService _context = new ContextService();
        private ScreenService _screen = new ScreenService();

        // Получаем имя окна
        public string GetActiveWindow() => _context.GetLayer1_Metadata(out _);

        // Получаем элемент под мышкой
        public string GetMouseElement() => _context.GetLayer3_UIHierarchy();

        // Получаем текст с экрана (OCR)
        public async Task<string> GetVisualContext()
        {
            var ocr = await _screen.GetLayer2_OCR_WithDelta();
            return ocr.VisualChanged ? ocr.Text : "[Экран не изменился]";
        }
    }

    // 3. OMNI LOOP (Главный цикл)
    // Собирает данные из Cortex и отправляет в NeuralLink
    public class OmniLoop
    {
        private readonly SensoryCortex _cortex;
        private readonly NeuralLink _link;

        public OmniLoop(SensoryCortex cortex, NeuralLink link)
        {
            _cortex = cortex;
            _link = link;
        }

        public async Task<string> UserQuery(string userText)
        {
            // 1. Сбор данных (Смотрим, что на экране)
            string window = _cortex.GetActiveWindow();
            string mouse = _cortex.GetMouseElement();
            string screen = await _cortex.GetVisualContext();

            // 2. Мыслительный процесс (Отправка в Gemini)
            // Вызываем тот самый AskContextAware, который ты хотел
            return await _link.Brain.AskContextAware(userText, window, mouse, screen);
        }

        public void Pulse() { /* Метод для фоновых задач, если нужно */ }
    }
}