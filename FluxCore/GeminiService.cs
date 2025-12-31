using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FluxCore
{
    // --- Вспомогательные классы для API ---
    public class GeminiContent
    {
        public string role { get; set; }
        public List<GeminiPart> parts { get; set; } = new List<GeminiPart>();
    }

    public class GeminiPart
    {
        public string text { get; set; }
    }

    public class GeminiService
    {
        private readonly string _apiKey;
        private readonly HttpClient _httpClient = new HttpClient();

        // Используем самую новую модель для лучшего контекста
        // Если будет ошибка 404, верни "gemini-1.5-flash"
        private const string Endpoint = "https://generativelanguage.googleapis.com/v1/models/gemini-2.5-flash:generateContent";

        public GeminiService(string apiKey)
        {
            _apiKey = apiKey;
            _httpClient.Timeout = TimeSpan.FromSeconds(60);
        }

        // ==========================================
        // 1. НОВЫЙ МЕТОД (С ПАМЯТЬЮ И ИСТОРИЕЙ)
        // ==========================================
        public async Task<string> ChatWithHistory(List<ChatMessage> history, string userNewInput, string screenContext, string activeApp, string memories)
        {
            // 1. Формируем "Системный Промпт" (Контекст момента)
            var systemInstruction = $@"
[[SYSTEM OVERRIDE: FLUX OS]]
ROLE: You are FLUX, an intelligent OS assistant.
GOAL: Help the user, remember previous messages, and analyze the screen.

[[CURRENT CONTEXT]]
- APP: {activeApp}
- TIME: {DateTime.Now:HH:mm}
- MEMORY: {memories}

[[VISUAL DATA (SCREEN)]]
{screenContext}

INSTRUCTIONS:
1. Use the conversation history to understand context.
2. Use 'VISUAL DATA' to see what the user sees.
3. Be concise and direct.
";
            var contents = new List<GeminiContent>();

            // 2. Конвертируем историю чата в формат Gemini
            if (history != null)
            {
                foreach (var msg in history)
                {
                    if (string.IsNullOrWhiteSpace(msg.Text)) continue;
                    contents.Add(new GeminiContent
                    {
                        role = msg.IsUser ? "user" : "model",
                        parts = new List<GeminiPart> { new GeminiPart { text = msg.Text } }
                    });
                }
            }

            // 3. Добавляем ТЕКУЩИЙ запрос пользователя + Системный контекст (невидимо для чата)
            // Мы вшиваем контекст прямо в последнее сообщение пользователя
            string finalUserPrompt = $"{systemInstruction}\n\n[[USER REQUEST]]:\n{userNewInput}";

            contents.Add(new GeminiContent
            {
                role = "user",
                parts = new List<GeminiPart> { new GeminiPart { text = finalUserPrompt } }
            });

            return await SendPayload(contents);
        }

        // ==========================================
        // 2. СТАРЫЕ МЕТОДЫ (ДЛЯ СОВМЕСТИМОСТИ)
        // ==========================================

        // Тот самый метод, на который ругается компилятор
        public async Task<string> AskSimple(string prompt)
        {
            // Просто отправляем один запрос без истории
            return await ChatWithHistory(new List<ChatMessage>(), prompt, "No screen data", "Unknown", "No memory");
        }

        // Старый метод AskContextAware (на случай, если он вызывается из старого MainWindow)
        public async Task<string> AskContextAware(string userVoice, string appMeta, string mouseFocus, string screenText, List<string> memories, string clipboard)
        {
            string fullScreen = $"Mouse: {mouseFocus}\nScreen: {screenText}\nClipboard: {clipboard}";
            string memoryBlock = (memories != null) ? string.Join("\n", memories) : "";

            // Перенаправляем на новый умный метод (но без истории чата, т.к. старый вызов её не передает)
            return await ChatWithHistory(new List<ChatMessage>(), userVoice, fullScreen, appMeta, memoryBlock);
        }

        // ==========================================
        // 3. ОТПРАВКА (ВНУТРЕННЯЯ ЛОГИКА)
        // ==========================================
        private async Task<string> SendPayload(object payloadObj)
        {
            var payload = new
            {
                contents = payloadObj,
                generationConfig = new { temperature = 1, maxOutputTokens = 2048 }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                var response = await _httpClient.PostAsync($"{Endpoint}?key={_apiKey}", content);
                var responseStr = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                    return $"⚠️ Error {response.StatusCode}: {responseStr}";

                using var doc = JsonDocument.Parse(responseStr);
                if (doc.RootElement.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
                {
                    var text = candidates[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
                    return text?.Trim() ?? "...";
                }
                return "No response.";
            }
            catch (Exception ex)
            {
                return $"Exception: {ex.Message}";
            }
        }
    }
}