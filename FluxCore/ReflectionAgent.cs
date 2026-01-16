using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace FluxCore
{
    /// <summary>
    /// Reflection Agent: Analyzes failures and suggests alternative approaches.
    /// This is what makes JARVIS "think" about what went wrong.
    /// </summary>
    public class ReflectionAgent
    {
        private readonly GeminiService _gemini;

        public ReflectionAgent(GeminiService gemini)
        {
            _gemini = gemini;
        }

        /// <summary>
        /// Analyzes a failure and returns a recovery strategy.
        /// </summary>
        public async Task<RecoveryPlan> AnalyzeFailureAsync(
            string originalGoal,
            string attemptedAction,
            string failureReason,
            string screenshotBase64,
            string currentWindow)
        {
            var prompt = $@"
You are an error recovery AI. Analyze this failure:

ORIGINAL GOAL: {originalGoal}
ATTEMPTED ACTION: {attemptedAction}
FAILURE REASON: {failureReason}
CURRENT WINDOW: {currentWindow}

Look at the screenshot. What went wrong and how should we fix it?

Common issues and fixes:
1. Wrong window focused → Open/focus the correct app first
2. Element not found → Try scrolling, or use keyboard navigation (Tab, Enter)
3. Safety stop → Wait longer for window to load
4. Click failed → Try clicking by coordinates instead

Respond with ONE of these JSON formats:

If we should try a different approach:
{{""action"": ""alternative"", ""command"": ""[[COMMAND:args]]"", ""reason"": ""why this will work""}}

If we should retry the same thing after waiting:
{{""action"": ""retry"", ""wait_ms"": 2000, ""reason"": ""why waiting will help""}}

If the task is impossible right now:
{{""action"": ""abort"", ""reason"": ""why we cannot continue""}}

JSON:";

            try
            {
                string response = await _gemini.ChatWithHistory(
                    new List<ChatMessage>(),
                    prompt,
                    string.IsNullOrEmpty(screenshotBase64) ? "" : "BASE64:" + screenshotBase64,
                    currentWindow,
                    ""
                );

                // Parse JSON from response
                response = response.Trim();
                if (response.StartsWith("```json")) response = response.Substring(7);
                if (response.StartsWith("```")) response = response.Substring(3);
                if (response.EndsWith("```")) response = response.Substring(0, response.Length - 3);
                response = response.Trim();

                // Find JSON in response
                int jsonStart = response.IndexOf('{');
                int jsonEnd = response.LastIndexOf('}') + 1;
                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    response = response.Substring(jsonStart, jsonEnd - jsonStart);
                }

                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;

                string action = root.GetProperty("action").GetString() ?? "abort";
                string reason = root.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";

                switch (action)
                {
                    case "alternative":
                        string command = root.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "";
                        return new RecoveryPlan
                        {
                            Strategy = RecoveryStrategy.Alternative,
                            AlternativeCommand = command,
                            Reason = reason
                        };

                    case "retry":
                        int waitMs = root.TryGetProperty("wait_ms", out var w) ? w.GetInt32() : 1000;
                        return new RecoveryPlan
                        {
                            Strategy = RecoveryStrategy.Retry,
                            WaitBeforeRetryMs = waitMs,
                            Reason = reason
                        };

                    case "abort":
                    default:
                        return new RecoveryPlan
                        {
                            Strategy = RecoveryStrategy.Abort,
                            Reason = reason
                        };
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ReflectionAgent] Parse error: {ex.Message}");
                return new RecoveryPlan
                {
                    Strategy = RecoveryStrategy.Retry,
                    WaitBeforeRetryMs = 1000,
                    Reason = "Could not analyze failure, retrying with delay"
                };
            }
        }

        /// <summary>
        /// Quick analysis without screenshot - for faster decisions.
        /// </summary>
        public RecoveryPlan QuickAnalyze(string failureReason)
        {
            // Pattern-based quick decisions
            if (failureReason.Contains("SAFETY STOP"))
            {
                return new RecoveryPlan
                {
                    Strategy = RecoveryStrategy.Retry,
                    WaitBeforeRetryMs = 2000,
                    Reason = "Window not focused yet, waiting for it to open"
                };
            }

            if (failureReason.Contains("not found"))
            {
                return new RecoveryPlan
                {
                    Strategy = RecoveryStrategy.Alternative,
                    AlternativeCommand = "[[SCROLL:down]]",
                    Reason = "Element might be below the fold, scrolling to find it"
                };
            }

            if (failureReason.Contains("timeout"))
            {
                return new RecoveryPlan
                {
                    Strategy = RecoveryStrategy.Retry,
                    WaitBeforeRetryMs = 3000,
                    Reason = "Operation timed out, waiting longer"
                };
            }

            return new RecoveryPlan
            {
                Strategy = RecoveryStrategy.Retry,
                WaitBeforeRetryMs = 1000,
                Reason = "Unknown error, retrying"
            };
        }
    }

    public enum RecoveryStrategy
    {
        Retry,        // Try the same action again (maybe after waiting)
        Alternative,  // Try a different action
        Abort         // Give up on this step
    }

    public class RecoveryPlan
    {
        public RecoveryStrategy Strategy { get; set; }
        public string AlternativeCommand { get; set; } = "";
        public int WaitBeforeRetryMs { get; set; } = 1000;
        public string Reason { get; set; } = "";
    }
}
