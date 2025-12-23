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
        // 🔥 ИЗМЕНЕНО: Используем стабильный эндпоинт v1 вместо v1beta
        // Это убирает ошибку 404 для Tier 1 аккаунтов
        private const string Endpoint = "https://generativelanguage.googleapis.com/v1/models/gemini-2.5-flash-lite:generateContent";
        public GeminiService(string apiKey)
        {
            _apiKey = apiKey;
            // Увеличим таймаут, на платном тире можно ждать качественный ответ
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
        }
        public async Task<string> AskContextAware(string userVoice, string appMeta, string mouseFocus, string screenText)
        {
            // Собираем промпт с учетом твоей логики Context-Aware
            var fullPrompt = $@"
ТЫ — FLUXCORE. Твоя задача — понимать контекст экрана.
ДАННЫЕ:
1. ПРИЛОЖЕНИЕ: {appMeta}
2. МЫШЬ НАВЕДЕНА НА: {mouseFocus}
3. ТЕКСТ ЭКРАНА: {screenText}
ЗАПРОС: ""{userVoice}""
Ответь кратко и по делу на русском языке.";
            var payload = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new[] { new { text = fullPrompt } }
                    }
                },
                generationConfig = new
                {
                    temperature = 1, // Немного креативности
                    maxOutputTokens = 1024
                }
            };
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            try
            {
                // Шлем запрос в v1 эндпоинт
                var response = await _httpClient.PostAsync($"{Endpoint}?key={_apiKey}", content);
                var responseStr = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    // Если всё равно ошибка — выводим её полностью для дебага
                    return $"⚠️ ОШИБКА ({response.StatusCode}):\n{responseStr}";
                }
                using var doc = JsonDocument.Parse(responseStr);
                if (doc.RootElement.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
                {
                    var text = candidates[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
                    return text?.Trim() ?? "ИИ вернул пустой ответ.";
                }
                return "Ответ не найден в JSON.";
            }
            catch (Exception ex)
            {
                return $"💀 КРИТИЧЕСКИЙ СБОЙ: {ex.Message}";
            }
        }
    }
}