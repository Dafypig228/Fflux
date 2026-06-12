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
        /// Executes a command with automatic retry using different strategies.
        /// </summary>
        private async Task<ExecutionOutcome> ExecuteWithRetryAsync(string cmdType, string cmdArg)
        {
            string lastError = "";
            string upperType = cmdType.ToUpper();

            // Retry policy:
            //   CLICK    — retries internally via ClickWithRetryAsync, don't double-retry
            //   Scripts  — RUN_SHELL/PYTHON/RUN_CSHARP etc. are deterministic: re-running an
            //              identical failing script wastes up to 60s per attempt and can
            //              repeat side effects (file moves, sends). Execute ONCE; the AI
            //              sees the error and writes a corrected script next step.
            //   Other UI — transient focus/timing issues are real, allow retries.
            bool isUiCommand = ScreenCommands.Contains(upperType);
            int maxRetries = (upperType == "CLICK" || upperType == "CLICKING") ? 1
                           : isUiCommand ? MAX_RETRIES
                           : 1;

            for (int retry = 0; retry < maxRetries; retry++)
            {
                try
                {
                    var result = await ExecuteSingleCommandAsync(cmdType, cmdArg, retry);

                    if (result.Success)
                    {
                        return new ExecutionOutcome(true, result.Message);
                    }

                    lastError = result.Message;

                    // Safety stop handling
                    if (result.Message.Contains("SAFETY STOP"))
                    {
                        if (result.Message.Contains("Davos's own window") &&
                            upperType != "SCROLL")
                        {
                            // Hard stop for CLICK/TYPE/KEYS — would corrupt Davos UI. Do not retry.
                            return new ExecutionOutcome(false, "SAFETY STOP: Cannot interact with Davos UI.");
                        }
                        // For SCROLL or transient wrong-focus — refocus the expected app ONLY
                        // when we can infer one from the command. (The old default of "chrome"
                        // made Davos randomly open Chrome in the middle of unrelated tasks.)
                        string expectedApp = GetExpectedApp(cmdType, cmdArg);
                        if (!string.IsNullOrEmpty(expectedApp))
                            await _executor.OpenApp(expectedApp);
                        await Task.Delay(200);
                        continue;
                    }

                    // Element not found — quick retry
                    if (result.Message.Contains("not found") && retry < maxRetries - 1)
                    {
                        await Task.Delay(100);
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }
            }

            return new ExecutionOutcome(false, $"Failed: {lastError}");
        }

        /// <summary>
        /// Executes a single command, with strategy variation based on retry count.
        /// </summary>
        private async Task<ExecutionResult> ExecuteSingleCommandAsync(string cmdType, string cmdArg, int retryCount)
        {
            // SELF-PROTECTION: Never act on our own window
            string upperCmd = cmdType.ToUpper();
            if (upperCmd == "CLICK" || upperCmd == "KEYS" || upperCmd == "TYPE" || upperCmd == "TYPING" || upperCmd == "SCROLL")
            {
                try
                {
                    int myPid = System.Diagnostics.Process.GetCurrentProcess().Id;
                    IntPtr fgWindow = GetForegroundWindow();
                    if (fgWindow != IntPtr.Zero)
                    {
                        GetWindowThreadProcessId(fgWindow, out uint fgPid);
                        if (fgPid == myPid)
                        {
                            // Our own window is focused — DON'T execute, switch away
                            System.Diagnostics.Debug.WriteLine("[SAFETY] Foreground window is FluxCore itself — skipping command to avoid self-destruction");
                            return new ExecutionResult(false, $"SAFETY STOP: Cannot execute {cmdType} on Davos's own window. Wrong window focused.");
                        }
                    }
                }
                catch { /* Best effort check */ }
            }

            // Retry pacing: brief pause lets transient focus/timing issues settle.
            // NOTE: no hidden UI mutations here — the old code scrolled the page on
            // CLICK retries, which silently invalidated every coordinate the AI knew.
            // If scrolling is needed, the AI decides that explicitly via [[SCROLL:...]].
            if (retryCount > 0)
            {
                await Task.Delay(100 * retryCount);
            }

            // Execute the actual command
            switch (cmdType.ToUpper())
            {
                case "OPEN_APP":
                case "LAUNCHING":
                case "OPENING":
                    return await _executor.OpenApp(cmdArg);

                case "TYPE":
                case "TYPING":
                    // TYPE always types. OPEN_APP handles URL launching.
                    var typeRes = await _automation.TypeTextAsync("", cmdArg);
                    return new ExecutionResult(typeRes.Success, typeRes.Message);

                case "KEYS":
                    var keysRes = await _automation.SendKeysAsync(cmdArg);
                    return new ExecutionResult(keysRes.Success, keysRes.Message);

                case "CLICK":
                case "CLICKING":
                    // CLICK:7 = element [7] from the current step's numbered list
                    var clickRes = await _automation.ClickElementAsync(ResolveElementIndex(cmdArg));
                    return new ExecutionResult(clickRes.Success, clickRes.Message);

                case "SCROLL":
                    var scrollRes = await _automation.ScrollAsync(cmdArg);
                    return new ExecutionResult(scrollRes.Success, scrollRes.Message);

                case "DRAG":
                case "DRAGGING":
                    var dragRes = await _automation.DragDropAsync(cmdArg);
                    return new ExecutionResult(dragRes.Success, dragRes.Message);

                case "WINDOW":
                    var winRes = await _automation.WindowControlAsync(cmdArg);
                    return new ExecutionResult(winRes.Success, winRes.Message);

                case "RUN_PYTHON":
                case "PYTHON":
                    return await _codeRunner.RunPythonAsync(cmdArg);

                case "RUN_SHELL":
                case "POWERSHELL":
                case "PS":
                    // Safety handled by confidence gate (DestructiveCommands set + actionConfidence < 0.7)
                    return await _codeRunner.RunPowerShellAsync(cmdArg);

                case "RUN_CSHARP":
                case "CSHARP":
                case "CS":
                    if (_scriptGlobals == null)
                        return new ExecutionResult(false, "ScriptGlobals not initialized — RUN_CSHARP unavailable");
                    return await _codeRunner.RunCSharpAsync(cmdArg, _scriptGlobals);

                case "START_BACKGROUND":
                    return await _codeRunner.StartBackgroundAsync(cmdArg);

                case "READ_LOG":
                    return _codeRunner.ReadLog(cmdArg);

                case "CHECK_BACKGROUND":
                    return _codeRunner.CheckBackground(cmdArg);

                case "STOP_BACKGROUND":
                    return _codeRunner.StopBackground(cmdArg);

                case "REJECT":
                    // Task is outside JarvisCore's domain. This case is a fallback —
                    // REJECT is normally caught at the loop level before command dispatch.
                    return new ExecutionResult(false, $"REJECTED: {cmdArg}");

                case "RESPOND":
                    // Final answer to the user. This command was advertised to the model
                    // and parsed, but had NO executor case — every RESPOND failed with
                    // "Unknown command" and the answer text was silently lost.
                    string answer = cmdArg
                        .Replace("TASK_COMPLETE", "", StringComparison.OrdinalIgnoreCase)
                        .Replace("TASK_FAILED", "", StringComparison.OrdinalIgnoreCase)
                        .Trim();
                    return new ExecutionResult(true, answer);

                case "LOG":
                    // LOG is informative - keep it in Neuro-Hud only
                    // _logToUI($"[📝] {cmdArg}");
                    OnThought?.Invoke(cmdArg);
                    return new ExecutionResult(true, cmdArg);

                case "WAIT":
                    // Wait for specified milliseconds (for page loads)
                    if (int.TryParse(cmdArg, out int waitMs))
                    {
                        waitMs = Math.Min(waitMs, 10000); // Cap at 10 seconds
                        await Task.Delay(waitMs);
                        return new ExecutionResult(true, $"Waited {waitMs}ms");
                    }
                    return new ExecutionResult(false, "Invalid wait time");

                // PLAYWRIGHT - RELIABLE BROWSER COMMANDS
                case "CLICK_TEXT":
                    // Click element by visible text - MUCH more reliable than coordinates!
                    var clickTextRes = await _executor.ClickByTextAsync(cmdArg);
                    return new ExecutionResult(clickTextRes.Success, clickTextRes.Message);

                case "BROWSER_TYPE":
                    // Type into focused element in browser
                    var browserTypeRes = await _executor.TypeIntoFocusedAsync(cmdArg);
                    return new ExecutionResult(browserTypeRes.Success, browserTypeRes.Message);

                case "BROWSER_OPEN":
                    // Open URL in controlled Playwright browser
                    var browserOpenRes = await _executor.OpenUrlAsync(cmdArg);
                    return new ExecutionResult(browserOpenRes.Success, browserOpenRes.Message);

                case "PAGE_INFO":
                    // Get info about current page
                    var pageInfoRes = await _executor.GetPageInfoAsync();
                    return new ExecutionResult(pageInfoRes.Success, pageInfoRes.Message);

                // File ops removed — AI uses [[RUN_SHELL:...]] for all file operations

                case "HIDE_SELF":
                case "MINIMIZE_SELF":
                    // Run on thread pool ensuring we don't block command execution loop
                     await Task.Run(() => _automation.MinimizeFlux());
                    return new ExecutionResult(true, "Flux window minimized.");

                default:
                    return new ExecutionResult(false, $"Unknown command: {cmdType}");
            }
        }

        private string GetExpectedApp(string cmdType, string cmdArg)
        {
            // Infer expected app from command context.
            // Returns "" when nothing can be inferred — callers must NOT refocus then.
            // (The old default of "chrome" opened Chrome during unrelated tasks.)
            string argLower = cmdArg.ToLower();
            if (argLower.Contains("instagram")) return "chrome";
            if (argLower.Contains("telegram")) return "telegram";
            if (argLower.Contains("notepad")) return "notepad";
            if (argLower.Contains("cmd") || argLower.Contains("command")) return "cmd";
            if (argLower.Contains("powershell")) return "powershell";
            return "";
        }

        // P/Invoke for window finding and focus
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        /// <summary>
        /// Finds a window by partial name match.
        /// </summary>
        private IntPtr FindWindowByName(string partialName)
        {
            IntPtr found = IntPtr.Zero;
            var sb = new System.Text.StringBuilder(256);

            EnumWindows((hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd)) return true;

                GetWindowText(hWnd, sb, 256);
                string title = sb.ToString();

                // Skip our own window
                if (title.Contains("Davos", StringComparison.OrdinalIgnoreCase))
                    return true;

                // Match by process name patterns
                bool isMatch = partialName.ToLower() switch
                {
                    "chrome" => title.Contains("Chrome", StringComparison.OrdinalIgnoreCase) ||
                                title.Contains("Google", StringComparison.OrdinalIgnoreCase),
                    "telegram" => title.Contains("Telegram", StringComparison.OrdinalIgnoreCase),
                    "whatsapp" => title.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase),
                    "cmd" => title.Contains("Command Prompt", StringComparison.OrdinalIgnoreCase) ||
                             title.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase),
                    "powershell" => title.Contains("PowerShell", StringComparison.OrdinalIgnoreCase) ||
                                    title.Contains("pwsh", StringComparison.OrdinalIgnoreCase) ||
                                    title.Contains("Windows Terminal", StringComparison.OrdinalIgnoreCase),
                    "notepad" => title.Contains("Notepad", StringComparison.OrdinalIgnoreCase),
                    _ => title.Contains(partialName, StringComparison.OrdinalIgnoreCase)
                };

                if (isMatch)
                {
                    found = hWnd;
                    return false; // Stop enumeration
                }

                return true;
            }, IntPtr.Zero);

            return found;
        }
    }
}
