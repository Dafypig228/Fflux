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
        public GeminiInlineData inline_data { get; set; }
    }

    public class GeminiInlineData
    {
        public string mime_type { get; set; }
        public string data { get; set; }
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
            // 1. Формируем "Системный Промпт" (Контекст момента)
            // Если screenContext содержит Base64, мы не пихаем его в текст, а пишем заглушку.
            string visualTextLog = screenContext.StartsWith("BASE64:") ? "[IMAGE ATTACHED]" : screenContext;

            var systemInstruction = $@"
FLUX: Windows AI. Execute with [[COMMAND:arg]]

[[KEYS:WIN+D]] [[WINDOW:close]] [[OPEN_APP:appname]] [[TYPE:txt]] [[CLICK:text OR x,y]] [[LOG:text]]

RULES:
1. DO NOT use natural language like ""Typing: hello"". ONLY use [[TYPE:hello]].
2. ALWAYS use [[LOG: explanation]] to tell the user what you are doing.
3. Messengers: SEARCH first! Don't assume open chat is correct.

{activeApp}|{memories}|{visualTextLog}
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

            // === VISION UPDATE: Если есть картика, добавляем её ===
            var userParts = new List<GeminiPart> { new GeminiPart { text = finalUserPrompt } };

            if (!string.IsNullOrEmpty(screenContext) && screenContext.StartsWith("BASE64:"))
            {
                string base64 = screenContext.Substring(7);
                userParts.Add(new GeminiPart 
                { 
                    inline_data = new GeminiInlineData 
                    { 
                        mime_type = "image/jpeg", 
                        data = base64 
                    } 
                });
            }
            else
            {
                // Если картинки нет, кидаем текстовый контекст по старинке (fallback)
                // Но мы договорились, что screenContext теперь это картинка.
                // Если пришел текст, добавим его просто текстом.
                if (!string.IsNullOrEmpty(screenContext) && !screenContext.StartsWith("BASE64:"))
                {
                    userParts.Add(new GeminiPart { text = $"[[SCREEN TEXT LOG]]:\n{screenContext}" });
                }
            }

            contents.Add(new GeminiContent
            {
                role = "user",
                parts = userParts
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
        // ==========================================
        // 4. EMBEDDINGS (ДЛЯ ПАМЯТИ)
        // ==========================================
        public async Task<float[]> GetEmbeddingAsync(string text)
        {
            var payload = new
            {
                model = "models/text-embedding-004",
                content = new { parts = new[] { new { text = text } } }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                var response = await _httpClient.PostAsync($"https://generativelanguage.googleapis.com/v1beta/models/text-embedding-004:embedContent?key={_apiKey}", content);
                var responseStr = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode) return new float[0];

                using var doc = JsonDocument.Parse(responseStr);
                if (doc.RootElement.TryGetProperty("embedding", out var embeddingEl) &&
                    embeddingEl.TryGetProperty("values", out var valuesEl))
                {
                    // Конвертируем JSON array в float[]
                    var list = new List<float>();
                    foreach (var v in valuesEl.EnumerateArray())
                    {
                        list.Add(v.GetSingle());
                    }
                    return list.ToArray();
                }
                return new float[0];
            }
            catch
            {
                return new float[0];
            }
        }

        // ==========================================
        // 5. SIMPLE GENERATION (ДЛЯ СУММАРИЗАЦИИ)
        // ==========================================
        public async Task<string> GenerateText(string prompt)
        {
            var contents = new List<GeminiContent>
            {
                new GeminiContent
                {
                    role = "user",
                    parts = new List<GeminiPart> { new GeminiPart { text = prompt } }
                }
            };
            return await SendPayload(contents);
        }
    }
}