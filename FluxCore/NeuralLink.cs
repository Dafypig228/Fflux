using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FluxCore
{
    public class NeuralLink
    {
        private readonly string _apiKey;
        private readonly HttpClient _client;

        // Оставляю твою рабочую версию (ты сказал 2.5, я поставлю универсальный 2.0-flash-exp, 
        // но если у тебя свой URL - оставь свой!)
        private const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent";

        public NeuralLink(string apiKey)
        {
            _apiKey = apiKey;
            _client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        }

        public async Task<string> ProcessSignal(string userPrompt, string uiTree, string base64Image)
        {
            var systemInstruction = $@"
SYSTEM: Ты — FluxCore (Jarvis).
[UI TREE]:
{uiTree}
[USER QUERY]: ""{userPrompt}""
TASK: Проанализируй скриншот и данные. Ответь развернуто и точно.";

            var payload = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new object[]
                        {
                            new { text = systemInstruction },
                            new { inline_data = new { mime_type = "image/jpeg", data = base64Image } }
                        }
                    }
                },
                // 🔥 ИСПРАВЛЕНИЕ: Снял лимиты (8192 токена = ~6000 слов)
                generationConfig = new
                {
                    temperature = 0.5,
                    maxOutputTokens = 8192
                }
            };

            try
            {
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _client.PostAsync($"{Endpoint}?key={_apiKey}", content);
                var resString = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode) return $"⚠️ Error: {response.StatusCode}";

                using var doc = JsonDocument.Parse(resString);
                if (doc.RootElement.TryGetProperty("candidates", out var c) && c.GetArrayLength() > 0)
                {
                    return c[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString()?.Trim() ?? "...";
                }
                return "[Тишина]";
            }
            catch (Exception ex) { return $"[Сбой: {ex.Message}]"; }
        }
    }
}