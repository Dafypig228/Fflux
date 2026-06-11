using System.Collections.Generic;
using System.Threading.Tasks;

namespace FluxCore.LLM
{
    /// <summary>
    /// Universal LLM interface. Every model provider (Gemini, local DeepSeek, Ollama, etc.)
    /// implements this. Method signatures match GeminiService exactly — zero friction migration.
    /// </summary>
    public interface ILLMService
    {
        /// <summary>Model identifier (e.g. "gemini-2.5-flash", "deepseek-r1:1.5b")</summary>
        string ModelId { get; }

        /// <summary>Whether this model can process images (BASE64 screenshots)</summary>
        bool SupportsVision { get; }

        /// <summary>
        /// Chat with conversation history + system instruction.
        /// System instruction goes into the model's native system prompt field (not concatenated).
        /// </summary>
        Task<string> ChatWithHistory(
            List<ChatMessage> history,
            string userNewInput,
            string screenContext,
            string activeApp,
            string memories,
            string? systemInstructionOverride = null,
            float temperature = 0.3f);

        /// <summary>Simple text generation (single prompt, no history)</summary>
        Task<string> GenerateText(string prompt, float temperature = 0.5f);

        /// <summary>Legacy compatibility — delegates to GenerateText</summary>
        Task<string> AskSimple(string prompt);

        /// <summary>
        /// Get embedding vector for semantic search.
        /// Local models return empty array — only Gemini has matching embedding dimensions.
        /// </summary>
        Task<float[]> GetEmbeddingAsync(string text);
    }
}
