using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FluxCore.LLM
{
    /// <summary>
    /// Smart model router with automatic fallback.
    /// Routes requests to the best available model:
    ///   - Vision tasks → _visionModel (Gemini, or Qwen2-VL later)
    ///   - Text tasks → _primary (local DeepSeek), fallback to _fallback (Gemini) on error
    ///   - Embeddings → ALWAYS _fallback (Gemini) — local models don't have matching dims
    ///
    /// Implements ILLMService itself, so callers don't know they're using a router.
    /// </summary>
    public class ModelRouter : ILLMService
    {
        private readonly ILLMService _primary;      // e.g. local DeepSeek for fast text tasks
        private readonly ILLMService _fallback;     // Gemini = always-available cloud fallback
        private readonly ILLMService _visionModel;  // Model with vision support

        public string ModelId => "router";
        public bool SupportsVision => _visionModel.SupportsVision;

        public ModelRouter(ILLMService primary, ILLMService fallback, ILLMService? visionModel = null)
        {
            _primary = primary;
            _fallback = fallback;
            _visionModel = visionModel ?? fallback; // Default: Gemini handles vision
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
            // Route vision tasks to vision model
            bool hasImage = !string.IsNullOrEmpty(screenContext) && screenContext.StartsWith("BASE64:");
            if (hasImage && !_primary.SupportsVision)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[ROUTER] Vision task → {_visionModel.ModelId} (primary {_primary.ModelId} has no vision)");
                return await _visionModel.ChatWithHistory(
                    history, userNewInput, screenContext, activeApp, memories, systemInstructionOverride, temperature);
            }

            // Try primary model first
            System.Diagnostics.Debug.WriteLine($"[ROUTER] Trying primary: {_primary.ModelId}");
            string result = await _primary.ChatWithHistory(
                history, userNewInput, screenContext, activeApp, memories, systemInstructionOverride, temperature);

            // If primary failed → auto-fallback
            if (IsError(result))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[ROUTER] Primary failed ({result.Substring(0, Math.Min(80, result.Length))}), falling back to {_fallback.ModelId}");
                result = await _fallback.ChatWithHistory(
                    history, userNewInput, screenContext, activeApp, memories, systemInstructionOverride, temperature);
            }

            return result;
        }

        public async Task<string> GenerateText(string prompt, float temperature = 0.5f)
        {
            System.Diagnostics.Debug.WriteLine($"[ROUTER] GenerateText → primary: {_primary.ModelId}");
            string result = await _primary.GenerateText(prompt, temperature);

            if (IsError(result))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[ROUTER] Primary failed, falling back to {_fallback.ModelId}");
                result = await _fallback.GenerateText(prompt, temperature);
            }

            return result;
        }

        public async Task<string> AskSimple(string prompt)
        {
            string result = await _primary.AskSimple(prompt);

            if (IsError(result))
            {
                result = await _fallback.AskSimple(prompt);
            }

            return result;
        }

        /// <summary>
        /// Embeddings ALWAYS go to Gemini fallback.
        /// Local models don't have matching embedding dimensions for MemoryService's vector DB.
        /// This is hardcoded, not routed.
        /// </summary>
        public Task<float[]> GetEmbeddingAsync(string text)
        {
            return _fallback.GetEmbeddingAsync(text);
        }

        // ==========================================
        // HELPERS
        // ==========================================

        private static bool IsError(string response)
        {
            if (string.IsNullOrWhiteSpace(response)) return true;
            return response.StartsWith("⚠️") ||
                   response.StartsWith("Exception:") ||
                   response.StartsWith("[Blocked") ||
                   response.StartsWith("[Response blocked") ||
                   response.StartsWith("ERROR:") ||
                   response == "No response from AI." ||
                   response == "No response from local model." ||
                   response == "...";
        }
    }
}
