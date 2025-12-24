using System;
using System.Threading.Tasks;

namespace FluxCore
{
    public class PipelineService
    {
        private readonly GeminiService _gemini;
        private readonly ContextService _context;
        private readonly ScreenService _screen;

        // "Короткая память" - последнее состояние мира
        public string CurrentWorldState { get; private set; } = "Система запущена. Ожидание данных.";

        public PipelineService(GeminiService gemini, ContextService context, ScreenService screen)
        {
            _gemini = gemini;
            _context = context;
            _screen = screen;
        }

        // Этот метод вызывается каждые 2-3 секунды или по триггеру (клик)
        public async Task UpdateWorldState()
        {
            // 1. Слой Метаданных (Delta Check)
            string layer1 = _context.GetLayer1_Metadata(out bool appChanged);

            // 2. Слой Текста + Визуал (Delta Check)
            var layer2Result = await _screen.GetLayer2_OCR_WithDelta();

            // Если ни приложение, ни картинка не изменились - СПИМ.
            if (!appChanged && !layer2Result.VisualChanged)
            {
                // Мы не дергаем ИИ, экономим ресурсы, используем старый WorldState
                return;
            }

            string layer2 = layer2Result.Text;
            if (layer2.Length > 1500) layer2 = layer2.Substring(0, 1500) + "...";

            // 3. Формируем запрос "Абстракция"
            // Мы просим Gemini выполнить роль той самой "Local Model", которая сжимает данные в суть.
            string abstractionPrompt = $@"
РОЛЬ: Ты - модуль абстракции (Data Abstraction Layer).
Твоя задача превратить сырые данные с сенсоров в краткую сводку ситуации (World State).

ВХОДНЫЕ ДАННЫЕ:
[МЕТАДАННЫЕ]: {layer1}
[ВИДИМЫЙ ТЕКСТ]: {layer2}

ЗАДАЧА:
Опиши одной строкой, что происходит на экране.
Пример: ""Пользователь в Chrome читает статью про React Hooks.""
Пример: ""Пользователь в Visual Studio редактирует класс Program.cs.""
ВЫВОД (Только строка описания):";

            // Получаем новую "Сводку мира"
            // Используем Gemini как быстрый процессор данных
            CurrentWorldState = await _gemini.AskSimple(abstractionPrompt);
        }

        public async Task<string> ExecuteUserQuery(string userVoice)
        {
            // 4. Слой UI (Актуальный в момент вопроса)
            string layer3 = _context.GetLayer3_UIHierarchy();

            // ФИНАЛЬНЫЙ ЗАПРОС К ИИ
            // У него есть "Сводка мира" (из памяти) + "Куда я тычу" (Layer 3) + Вопрос
            string prompt = $@"
STATE: {CurrentWorldState}
FOCUS: {layer3}
USER: ""{userVoice}""

Исходя из состояния (STATE) и того, на что указывает пользователь (FOCUS), ответь на вопрос.";

            return await _gemini.AskSimple(prompt);
        }
    }
}