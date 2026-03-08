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

            // CLICK already retries 3x internally via ClickWithRetryAsync — don't double-retry
            int maxRetries = (cmdType.ToUpper() == "CLICK") ? 1 : MAX_RETRIES;

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

                    // Safety stop — refocus window
                    if (result.Message.Contains("SAFETY STOP"))
                    {
                        await _executor.OpenApp(GetExpectedApp(cmdType, cmdArg));
                        await Task.Delay(100);
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

            // Apply retry strategies
            if (retryCount > 0)
            {
                // Strategy for retries:
                // 1. Click: Try coordinates if text selector failed
                // 2. Type: Try clipboard paste instead of keyboard
                // 3. Open: Try alternative app names

                if (cmdType == "CLICK" && retryCount == 1)
                {
                    // Try scrolling first, then retry
                    await _automation.ScrollAsync("down");
                    await Task.Delay(150);
                }
                else if (cmdType == "CLICK" && retryCount == 2)
                {
                    // REMOVED dangerous Tab+Enter fallback which opens Explorer!
                    // If click fails twice, just report failure so AI can try coordinates.
                    return new ExecutionResult(false, "Click failed (element not found)");
                }
                else if (cmdType == "TYPE" && retryCount > 0)
                {
                    // Focus the active window by clicking its center (not hardcoded coords)
                    IntPtr fg = GetForegroundWindow();
                    if (fg != IntPtr.Zero)
                    {
                        SetForegroundWindow(fg);
                        await Task.Delay(50);
                    }
                }
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
                    var clickRes = await _automation.ClickElementAsync(cmdArg);
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

                case "RESPOND":
                    _logToUI($"[message] {cmdArg}");
                    OnResponse?.Invoke(cmdArg);
                    return new ExecutionResult(true, $"Responded to user: {cmdArg.Substring(0, Math.Min(50, cmdArg.Length))}...");

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
            // Infer expected app from command context
            if (cmdArg.ToLower().Contains("instagram")) return "chrome";
            if (cmdArg.ToLower().Contains("telegram")) return "telegram";
            if (cmdArg.ToLower().Contains("notepad")) return "notepad";
            if (cmdArg.ToLower().Contains("cmd") || cmdArg.ToLower().Contains("command")) return "cmd";
            if (cmdArg.ToLower().Contains("powershell")) return "powershell";
            return "chrome"; // default
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
