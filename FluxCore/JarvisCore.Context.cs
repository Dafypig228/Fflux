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
        /// <summary>
        /// Builds ONLY the per-step dynamic context. Static rules/tools/paths
        /// are in _staticInstruction (cached by Gemini across all steps).
        /// </summary>
        private string BuildDynamicContext(string originalGoal, List<string> failures,
            List<string> successes, string activeWindow, int step,
            string clickableElements, List<string> memories, string ragContext = "")
        {
            var sb = new StringBuilder();

            // ── Mid-task context injection (prepended at TOP so the LLM cannot miss it) ──
            // FluxBrain calls InjectMidTaskContext() when the user sends a message while
            // a task is running (INJECT_CTX intent).
            var urgentUpdates = new StringBuilder();
            while (_midTaskContext.TryDequeue(out var ctx))
                urgentUpdates.AppendLine($"[URGENT MID-TASK UPDATE FROM USER]: {ctx}\nAdapt your next steps immediately.");

            if (urgentUpdates.Length > 0)
                sb.Append(urgentUpdates);

            sb.AppendLine($"USER REQUEST: {originalGoal}");
            sb.AppendLine($"ACTIVE WINDOW: {activeWindow} | Step: {step + 1}/30");
            sb.AppendLine("You can SEE the screen and control it.");

            if (memories != null && memories.Any())
            {
                sb.AppendLine("\n⚠ MANDATORY LESSONS (from past failures — YOU MUST FOLLOW THESE):");
                foreach (var m in memories) sb.AppendLine($"  ★ {m}");
            }
            if (successes.Count > 0)
            {
                sb.AppendLine("\n✓ Completed:");
                foreach (var s in successes.TakeLast(8)) sb.AppendLine($"  {s}");
            }
            if (failures.Count > 0)
            {
                sb.AppendLine("\n✗ FAILED (DO NOT repeat — try completely different approach):");
                foreach (var f in failures.TakeLast(5)) sb.AppendLine($"  {f}");
            }
            if (failures.Count >= 3)
            {
                sb.AppendLine("\n💡 ALTERNATIVE STRATEGIES (pick one you haven't tried):");
                sb.AppendLine("  0. If scripts keep failing with 'not found'/'invalid class'/'does not contain a definition' —");
                sb.AppendLine("     the API you invented DOES NOT EXIST. Use ONLY services documented in <grounding>/<tools>.");
                sb.AppendLine("  1. Use [[RUN_SHELL:...]] instead of UI clicks");
                sb.AppendLine("  2. Use [[KEYS:TAB]] + [[KEYS:ENTER]] for keyboard navigation");
                sb.AppendLine("  3. [[SCROLL:down]] to reveal hidden elements");
                sb.AppendLine("  4. [[OPEN_APP:...]] to refocus the correct window");
                sb.AppendLine("  5. If nothing works: [[RESPOND:honest report of what you tried]] then TASK_FAILED");
            }
            if (!string.IsNullOrEmpty(clickableElements))
            {
                sb.AppendLine($"\n{clickableElements}");
            }

            // ── RAG slot: query-relevant long-term memory (3000 chars) ────────────
            if (!string.IsNullOrEmpty(ragContext))
                AppendPassive(sb, $"<rag_memory>\n{Cap(ragContext, 3000)}\n</rag_memory>");

            // ── Passive context: immediate-awareness tier (reduced budgets) ────────
            // Cap()     : hard character limit per source — prevents prompt bloat
            // Untrusted(): wraps external-origin data so LLM never treats it as instructions
            AppendPassive(sb, Cap(Untrusted(Clipboard?.GetRecentClipboard(5),              "clipboard"),   300));
            AppendPassive(sb, Cap(Untrusted(FileWatcher?.GetRecentActivity(10),           "filesystem"),   400));
            AppendPassive(sb, Cap(_cortex?.GetFocusTimeline(60),                                           300));
            AppendPassive(sb, Cap(GitWatcher?.GetGitSummary(),                                             300));
            AppendPassive(sb, Cap(Metrics?.GetMetricsSummary(),                                            150));
            AppendPassive(sb, Cap(Untrusted(Notifications?.GetRecentNotifications(8),     "system"),       300));
            AppendPassive(sb, Cap(Untrusted(ChromeBridge?.GetPageContext(),               "chrome"),       400));
            AppendPassive(sb, Cap(Untrusted(ChromeBridge?.GetVsCodeContext(),             "vscode"),       400));
            AppendPassive(sb, Cap(Untrusted(Telegram?.GetRecentMessages(5),               "telegram"),     400));
            AppendPassive(sb, Cap(Untrusted(TerminalSource?.GetRecentTerminalOutput(3),   "terminal"),     300));
            AppendPassive(sb, Cap(Untrusted(EventLog?.GetRecentErrors(3),                 "eventlog"),     200));
            AppendPassive(sb, Cap(DataLake?.GetRecent("task", 5),                                          400));

            // BATCH REMINDER — reinforce at key points
            if (step <= 2)
                sb.AppendLine("\n⚡ REMINDER: For multi-file operations, use ONE Get-ChildItem pipeline. NEVER repeat Move-Item/Copy-Item for individual files.");

            sb.AppendLine("\nExecute the next action.");
            return sb.ToString();
        }

        /// <summary>
        /// Extracts ALL commands from AI response, in order of appearance.
        /// </summary>
        private List<(string Type, string Arg)> ExtractAllCommands(string text)
        {
            var result = new List<(string Type, string Arg, int Position)>();
            var commandTypes = new[] {
                // Primary (advertised in prompt)
                "CLICK", "TYPE", "KEYS", "SCROLL", "RUN_SHELL", "OPEN_APP", "RESPOND",
                // Scripting & background process management
                "RUN_CSHARP", "CSHARP", "CS",
                "START_BACKGROUND", "READ_LOG", "CHECK_BACKGROUND", "STOP_BACKGROUND",
                // Legacy aliases (not in prompt, still parsed for backward compat)
                "HIDE_SELF", "MINIMIZE_SELF", "POWERSHELL", "PS", "RUN_PYTHON", "PYTHON",
                "WAIT", "LOG", "WINDOW", "DRAG", "DRAGGING",
                "CLICKING", "LAUNCHING", "OPENING", "TYPING",
                "CLICK_TEXT", "BROWSER_TYPE", "BROWSER_OPEN", "PAGE_INFO", "REJECT"
            };

            foreach (var cmdType in commandTypes)
            {
                string pattern = $"[[{cmdType}:";
                int searchStart = 0;

                while (true)
                {
                    // Try with colon first (Argument provided)
                    int start = text.IndexOf(pattern, searchStart);
                    bool hasArg = true;

                    // If not found with colon, try without (Parameterless)
                    if (start < 0)
                    {
                        string simplePattern = $"[[{cmdType}]]";
                        start = text.IndexOf(simplePattern, searchStart);
                        hasArg = false;
                    }

                    if (start < 0) break;

                    if (!hasArg)
                    {
                        result.Add((cmdType, "", start));
                        searchStart = start + cmdType.Length + 4; // [[ + CMD + ]]
                        continue;
                    }

                    int argStart = start + pattern.Length;
                    int end = -1;
                    
                    // SPECIAL: For script commands, content may contain ]] (e.g., Python 2D arrays, C# generics)
                    // Strategy: prefer ]] followed by command markers over ]] followed by plain newline.
                    // For plain ]]\n, use the LAST match (closest to actual command end), not the first.
                    bool isScript = cmdType == "RUN_SHELL" || cmdType == "POWERSHELL" || cmdType == "PS" ||
                                    cmdType == "RUN_PYTHON" || cmdType == "PYTHON" || cmdType == "TYPE" ||
                                    cmdType == "RUN_CSHARP" || cmdType == "CSHARP" || cmdType == "CS" ||
                                    cmdType == "START_BACKGROUND";
                    if (isScript)
                    {
                        // BOUNDARY: a script arg can never legitimately contain another
                        // [[COMMAND: opener. Without this bound, the "last ]] before
                        // newline" fallback could swallow every FOLLOWING command into the
                        // script's argument — and those commands were ALSO parsed in their
                        // own pass, so the script ran corrupted AND the commands ran twice.
                        int boundary = text.Length;
                        foreach (var t in commandTypes)
                        {
                            int pos = text.IndexOf($"[[{t}:", argStart);
                            if (pos >= 0 && pos < boundary) boundary = pos;
                        }

                        int bestEnd = -1;
                        int searchPos = argStart;
                        while (searchPos < boundary)
                        {
                            int candidate = text.IndexOf("]]", searchPos);
                            if (candidate < 0 || candidate >= boundary) break;
                            int after = candidate + 2;

                            if (after >= text.Length)
                            {
                                bestEnd = candidate;
                                break; // End of text = definitive
                            }

                            string remainder = text.Substring(after).TrimStart();
                            // HIGH confidence: followed by known command markers
                            if (remainder.StartsWith("[[") ||
                                remainder.StartsWith("ACTION:") ||
                                remainder.StartsWith("THOUGHT:") ||
                                remainder.StartsWith("CONFIDENCE") ||
                                remainder.StartsWith("TASK_COMPLETE") ||
                                remainder.StartsWith("TASK_FAILED"))
                            {
                                bestEnd = candidate;
                                break; // Definitive match
                            }

                            // LOW confidence: followed by newline (could be inside code like
                            // Python 2D arrays) — keep updating; use the LAST ]]\n before the boundary
                            if (text[after] == '\n' || text[after] == '\r')
                            {
                                bestEnd = candidate;
                            }

                            searchPos = candidate + 1;
                        }

                        // Malformed: no closing ]] before the next command opener —
                        // take everything up to it as the arg instead of dropping the command
                        if (bestEnd < 0 && boundary < text.Length)
                        {
                            result.Add((cmdType, text.Substring(argStart, boundary - argStart).Trim(), start));
                            searchStart = boundary;
                            continue;
                        }
                        end = bestEnd;
                    }
                    else
                    {
                        end = text.IndexOf("]]", argStart);
                    }
                    
                    if (end < 0)
                    {
                        searchStart = argStart;
                        continue;
                    }

                    string arg = text.Substring(argStart, end - argStart).Trim();
                    result.Add((cmdType, arg, start));
                    searchStart = end + 2;
                }
            }

            // Sort by position in text (execute in order they appear)
            return result.OrderBy(c => c.Position)
                         .Select(c => (c.Type, c.Arg))
                         .ToList();
        }

        // ── Context helpers ────────────────────────────────────────────────────────

        /// <summary>Hard-caps a string to maxChars characters.</summary>
        private static string Cap(string? s, int maxChars) =>
            string.IsNullOrEmpty(s) ? "" :
            s.Length <= maxChars ? s : s[..maxChars] + "\n  […truncated]";

        /// <summary>
        /// Wraps external-origin content in an XML tag that tells the LLM
        /// the content is untrusted data — never instructions to follow.
        /// </summary>
        private static string Untrusted(string? content, string source) =>
            string.IsNullOrEmpty(content) ? "" :
            $"<external_data source=\"{source}\" trusted=\"false\">\n{content}\n</external_data>";

        /// <summary>Appends a passive context block only if it has content.</summary>
        private static void AppendPassive(StringBuilder sb, string block)
        {
            if (!string.IsNullOrEmpty(block)) sb.AppendLine($"\n{block}");
        }

        // PredictScreenRequirement removed — no keyword arrays.
        // Screen access is always allowed; the AI dynamically decides which tools to use.

        /// <summary>
        /// Request screen access from user if needed.
        /// </summary>
        private async Task<bool> RequestScreenAccessIfNeededAsync(string reason)
        {
            if (!_smartModeEnabled) return true; // Always allow if smart mode disabled
            if (_screenAccessGranted) return true; // Already granted for this session

            if (_requestScreenAccess != null)
            {
                _logToUI($"[🖥️] Requesting screen access: {reason}");
                _screenAccessGranted = await _requestScreenAccess(reason);

                if (_screenAccessGranted)
                    _logToUI("[✓] Screen access granted");
                else
                    _logToUI("[✗] Screen access denied - will use background-only operations");

                return _screenAccessGranted;
            }

            // No callback set, assume access granted
            return true;
        }

        /// <summary>
        /// Reset screen access for new task.
        /// </summary>
        public void ResetScreenAccess()
        {
            _screenAccessGranted = false;
        }
    }
}
