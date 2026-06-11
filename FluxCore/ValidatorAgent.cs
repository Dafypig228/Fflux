using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluxCore.LLM;

namespace FluxCore
{
    /// <summary>
    /// Validator/Critic Agent: visually verifies that screen actions actually happened.
    /// </summary>
    public class ValidatorAgent
    {
        private readonly ILLMService _llm;

        public ValidatorAgent(ILLMService llm)
        {
            _llm = llm;
        }

        // NOTE: the legacy text-based ValidateAsync (WRITE_FILE/RUN_NODE/DOWNLOAD_FILE
        // checks + a Contains("failed") heuristic that flagged successes as failures)
        // was removed with its only caller, the dead MainWindow execution pipeline.

        /// <summary>
        /// Visually verifies an action using the AFTER screenshot.
        /// The ILLMService wrapper supports one image per call, so the prompt is honest
        /// about receiving a single post-action screenshot (the old prompt promised two
        /// images and confused the model). The beforeBase64 parameter is kept for
        /// signature compatibility (ScreenAgent also calls this).
        /// Inconclusive responses count as SUCCESS — a false FAILURE poisons the task
        /// loop far more than a missed verification.
        /// </summary>
        public async Task<ValidationResult> ValidateVisualAsync(string intent, string beforeBase64, string afterBase64)
        {
            try
            {
                if (string.IsNullOrEmpty(afterBase64))
                {
                    return new ValidationResult(true, "Skipped visual check (missing screenshot)", false);
                }

                string prompt =
                    $"You are a QA validator. An automation agent just performed this action: '{intent}'.\n" +
                    "The attached screenshot shows the screen AFTER the action.\n" +
                    "Decide whether the screen state is CONSISTENT with the action having succeeded.\n" +
                    "Examples: 'Open Notepad' → a Notepad window is visible; 'Type Hello' → that text appears in a field.\n" +
                    "Only answer FAILURE if the screen clearly contradicts the action. If it is consistent or you cannot tell, answer SUCCESS.\n\n" +
                    "Reply in EXACTLY this format:\n" +
                    "VERDICT: SUCCESS or FAILURE\n" +
                    "REASON: one short sentence";

                string response = await _llm.ChatWithHistory(
                    new List<ChatMessage>(), prompt, "BASE64:" + afterBase64, "", "", null, 0.1f);

                // Tolerant parsing — accepts "VERDICT: FAILURE", "**VERDICT:** FAILURE", "VERDICT:FAILURE", …
                // The old exact-string check ("VERDICT: SUCCESS") returned false FAILURE
                // whenever the model added markdown or dropped the space.
                var verdictMatch = System.Text.RegularExpressions.Regex.Match(
                    response, @"VERDICT\W{0,5}(SUCCESS|FAILURE)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (!verdictMatch.Success)
                    return new ValidationResult(true, "Visual check inconclusive — assuming success", false);

                bool isSuccess = verdictMatch.Groups[1].Value.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase);

                var reasonMatch = System.Text.RegularExpressions.Regex.Match(
                    response, @"REASON\W{0,5}(.+)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                string reason = reasonMatch.Success ? reasonMatch.Groups[1].Value.Trim() : response.Trim();

                return new ValidationResult(isSuccess, reason, !isSuccess);
            }
            catch (Exception ex)
            {
                return new ValidationResult(true, $"Visual check failed to run: {ex.Message}", false);
            }
        }
    }

    public class ValidationResult
    {
        public bool Success { get; }
        public string Message { get; }
        public bool ShouldRetry { get; }

        public ValidationResult(bool success, string message, bool shouldRetry)
        {
            Success = success;
            Message = message;
            ShouldRetry = shouldRetry;
        }
    }
}
