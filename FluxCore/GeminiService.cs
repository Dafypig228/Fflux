using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FluxCore
{
    public class GeminiService
    {
        private readonly string _apiKey;
        private readonly HttpClient _httpClient = new HttpClient();

        // Используем модель 1.5 Flash (быстрая и умная)
        private const string Endpoint = "https://generativelanguage.googleapis.com/v1/models/gemini-2.5-flash:generateContent";

        public GeminiService(string apiKey)
        {
            _apiKey = apiKey;
            _httpClient.Timeout = TimeSpan.FromSeconds(60);
        }

        // --- 1. УМНЫЙ МЕТОД (С КОНТЕКСТОМ) ---
        public async Task<string> AskContextAware(string userVoice, string appMeta, string mouseFocus, string screenText)
        {
            // Формируем "Божественный промпт" для глубокого анализа
            var fullPrompt = $@"
SYSTEM: FLUXCORE OS INTEGRATION.
MODE: DEEP ANALYSIS.

--- INPUT DATA STREAM ---
[ACTIVE PROCESS]
{appMeta}

[VISUAL OCR DATA]
{screenText}

[UI AUTOMATION TREE]
{mouseFocus}

--- USER REQUEST ---
'{userVoice}'

--- INSTRUCTIONS ---
Ты — технический ассистент Flux. 
1. Проанализируй иерархию элементов (UI TREE).
2. Используй данные OCR, чтобы понять контекст экрана.
3. Отвечай развернуто и технически грамотно.
4. Если пользователь спрашивает 'Что это?', используй данные о элементе под курсором.";

            return await SendRequest(fullPrompt);
        }

        // --- 2. ПРОСТОЙ МЕТОД (FIX ДЛЯ ОШИБКИ) ---
        // Этот метод нужен, чтобы твой старый код не ломался
        public async Task<string> AskSimple(string prompt)
        {
            return await SendRequest(prompt);
        }

        // --- ВНУТРЕННЯЯ ОТПРАВКА ---
        private async Task<string> SendRequest(string promptText)
        {
            var payload = new
            {
                contents = new[]
                {
                    new { parts = new[] { new { text = promptText } } }
                },
                generationConfig = new
                {
                    temperature = 0.4,
                    maxOutputTokens = 100000
                }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                var response = await _httpClient.PostAsync($"{Endpoint}?key={_apiKey}", content);
                var responseStr = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode) return $"⚠️ API Error: {response.StatusCode}";

                using var doc = JsonDocument.Parse(responseStr);
                if (doc.RootElement.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
                {
                    var text = candidates[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
                    return text?.Trim() ?? "...";
                }
                return "Нет данных.";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }
    }
}