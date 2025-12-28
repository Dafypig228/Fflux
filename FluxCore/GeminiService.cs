using System;
using System.Collections.Generic;
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

        // Модель 1.5 Flash (быстрая и дешевая)
        private const string Endpoint = "https://generativelanguage.googleapis.com/v1/models/gemini-2.5-flash:generateContent";

        public GeminiService(string apiKey)
        {
            _apiKey = apiKey;
            _httpClient.Timeout = TimeSpan.FromSeconds(60);
        }

        // --- 1. ГЛАВНЫЙ МЕТОД (С ПАМЯТЬЮ) ---
        public async Task<string> AskContextAware(string userVoice, string appMeta, string mouseFocus, string screenText, List<string> memories, string clipboard)
        {
            string memoryBlock = (memories != null && memories.Count > 0) ? string.Join("\n", memories) : "NO DATA.";

            var systemPrompt = $@"
SYSTEM: FLUX OS.
MODE: PRECISE CURSOR ANALYSIS.

--- MEMORY ---
{memoryBlock}

--- VISUAL INPUTS ---
1. ACTIVE APP: {appMeta}

2. 🎯 MOUSE FOCUS (EXACT OBJECT UNDER CURSOR):
{mouseFocus}

3. 🖼️ SCREEN AREA TEXT (400px RADIUS AROUND CURSOR):
{screenText}

4. CLIPBOARD:
{clipboard}

--- USER REQUEST ---
'{userVoice}'

--- RULES ---
1. Identify the 'MOUSE FOCUS' object.
2. WARNING: If 'MOUSE FOCUS' says ""BLOCKED BY OVERLAY"", IGNORE IT completely. 
   Instead, rely 100% on 'SCREEN AREA TEXT' and 'ACTIVE APP' to guess what is under the cursor.
3. If the user points at a chat, name the chat (from OCR or container info).
4. Be concise.";

            return await SendRequest(systemPrompt);
        }

        // --- 2. ПРОСТОЙ МЕТОД (FIX ОШИБКИ) ---
        // Мы возвращаем этот метод, чтобы старый код не ломался
        public async Task<string> AskSimple(string prompt)
        {
            return await SendRequest(prompt);
        }

        // --- ВНУТРЕННЯЯ ОТПРАВКА ---
        private async Task<string> SendRequest(string promptText)
        {
            var payload = new
            {
                contents = new[] { new { parts = new[] { new { text = promptText } } } }
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
                    // Безопасное получение текста (fix null reference warning)
                    var textElement = candidates[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text");
                    return textElement.GetString()?.Trim() ?? "...";
                }
                return "Нет данных от ИИ.";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }
    }
}