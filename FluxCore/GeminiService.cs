using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FluxCore
{
    // --- Helper classes for Gemini API ---
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

    public class GeminiService : FluxCore.LLM.ILLMService
    {
        private readonly string _apiKey;
        private readonly HttpClient _httpClient = new HttpClient();

        private const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent";

        // ILLMService properties
        public string ModelId => "gemini-2.5-flash";
        public bool SupportsVision => true;

        public GeminiService(string apiKey)
        {
            _apiKey = apiKey;
            _httpClient.Timeout = TimeSpan.FromSeconds(60);
        }

        // ==========================================
        // 1. MAIN METHOD: Chat with history + proper system_instruction
        // ==========================================
        public async Task<string> ChatWithHistory(
            List<ChatMessage> history,
            string userNewInput,
            string screenContext,
            string activeApp,
            string memories,
            string? systemInstructionOverride = null,
            float temperature = 0.3f)
        {
            // 1. Determine system instruction
            string systemInstruction;
            if (systemInstructionOverride != null)
            {
                systemInstruction = systemInstructionOverride;
            }
            else
            {
                // Neutral fallback for legacy callers (AskSimple, AskContextAware)
                // No "MODES" or "CONVERSATION vs ACTION" — callers provide their own instructions
                systemInstruction = "You are Davos, a helpful AI assistant. " +
                    $"Respond concisely in the user's language. Context: {activeApp}";
            }

            // 2. Convert chat history to Gemini format
            var contents = new List<GeminiContent>();
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

            // 3. Build CLEAN user message — just the user's input, NOT system prompt
            var userParts = new List<GeminiPart> { new GeminiPart { text = userNewInput } };

            // Add screenshot as vision input if present
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
            else if (!string.IsNullOrEmpty(screenContext))
            {
                // Text-only screen context (legacy fallback)
                userParts.Add(new GeminiPart { text = $"[SCREEN CONTEXT]: {screenContext}" });
            }

            contents.Add(new GeminiContent
            {
                role = "user",
                parts = userParts
            });

            // 4. System instruction goes as a SEPARATE top-level field (Gemini API native)
            return await SendPayload(contents, systemInstruction, temperature);
        }

        // ==========================================
        // 2. LEGACY METHODS (backward compatibility)
        // ==========================================

        public async Task<string> AskSimple(string prompt)
        {
            return await ChatWithHistory(new List<ChatMessage>(), prompt, "", "", "");
        }

        public async Task<string> AskContextAware(string userVoice, string appMeta, string mouseFocus, string screenText, List<string> memories, string clipboard)
        {
            string fullScreen = $"Mouse: {mouseFocus}\nScreen: {screenText}\nClipboard: {clipboard}";
            string memoryBlock = (memories != null) ? string.Join("\n", memories) : "";
            return await ChatWithHistory(new List<ChatMessage>(), userVoice, fullScreen, appMeta, memoryBlock);
        }

        // ==========================================
        // 3. SEND PAYLOAD — Proper Gemini API structure
        // ==========================================
        /// <summary>
        /// Sends payload to Gemini API with native system_instruction field.
        /// Includes exponential backoff retry for 429/503 errors.
        /// </summary>
        private async Task<string> SendPayload(object contentsObj, string? systemInstruction = null, float temperature = 0.5f)
        {
            // Build the proper Gemini API payload:
            // {
            //   "system_instruction": { "parts": [{"text": "..."}] },  ← TOP-LEVEL (enables caching!)
            //   "contents": [...],                                      ← conversation history + user message
            //   "generationConfig": {...}
            // }
            // Safety settings: BLOCK_NONE for all categories since Davos operates
            // on the user's own PC — screenshots are the user's own screen content
            var safetySettings = new[]
            {
                new { category = "HARM_CATEGORY_HARASSMENT", threshold = "BLOCK_NONE" },
                new { category = "HARM_CATEGORY_HATE_SPEECH", threshold = "BLOCK_NONE" },
                new { category = "HARM_CATEGORY_SEXUALLY_EXPLICIT", threshold = "BLOCK_NONE" },
                new { category = "HARM_CATEGORY_DANGEROUS_CONTENT", threshold = "BLOCK_NONE" }
            };

            object payload;
            if (!string.IsNullOrWhiteSpace(systemInstruction))
            {
                payload = new
                {
                    system_instruction = new
                    {
                        parts = new[] { new { text = systemInstruction } }
                    },
                    contents = contentsObj,
                    generationConfig = new { temperature = (double)temperature, maxOutputTokens = 32768 },
                    safetySettings
                };
            }
            else
            {
                payload = new
                {
                    contents = contentsObj,
                    generationConfig = new { temperature = (double)temperature, maxOutputTokens = 32768 },
                    safetySettings
                };
            }

            var json = JsonSerializer.Serialize(payload);
            System.Diagnostics.Debug.WriteLine($"[GEMINI PAYLOAD] {json}");
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            string url = $"{Endpoint}?key={_apiKey}";

            try
            {
                var response = await _httpClient.PostAsync(url, content);
                var responseStr = await response.Content.ReadAsStringAsync();

                // ── 429 / 503 RETRY with exponential backoff ──
                if ((int)response.StatusCode == 429 || (int)response.StatusCode == 503)
                {
                    for (int retry = 0; retry < 3; retry++)
                    {
                        int delayMs = (int)Math.Pow(2, retry) * 1000; // 1s, 2s, 4s
                        System.Diagnostics.Debug.WriteLine(
                            $"[GEMINI] Rate limited ({response.StatusCode}), retry {retry + 1}/3 in {delayMs}ms");
                        await Task.Delay(delayMs);

                        // Must recreate StringContent (it's disposed after POST)
                        content = new StringContent(json, Encoding.UTF8, "application/json");
                        response = await _httpClient.PostAsync(url, content);
                        responseStr = await response.Content.ReadAsStringAsync();

                        if (response.IsSuccessStatusCode) break;
                        if ((int)response.StatusCode != 429 && (int)response.StatusCode != 503) break;
                    }
                }

                // ── Error response (after retries) ──
                if (!response.IsSuccessStatusCode)
                    return $"⚠️ Error {response.StatusCode}: {responseStr.Substring(0, Math.Min(200, responseStr.Length))}";

                // ── Parse successful response ──
                using var doc = JsonDocument.Parse(responseStr);

                // Check for prompt-level block (blocked BEFORE generation)
                if (doc.RootElement.TryGetProperty("promptFeedback", out var feedback) &&
                    feedback.TryGetProperty("blockReason", out var blockReason))
                {
                    return $"[Blocked by API: {blockReason.GetString()}]";
                }

                if (!doc.RootElement.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
                {
                    string rawResp = responseStr.Substring(0, Math.Min(500, responseStr.Length));
                    System.Diagnostics.Debug.WriteLine($"[GEMINI] EMPTY CANDIDATES. Full response: {rawResp}");
                    if (doc.RootElement.TryGetProperty("promptFeedback", out var pf))
                    {
                        System.Diagnostics.Debug.WriteLine($"[GEMINI] Prompt feedback: {pf}");
                        if (pf.TryGetProperty("safetyRatings", out var sr))
                            System.Diagnostics.Debug.WriteLine($"[GEMINI] Safety ratings: {sr}");
                    }
                    return "[EMPTY_RESPONSE:safety_likely]";
                }

                var first = candidates[0];

                // SAFE content extraction — never crashes on missing keys
                if (first.TryGetProperty("content", out var contentEl) &&
                    contentEl.TryGetProperty("parts", out var parts) &&
                    parts.GetArrayLength() > 0 &&
                    parts[0].TryGetProperty("text", out var textEl))
                {
                    string resultText = textEl.GetString()?.Trim() ?? "...";

                    // CHECK for truncation — if finishReason is MAX_TOKENS, the response was cut off
                    if (first.TryGetProperty("finishReason", out var finishReason))
                    {
                        var finishStr = finishReason.GetString();
                        if (finishStr == "MAX_TOKENS")
                        {
                            System.Diagnostics.Debug.WriteLine("[GEMINI] ⚠ Response truncated by MAX_TOKENS!");
                            // Append closing ]] if there's an unclosed command
                            if (resultText.LastIndexOf("[[") > resultText.LastIndexOf("]]"))
                            {
                                resultText += "]]";
                                System.Diagnostics.Debug.WriteLine("[GEMINI] Auto-closed truncated command tag");
                            }
                        }
                    }

                    return resultText;
                }

                // No content = safety/other block
                if (first.TryGetProperty("finishReason", out var reason))
                {
                    var reasonStr = reason.GetString();
                    if (reasonStr != "STOP")
                        return $"[Response blocked: {reasonStr}]";
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[GEMINI] Unexpected response: {responseStr.Substring(0, Math.Min(300, responseStr.Length))}");
                ChatLogger.LogError("Gemini/unexpected", responseStr);
                return "No response from AI.";
            }
            catch (TaskCanceledException)
            {
                return "⚠️ Request timed out.";
            }
            catch (Exception ex)
            {
                return $"Exception: {ex.Message}";
            }
        }

        // ==========================================
        // 4. EMBEDDINGS
        // ==========================================
        private LocalEmbeddingService? _localEmbedder;

        /// <summary>
        /// Sets the local ONNX embedder. When ready, it takes priority over the Gemini API,
        /// keeping all passive sensor data offline and free.
        /// </summary>
        public void SetLocalEmbedder(LocalEmbeddingService embedder)
            => _localEmbedder = embedder;

        public async Task<float[]> GetEmbeddingAsync(string text)
        {
            // Use local ONNX model when available — private, free, 384-dim
            if (_localEmbedder?.IsReady == true)
                return await Task.FromResult(_localEmbedder.GetEmbedding(text));


            var payload = new
            {
                model = "models/text-embedding-004",
                content = new { parts = new[] { new { text = text } } }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                var response = await _httpClient.PostAsync(
                    $"https://generativelanguage.googleapis.com/v1beta/models/text-embedding-004:embedContent?key={_apiKey}",
                    content);
                var responseStr = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode) return new float[0];

                using var doc = JsonDocument.Parse(responseStr);
                if (doc.RootElement.TryGetProperty("embedding", out var embeddingEl) &&
                    embeddingEl.TryGetProperty("values", out var valuesEl))
                {
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
        // 5. SIMPLE GENERATION (for classifiers, summarization, etc.)
        // ==========================================
        // ==========================================
        // 5b. AUDIO TRANSCRIPTION — sends WAV to Gemini for STT
        // ==========================================
        public async Task<string> TranscribeAudio(string base64Wav)
        {
            var contents = new List<GeminiContent>
            {
                new GeminiContent
                {
                    role = "user",
                    parts = new List<GeminiPart>
                    {
                        new GeminiPart
                        {
                            text = "Transcribe this audio exactly as spoken. The speaker may use Russian, English, Kazakh or mix languages. " +
                                   "Output ONLY the raw transcription text, nothing else. No quotes, no formatting, no explanations. " +
                                   "If the audio is silent or contains only noise, respond with exactly: [SILENT]"
                        },
                        new GeminiPart
                        {
                            inline_data = new GeminiInlineData
                            {
                                mime_type = "audio/wav",
                                data = base64Wav
                            }
                        }
                    }
                }
            };
            return await SendPayload(contents, null, 0.1f);
        }

        public async Task<string> GenerateText(string prompt, float temperature = 0.5f)
        {
            var contents = new List<GeminiContent>
            {
                new GeminiContent
                {
                    role = "user",
                    parts = new List<GeminiPart> { new GeminiPart { text = prompt } }
                }
            };
            return await SendPayload(contents, null, temperature);
        }

        // ==========================================
        // 6. COMMITMENT TOOL CALLING
        // ==========================================

        /// <summary>
        /// Single LLM call that handles conversation AND exposes native Gemini function-calling
        /// tools for scheduling, PC task execution, and mid-task context injection.
        ///
        /// Gemini AUTO mode may return BOTH a text part AND functionCall parts in the SAME
        /// response. We iterate ALL parts so nothing is lost:
        ///   • text                → what Davos says to the user right now
        ///   • commitment_add      → backend signal to schedule a deferred action
        ///   • execute_pc_task     → backend signal to launch JarvisCore automation
        ///   • inject_task_context → backend signal to inject context into running task
        /// </summary>
        public async Task<(string Text, CommitmentCall? Commitment, PcTaskCall? PcTask, InjectCtxCall? InjectCtx)>
            ChatWithAgentToolsAsync(
                List<ChatMessage> history,
                string userInput,
                string systemInstruction,
                float temperature,
                bool hasActiveTasks = false)
        {
            // Build contents array identical to ChatWithHistory
            var contents = new List<object>();
            foreach (var msg in history)
            {
                string role = msg.IsUser ? "user" : "model";
                contents.Add(new { role, parts = new[] { new { text = msg.Text ?? "" } } });
            }
            contents.Add(new { role = "user", parts = new[] { new { text = userInput } } });

            // Tool declarations
            object commitmentDecl = new
            {
                name        = "commitment_add",
                description = "Schedule a deferred action OR immediately deliver a message to the user. " +
                              "ALWAYS use this for: anything with a time delay ('in 10 seconds', 'in 5 minutes', " +
                              "'after X'), any request to 'write me', 'message me', 'remind me', 'contact me', " +
                              "'send me something'. This is handled entirely inside Davos — zero Windows UI needed.",
                parameters  = new
                {
                    type       = "object",
                    properties = new
                    {
                        delay_seconds = new
                        {
                            type        = "integer",
                            description = "Seconds to wait before executing. Use 0 for immediate delivery."
                        },
                        description = new
                        {
                            type        = "string",
                            description = "Short imperative description of the action to execute"
                        }
                    },
                    required = new[] { "delay_seconds", "description" }
                }
            };

            object pcTaskDecl = new
            {
                name        = "execute_pc_task",
                description = "Execute a task requiring DIRECT WINDOWS UI CONTROL or SHELL SCRIPTING. " +
                              "Use ONLY for: opening apps, clicking UI elements, typing, scrolling, " +
                              "running PowerShell/Python scripts, managing files on disk, browser navigation. " +
                              "NEVER use for: scheduling, timers, reminders, messaging the user, " +
                              "or anything involving Telegram — those are commitment_add.",
                parameters  = new
                {
                    type       = "object",
                    properties = new
                    {
                        description = new
                        {
                            type        = "string",
                            description = "A precise, imperative PC automation directive. Translate the user's " +
                                          "intent into clean steps: what app, what actions, what goal. " +
                                          "Strip conversational filler. Never include 'tell me when done' or 'let me know'."
                        }
                    },
                    required = new[] { "description" }
                }
            };

            var functionDecls = new List<object> { commitmentDecl, pcTaskDecl };

            // inject_task_context is only offered when a task is actually running
            if (hasActiveTasks)
            {
                functionDecls.Add(new
                {
                    name        = "inject_task_context",
                    description = "Provide new information or a correction to the currently running task. " +
                                  "Use when the user is clarifying, correcting, or adding context for a task " +
                                  "that is already in progress.",
                    parameters  = new
                    {
                        type       = "object",
                        properties = new
                        {
                            text = new
                            {
                                type        = "string",
                                description = "The update or correction to inject into the running task"
                            }
                        },
                        required = new[] { "text" }
                    }
                });
            }

            var safetySettings = new[]
            {
                new { category = "HARM_CATEGORY_HARASSMENT",        threshold = "BLOCK_NONE" },
                new { category = "HARM_CATEGORY_HATE_SPEECH",       threshold = "BLOCK_NONE" },
                new { category = "HARM_CATEGORY_SEXUALLY_EXPLICIT", threshold = "BLOCK_NONE" },
                new { category = "HARM_CATEGORY_DANGEROUS_CONTENT", threshold = "BLOCK_NONE" }
            };

            object payload = new
            {
                system_instruction = new { parts = new[] { new { text = systemInstruction } } },
                contents,
                tools            = new[] { new { functionDeclarations = functionDecls.ToArray() } },
                tool_config      = new { functionCallingConfig = new { mode = "AUTO" } },
                generationConfig = new { temperature = (double)temperature, maxOutputTokens = 8192 },
                safetySettings
            };

            var json    = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            string url  = $"{Endpoint}?key={_apiKey}";

            try
            {
                var response    = await _httpClient.PostAsync(url, content);
                var responseStr = await response.Content.ReadAsStringAsync();

                // Rate-limit retry
                if ((int)response.StatusCode == 429 || (int)response.StatusCode == 503)
                {
                    for (int retry = 0; retry < 3; retry++)
                    {
                        await Task.Delay((int)Math.Pow(2, retry) * 1000);
                        content  = new StringContent(json, Encoding.UTF8, "application/json");
                        response = await _httpClient.PostAsync(url, content);
                        responseStr = await response.Content.ReadAsStringAsync();
                        if (response.IsSuccessStatusCode) break;
                        if ((int)response.StatusCode != 429 && (int)response.StatusCode != 503) break;
                    }
                }

                if (!response.IsSuccessStatusCode)
                {
                    string err = responseStr.Substring(0, Math.Min(200, responseStr.Length));
                    return ($"⚠️ Error {response.StatusCode}: {err}", null, null, null);
                }

                using var doc = JsonDocument.Parse(responseStr);
                if (!doc.RootElement.TryGetProperty("candidates", out var candidates) ||
                    candidates.GetArrayLength() == 0)
                {
                    ChatLogger.LogError("Gemini/no-candidates", responseStr);
                    return ("No response from AI.", null, null, null);
                }

                var first = candidates[0];
                if (!first.TryGetProperty("content", out var contentEl) ||
                    !contentEl.TryGetProperty("parts", out var parts))
                {
                    ChatLogger.LogError("Gemini/no-parts", responseStr);
                    return ("No response from AI.", null, null, null);
                }

                // Iterate ALL parts — Gemini AUTO mode can return text + functionCall(s) together
                var textBuilder = new StringBuilder();
                CommitmentCall? commitment = null;
                PcTaskCall?     pcTask     = null;
                InjectCtxCall?  injectCtx  = null;

                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var textEl))
                    {
                        textBuilder.Append(textEl.GetString() ?? "");
                    }
                    else if (part.TryGetProperty("functionCall", out var fc) &&
                             fc.TryGetProperty("name", out var nameEl) &&
                             fc.TryGetProperty("args", out var args))
                    {
                        switch (nameEl.GetString())
                        {
                            case "commitment_add":
                                int    delaySec = args.TryGetProperty("delay_seconds", out var d)   ? d.GetInt32()        : 60;
                                string desc     = args.TryGetProperty("description",   out var dsc)  ? dsc.GetString() ?? "" : "";
                                commitment = new CommitmentCall(delaySec, desc);
                                break;

                            case "execute_pc_task":
                                string taskDesc = args.TryGetProperty("description", out var td) ? td.GetString() ?? "" : "";
                                pcTask = new PcTaskCall(taskDesc);
                                break;

                            case "inject_task_context":
                                string ctxText = args.TryGetProperty("text", out var ct2) ? ct2.GetString() ?? "" : "";
                                injectCtx = new InjectCtxCall(ctxText);
                                break;
                        }
                    }
                }

                return (textBuilder.ToString().Trim(), commitment, pcTask, injectCtx);
            }
            catch (TaskCanceledException)
            {
                return ("⚠️ Request timed out.", null, null, null);
            }
            catch (Exception ex)
            {
                return ($"Exception: {ex.Message}", null, null, null);
            }
        }
    }

    /// <summary>Parsed output from Gemini's commitment_add function call.</summary>
    public record CommitmentCall(int DelaySeconds, string Description);
    /// <summary>Parsed output from Gemini's execute_pc_task function call.</summary>
    public record PcTaskCall(string Description);
    /// <summary>Parsed output from Gemini's inject_task_context function call.</summary>
    public record InjectCtxCall(string Text);
}
