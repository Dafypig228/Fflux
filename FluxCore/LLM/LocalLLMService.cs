using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FluxCore.LLM
{
    /// <summary>
    /// OpenAI-compatible local model client.
    /// Talks to llama.cpp server, Ollama, LM Studio, or any server
    /// that implements the /v1/chat/completions endpoint.
    ///
    /// Designed for: DeepSeek-R1 1.5B (INT8, ~1.6GB VRAM on GTX 1650 Ti 4GB)
    /// </summary>
    public class LocalLLMService : ILLMService
    {
        private readonly HttpClient _http;
        private readonly string _baseUrl;
        private readonly string _modelId;

        public string ModelId => _modelId;
        public bool SupportsVision => false; // DeepSeek-R1 1.5B = text only

        public LocalLLMService(string baseUrl, string modelId)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _modelId = modelId;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        }

        public async Task<string> ChatWithHistory(
            List<ChatMessage> history,
            string userNewInput,
            string screenContext,
            string activeApp,
            string memories,
            string? systemInstructionOverride = null,
            float temperature = 0.3f)
        {
            try
            {
                // Build OpenAI-format messages array
                var messages = new List<object>();

                // System instruction → {"role":"system", "content":"..."}
                if (!string.IsNullOrWhiteSpace(systemInstructionOverride))
                {
                    messages.Add(new { role = "system", content = systemInstructionOverride });
                }

                // Conversation history → alternating user/assistant
                if (history != null)
                {
                    foreach (var msg in history)
                    {
                        if (string.IsNullOrWhiteSpace(msg.Text)) continue;
                        messages.Add(new
                        {
                            role = msg.IsUser ? "user" : "assistant",
                            content = msg.Text
                        });
                    }
                }

                // Current user message
                string userContent = userNewInput;

                // If there's a text screen context (non-vision), append it
                if (!string.IsNullOrEmpty(screenContext) && !screenContext.StartsWith("BASE64:"))
                {
                    userContent += $"\n\n[SCREEN CONTEXT]: {screenContext}";
                }
                else if (!string.IsNullOrEmpty(screenContext) && screenContext.StartsWith("BASE64:"))
                {
                    // Vision content — this model doesn't support it
                    System.Diagnostics.Debug.WriteLine(
                        $"[LOCAL LLM] Warning: {_modelId} doesn't support vision, skipping BASE64 screenshot");
                }

                messages.Add(new { role = "user", content = userContent });

                return await SendChatCompletion(messages, temperature);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LOCAL LLM] Error: {ex.Message}");
                return $"⚠️ Local model error: {ex.Message}";
            }
        }

        public async Task<string> GenerateText(string prompt, float temperature = 0.5f)
        {
            try
            {
                var messages = new List<object>
                {
                    new { role = "user", content = prompt }
                };
                return await SendChatCompletion(messages, temperature);
            }
            catch (Exception ex)
            {
                return $"⚠️ Local model error: {ex.Message}";
            }
        }

        public Task<string> AskSimple(string prompt)
        {
            return GenerateText(prompt);
        }

        /// <summary>
        /// Local models don't have matching embedding dimensions.
        /// Always returns empty — ModelRouter routes embeddings to Gemini fallback.
        /// </summary>
        public Task<float[]> GetEmbeddingAsync(string text)
        {
            return Task.FromResult(Array.Empty<float>());
        }

        // ==========================================
        // INTERNAL: POST to /v1/chat/completions
        // ==========================================
        private async Task<string> SendChatCompletion(List<object> messages, float temperature = 0.5f)
        {
            var payload = new
            {
                model = _modelId,
                messages = messages,
                temperature = (double)temperature,
                max_tokens = 4096,
                stream = false
            };

            var json = JsonSerializer.Serialize(payload);
            System.Diagnostics.Debug.WriteLine($"[LOCAL LLM] POST {_baseUrl}/v1/chat/completions");

            var content = new StringContent(json, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await _http.PostAsync($"{_baseUrl}/v1/chat/completions", content);
            }
            catch (HttpRequestException ex)
            {
                // Server offline or unreachable
                return $"⚠️ Local model offline ({_baseUrl}): {ex.Message}";
            }
            catch (TaskCanceledException)
            {
                return "⚠️ Local model timed out.";
            }

            var responseStr = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return $"⚠️ Local model error {response.StatusCode}: {responseStr.Substring(0, Math.Min(200, responseStr.Length))}";
            }

            // Parse OpenAI-format response:
            // { "choices": [{ "message": { "content": "..." } }] }
            try
            {
                using var doc = JsonDocument.Parse(responseStr);

                if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                    choices.GetArrayLength() > 0)
                {
                    var first = choices[0];
                    if (first.TryGetProperty("message", out var message) &&
                        message.TryGetProperty("content", out var textEl))
                    {
                        return textEl.GetString()?.Trim() ?? "...";
                    }
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[LOCAL LLM] Unexpected response: {responseStr.Substring(0, Math.Min(300, responseStr.Length))}");
                return "No response from local model.";
            }
            catch (JsonException ex)
            {
                return $"⚠️ Local model parse error: {ex.Message}";
            }
        }
    }
}
