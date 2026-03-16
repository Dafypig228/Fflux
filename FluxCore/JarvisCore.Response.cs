using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FluxCore.LLM;

namespace FluxCore
{
    public partial class JarvisCore
    {
        private async Task PerformReflexionAsync(string goal, List<ChatMessage> history, bool success)
        {
            try
            {
                string prompt = $@"You are the Reflexion Module. Task: '{goal}'. Outcome: {(success ? "SUCCESS" : "FAILURE")}.

Extract 1-3 lessons from this task execution. Each lesson should be:
- A GENERAL rule (not specific to this exact task)
- Actionable for future similar tasks
- Under 80 characters

Format EXACTLY (each lesson separated by ---):
TRIGGER: <one keyword that should recall this lesson>
LESSON: <the rule>
---
TRIGGER: <keyword>
LESSON: <the rule>
---";

                string response = await _llm.ChatWithHistory(
                    history,
                    "Reflect on this task and extract lessons.",
                    "", "", "",
                    prompt,
                    0.3f  // moderate temperature for reflexion
                );

                if (string.IsNullOrWhiteSpace(response)) return;

                // Parse multiple TRIGGER/LESSON pairs
                var blocks = response.Split("---", StringSplitOptions.RemoveEmptyEntries);
                int lessonCount = 0;

                foreach (var block in blocks)
                {
                    string trigger = "", lesson = "";
                    foreach (var line in block.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("TRIGGER:", StringComparison.OrdinalIgnoreCase))
                            trigger = trimmed.Substring(8).Trim().ToLower();
                        else if (trimmed.StartsWith("LESSON:", StringComparison.OrdinalIgnoreCase))
                            lesson = trimmed.Substring(7).Trim();
                    }

                    if (!string.IsNullOrWhiteSpace(trigger) && !string.IsNullOrWhiteSpace(lesson) && lesson.Length > 10)
                    {
                        _logToUI($"[🎓] Learned: [{trigger}] {lesson}");
                        await _memory.LearnStructuredAsync(trigger, lesson, success);
                        lessonCount++;
                        if (lessonCount >= 3) break; // Cap at 3 lessons
                    }
                }

                // Fallback: if no structured lessons parsed, try the old single-lesson approach
                if (lessonCount == 0)
                {
                    string lesson = response.Replace("Lesson:", "").Replace("TRIGGER:", "").Replace("LESSON:", "").Trim();
                    if (lesson.Length > 10)
                    {
                        string trigger = goal.ToLower().Replace("open", "").Replace("please", "").Trim().Split(' ')[0];
                        if (string.IsNullOrEmpty(trigger)) trigger = "general";
                        _logToUI($"[🎓] Learned: {lesson}");
                        await _memory.LearnStructuredAsync(trigger, lesson, success);
                    }
                }
            }
            catch (Exception ex)
            {
                _logToUI($"[⚠️] Failed to reflect: {ex.Message}");
            }
        }

        /// <summary>
        /// Generates a natural language response for the chat instead of raw logs.
        /// </summary>
        private string GenerateNaturalResponse(string userRequest, List<string> successes, List<string> failures, bool completed, string lastOutput = "")
        {
            var sb = new StringBuilder();

            if (completed && failures.Count == 0)
            {
                // Full success
                sb.Append("Done! ");
                if (successes.Count == 1)
                {
                    // Single action - describe it naturally
                    string action = successes[0].Replace("✓ ", "");
                    sb.Append(DescribeActionNaturally(action));
                }
                else if (successes.Count > 1)
                {
                    // Multiple actions
                    sb.Append($"I completed {successes.Count} actions.");
                }
            }
            else if (completed && failures.Count > 0)
            {
                // Partial success
                sb.Append($"Finished with some issues. ");
                if (successes.Count > 0)
                    sb.Append($"Completed {successes.Count} actions. ");
                sb.Append($"{failures.Count} actions had problems.");
            }
            else
            {
                // Not completed
                if (successes.Count > 0)
                    sb.Append($"Made progress ({successes.Count} actions done) but couldn't finish. ");
                else
                    sb.Append("I couldn't complete this task. ");

                if (failures.Count > 0)
                {
                    // Show last failure reason
                    string lastFail = failures.Last();
                    if (lastFail.Contains("not found"))
                        sb.Append("Couldn't find the element I was looking for.");
                    else if (lastFail.Contains("focus") || lastFail.Contains("window"))
                        sb.Append("Had trouble with window focus.");
                    else
                        sb.Append("Ran into some issues along the way.");
                }
            }

            // If the task produced actual data (RUN_CSHARP result, shell output, etc.), surface it
            if (!string.IsNullOrWhiteSpace(lastOutput))
                sb.Append($"\n{lastOutput}");

            return sb.ToString().Trim();
        }

        /// <summary>
        /// Converts action notation to natural language.
        /// </summary>
        private string DescribeActionNaturally(string action)
        {
            // Parse action like "CLICK:Button" or "TYPE:hello"
            var parts = action.Split(':');
            if (parts.Length < 2) return action;

            string cmd = parts[0].Trim().ToUpper();
            string arg = parts[1].Trim();

            return cmd switch
            {
                "CLICK" => $"Clicked on '{arg}'.",
                "TYPE" => $"Typed the text.",
                "KEYS" => $"Pressed {arg}.",
                "OPEN_APP" => $"Opened {arg}.",
                "SCROLL" => $"Scrolled {arg}.",
                "MOVE_FILE" => $"Moved the file.",
                "LIST_FILES" => $"Listed the files.",
                "POWERSHELL" or "PS" => $"Ran the command.",
                "PYTHON" => $"Ran Python code.",
                _ => $"Did {cmd.ToLower()}."
            };
        }

        /// <summary>
        /// Extracts conversational response from AI output (removes command markers).
        /// </summary>
        private string ExtractConversationalResponse(string aiResponse)
        {
            // If it's a pure conversation (no commands), clean it up
            string response = aiResponse;

            // Remove THOUGHT: prefix if present
            if (response.Contains("THOUGHT:"))
            {
                int start = response.IndexOf("THOUGHT:") + 8;
                int end = response.IndexOf("ACTION:", start);
                if (end == -1) end = response.Length;
                response = response.Substring(start, end - start).Trim();
            }

            // Remove any remaining [[COMMAND:...]] patterns
            response = System.Text.RegularExpressions.Regex.Replace(response, @"\[\[[^\]]+\]\]", "");

            // Remove TASK_COMPLETE markers
            response = response.Replace("TASK_COMPLETE", "").Replace("ACTION:", "").Trim();

            // If response is empty or too short, provide default
            if (string.IsNullOrWhiteSpace(response) || response.Length < 3)
                response = "I'm here. What would you like me to do?";

            return response;
        }
    }
}
