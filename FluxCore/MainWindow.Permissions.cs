using Application = System.Windows.Application;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;
using Clipboard = System.Windows.Clipboard;

using FluxCore;
using FluxCore.LLM;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Runtime.InteropServices;
using System.ComponentModel;

namespace FluxCore
{
    public partial class MainWindow
    {
        // =========================================
        // PERMISSION DIALOG SYSTEM
        // =========================================

        // Screen Access Permission (for Smart Mode)
        private TaskCompletionSource<bool>? _screenAccessResult;

        private void Btn_PermissionAllow_Click(object sender, RoutedEventArgs e)
        {
            PermissionOverlay.Visibility = Visibility.Collapsed;
            _permissionResult?.TrySetResult(true);
            _screenAccessResult?.TrySetResult(true);
        }

        private void Btn_PermissionDeny_Click(object sender, RoutedEventArgs e)
        {
            PermissionOverlay.Visibility = Visibility.Collapsed;
            _permissionResult?.TrySetResult(false);
            _screenAccessResult?.TrySetResult(false);
        }

        /// <summary>
        /// Shows screen access permission dialog for Smart Mode.
        /// Called when AI needs to use screen-based commands.
        /// </summary>
        private async Task<bool> RequestScreenAccessAsync(string reason)
        {
            _screenAccessResult = new TaskCompletionSource<bool>();

            await Dispatcher.InvokeAsync(() =>
            {
                PermissionActionText.Text = "🖥️ Screen Access Required:";
                PermissionDetailsText.Text = $"{reason}\n\nAllow Flux to view and interact with your screen?";
                PermissionOverlay.Visibility = Visibility.Visible;

                // Make window visible if hidden
                if (this.Opacity < 0.5)
                    this.Opacity = 1;
            });

            return await _screenAccessResult.Task;
        }

        /// <summary>
        /// Generic confirmation dialog — reused by FluxBrain (intent gate) and JarvisCore (destructive gate).
        /// Reuses the existing PermissionOverlay UI.
        /// </summary>
        private async Task<bool> RequestConfirmationAsync(string question)
        {
            _permissionResult = new TaskCompletionSource<bool>();
            await Dispatcher.InvokeAsync(() =>
            {
                PermissionActionText.Text = "🤔 Confirm:";
                PermissionDetailsText.Text = question;
                PermissionOverlay.Visibility = Visibility.Visible;
                if (this.Opacity < 0.5) this.Opacity = 1;
            });
            return await _permissionResult.Task;
        }

        /// <summary>
        /// Shows permission dialog and waits for user response.
        /// </summary>
        private string _expectedTarget = ""; // Track what app we expect to be focused

        private async Task<bool> RequestPermissionAsync(string actionType, string target)
        {
            _pendingAction = actionType;
            _pendingTarget = target;
            _permissionResult = new TaskCompletionSource<bool>();

            Dispatcher.Invoke(() =>
            {
                PermissionActionText.Text = $"Flux wants to {actionType}:";
                PermissionDetailsText.Text = target;
                PermissionOverlay.Visibility = Visibility.Visible;
            });

            return await _permissionResult.Task;
        }

        /// <summary>
        /// Executes actions with permission. Handles MULTIPLE commands in sequence.
        /// </summary>
        private async Task<string> ExecuteWithPermissionAsync(string fullResponse, bool skipPermission = false)
        {
            if (_executor == null) _executor = new ExecutionAgent();
            if (_automation == null) _automation = new WindowsAutomationAgent();

            var results = new List<string>();

            // Extract all commands from response
            var commands = ExtractAllCommands(fullResponse);

            if (commands.Count == 0)
                return "";

            // Ask permission once for all commands (UNLESS skipped)
            if (!skipPermission)
            {
                string summary = string.Join(", ", commands.Take(5).Select(c => $"{c.Type}:{c.Arg}"));
                if (commands.Count > 5) summary += $" (+{commands.Count - 5} more)";

                if (!await RequestPermissionAsync($"execute {commands.Count} action(s)", summary))
                    return "[Permission denied]";
            }

            // Execute each command in sequence
            // Execute each command in sequence
            foreach (var originalCmd in commands)
            {
                // Map aliases to standard types
                var cmd = originalCmd;
                if (cmd.Type == "Typing") cmd = ("TYPE", cmd.Arg);
                if (cmd.Type == "Launching" || cmd.Type == "Opening") cmd = ("OPEN_APP", cmd.Arg);
                if (cmd.Type == "Clicking") cmd = ("CLICK", cmd.Arg);
                if (cmd.Type == "Writing") cmd = ("WRITE_FILE", cmd.Arg);
                if (cmd.Type == "Keys") cmd = ("KEYS", cmd.Arg);

                try
                {
                    string result = "";

                    switch (cmd.Type)
                    {
                        case "OPEN_APP":
                            // Capture current window
                            string oldTitle = _cortex?.GetActiveWindow() ?? "";

                            var appResult = await _executor.OpenApp(cmd.Arg);
                            result = appResult.Success ? appResult.Message : $"Error: {appResult.Message}";

                            // Track what we expect to be focused
                            if (appResult.Success)
                            {
                                 _expectedTarget = cmd.Arg.ToLower();
                                 if (_expectedTarget.Contains("chrome")) _expectedTarget = "chrome";
                                 if (_expectedTarget.Contains("telegram")) _expectedTarget = "telegram";
                                 if (_expectedTarget.Contains("instagram")) _expectedTarget = "instagram"; ///chrome usually
                            }

                            // Smart Wait: Poll until active window changes (max 8s)
                            // Optimization: If we just focused an existing app, the title might NOT change.
                            // So we check if the result message says "Focused existing".
                            bool justFocused = appResult.Message.Contains("Focused existing");

                            if (appResult.Success)
                            {
                                int waited = 0;
                                int maxWait = justFocused ? 2000 : 8000; // Wait less if just focusing

                                while (waited < maxWait)
                                {
                                    await Task.Delay(500);
                                    waited += 500;
                                    string newTitle = _cortex?.GetActiveWindow() ?? "";

                                    // If title changed OR we are just focusing and the title ALREADY contains the target
                                    if (newTitle != oldTitle && !string.IsNullOrEmpty(newTitle) && !newTitle.Contains("Flux"))
                                    {
                                        System.Diagnostics.Debug.WriteLine($"[FLUX] Window switched to: {newTitle}");
                                        break;
                                    }

                                    // If we focused existing, and the CURRENT title is already correct, stop waiting
                                    if (justFocused && !string.IsNullOrEmpty(newTitle) && newTitle.ToLower().Contains(_expectedTarget))
                                    {
                                         break;
                                    }
                                }
                            }

                            else
                            {
                                await Task.Delay(1000); // Wait a bit on error
                            }

                            // Reset automation target to find the new window
                            _automation = new WindowsAutomationAgent();
                            break;

                        case "LOG":
                            Dispatcher.Invoke(() => LogMessage($"[🧠] {cmd.Arg}"));
                            result = "Logged thought.";
                            break;

                        case "READ_FILE":
                            var readResult = await _executor.ReadFileAsync(cmd.Arg);
                            result = readResult.Success ? readResult.Message : $"Error: {readResult.Message}";
                            break;

                        case "OPEN_URL":
                            var urlResult = await _executor.OpenUrlAsync(cmd.Arg);
                            result = urlResult.Success ? urlResult.Message : $"Error: {urlResult.Message}";
                            break;

                        case "CLICK":
                            // STRICT SAFETY CHECK
                            if (!string.IsNullOrEmpty(_expectedTarget))
                            {
                                string current = _cortex?.GetActiveWindow()?.ToLower() ?? "";
                                string proc = _cortex?.GetActiveProcessName()?.ToLower() ?? "";

                                bool isMatch = current.Contains(_expectedTarget) ||
                                               proc.Contains(_expectedTarget) ||
                                               (_expectedTarget == "chrome" && (proc == "chrome" || current.Contains("google") || current.Contains("new tab") || current.Contains("start page") || current.Contains("search"))) ||
                                               (_expectedTarget == "instagram" && (proc == "chrome" || current.Contains("chrome")));

                                if (!isMatch && !string.IsNullOrEmpty(current) && !current.Contains("flux"))
                                {
                                    result = $"[SAFETY STOP] Wrong Window! Expected: '{_expectedTarget}'. Actual: '{current}'.";
                                    Dispatcher.Invoke(() => LogMessage(result));
                                    results.Add(result); // Add to final result
                                    goto StopExecution;
                                }
                            }
                            var clickResult = await _automation.ClickElementAsync(cmd.Arg);
                            result = clickResult.Success ? clickResult.Message : $"Error: {clickResult.Message}";
                            break;

                        case "TYPE":
                            // STRICT SAFETY CHECK
                            if (!string.IsNullOrEmpty(_expectedTarget))
                            {
                                string current = _cortex?.GetActiveWindow()?.ToLower() ?? "";
                                string proc = _cortex?.GetActiveProcessName()?.ToLower() ?? "";

                                bool isMatch = current.Contains(_expectedTarget) ||
                                               proc.Contains(_expectedTarget) ||
                                               (_expectedTarget == "chrome" && (proc == "chrome" || current.Contains("google") || current.Contains("new tab") || current.Contains("start page") || current.Contains("search"))) ||
                                               (_expectedTarget == "instagram" && (proc == "chrome" || current.Contains("chrome")));

                                if (!isMatch && !string.IsNullOrEmpty(current) && !current.Contains("flux"))
                                {
                                    result = $"[SAFETY STOP] Wrong Window! Expected: '{_expectedTarget}'. Actual: '{current}'.";
                                    Dispatcher.Invoke(() => LogMessage(result));
                                    results.Add(result); // Add to final result
                                    // Stop executing subsequent commands
                                    goto StopExecution;
                                }
                            }

                            var typeResult = await _automation.TypeTextAsync("", cmd.Arg);
                            result = typeResult.Success ? typeResult.Message : $"Error: {typeResult.Message}";
                            await Task.Delay(300); // Wait for UI to process typed text
                            break;

                        case "KEYS":
                            var keysResult = await _automation.SendKeysAsync(cmd.Arg);
                            result = keysResult.Success ? keysResult.Message : $"Error: {keysResult.Message}";

                            // Smart delays based on what key was pressed
                            if (cmd.Arg.ToUpper().Contains("WIN"))
                            {
                                await Task.Delay(800); // Wait for Start Menu
                            }
                            else if (cmd.Arg.ToUpper() == "ENTER")
                            {
                                // ENTER after typing = app launch - wait for it to open!
                                await Task.Delay(2000); // Wait 2 seconds for app to fully open
                                _automation = new WindowsAutomationAgent(); // Reset to target new window
                            }
                            else
                            {
                                await Task.Delay(200);
                            }
                            break;

                        // === CODE EXECUTION COMMANDS ===
                        case "WRITE_FILE":
                            if (_codeRunner == null) _codeRunner = new CodeExecutionAgent();
                            // Format: [[WRITE_FILE:path|content]]
                            var writeParts = cmd.Arg.Split(new[] { '|' }, 2);
                            if (writeParts.Length < 2)
                            {
                                result = "Error: WRITE_FILE format is [[WRITE_FILE:path|content]]";
                            }
                            else
                            {
                                var writeResult = await _codeRunner.WriteFileAsync(writeParts[0].Trim(), writeParts[1]);
                                result = writeResult.Success ? writeResult.Message : $"Error: {writeResult.Message}";
                            }
                            break;

                        case "RUN_PYTHON":
                            if (_codeRunner == null) _codeRunner = new CodeExecutionAgent();
                            var pyResult = await _codeRunner.RunPythonAsync(cmd.Arg);
                            result = pyResult.Success ? $"Python output:\n{pyResult.Message}" : $"Error: {pyResult.Message}";
                            break;

                        case "RUN_SHELL":
                            if (_codeRunner == null) _codeRunner = new CodeExecutionAgent();
                            var shellResult = await _codeRunner.RunPowerShellAsync(cmd.Arg);
                            result = shellResult.Success ? $"Shell output:\n{shellResult.Message}" : $"Error: {shellResult.Message}";
                            break;

                        case "RUN_NODE":
                            if (_codeRunner == null) _codeRunner = new CodeExecutionAgent();
                            var nodeResult = await _codeRunner.RunNodeAsync(cmd.Arg);
                            result = nodeResult.Success ? $"Node.js output:\n{nodeResult.Message}" : $"Error: {nodeResult.Message}";
                            break;

                        case "CLIPBOARD":
                            if (_codeRunner == null) _codeRunner = new CodeExecutionAgent();
                            var clipResult = _codeRunner.SetClipboard(cmd.Arg);
                            result = clipResult.Success ? clipResult.Message : $"Error: {clipResult.Message}";
                            break;

                        case "DOWNLOAD_FILE":
                            if (_codeRunner == null) _codeRunner = new CodeExecutionAgent();
                            var dlParts = cmd.Arg.Split(new[] { '|' }, 2);
                            if (dlParts.Length < 2)
                                result = "Error: Format is [[DOWNLOAD_FILE:url|path]]";
                            else
                            {
                                var dlResult = await _codeRunner.DownloadFileAsync(dlParts[0].Trim(), dlParts[1].Trim());
                                result = dlResult.Success ? dlResult.Message : $"Error: {dlResult.Message}";
                            }
                            break;

                        case "SCROLL":
                            var scrollResult = await _automation.ScrollAsync(cmd.Arg);
                            result = scrollResult.Success ? scrollResult.Message : $"Error: {scrollResult.Message}";
                            break;

                        case "WINDOW":
                            var winResult = await _automation.WindowControlAsync(cmd.Arg);
                            result = winResult.Success ? winResult.Message : $"Error: {winResult.Message}";
                            break;
                    }

                    // === VALIDATOR: Check if action was successful ===
                    if (!string.IsNullOrEmpty(result))
                    {
                        if (_validator == null && _gemini != null)
                            _validator = new ValidatorAgent(_gemini);

                        if (_validator != null)
                        {
                            var validation = await _validator.ValidateAsync(cmd.Arg, cmd.Type, result);

                            if (!validation.Success)
                            {
                                result += $" [VALIDATOR: {validation.Message}]";

                                // If should retry and we haven't retried yet, mark for attention
                                if (validation.ShouldRetry)
                                {
                                    result += " [RETRY RECOMMENDED]";
                                }
                            }
                            else
                            {
                                result += $" ✓";
                            }
                        }

                        results.Add(result);
                    }
                }
                catch (Exception ex)
                {
                    results.Add($"Error in {cmd.Type}: {ex.Message}");
                }
            }

            StopExecution:
            return string.Join("\n", results);
        }

        /// <summary>
        /// Extracts all [[COMMAND:arg]] from text in order of appearance.
        /// </summary>
        private List<(string Type, string Arg)> ExtractAllCommands(string text)
        {
            var commands = new List<(int Position, string Type, string Arg)>();
            var commandTypes = new[] {
                "OPEN_APP", "READ_FILE", "OPEN_URL", "CLICK", "TYPE", "KEYS",
                "WRITE_FILE", "RUN_PYTHON", "RUN_SHELL", "RUN_NODE",
                "CLIPBOARD", "DOWNLOAD_FILE", "SCROLL", "WINDOW", "LOG",
                "Typing", "Launching", "Opening", "Clicking", "Writing", "Keys"
            };

            // Multiline commands that can contain newlines
            var multilineCommands = new HashSet<string> { "WRITE_FILE", "RUN_PYTHON", "RUN_SHELL", "RUN_NODE" };

            // Find ALL commands with their positions
            foreach (var cmdType in commandTypes)
            {
                string pattern = $"[[{cmdType}:";
                int searchStart = 0;

                while (true)
                {
                    int start = text.IndexOf(pattern, searchStart);
                    if (start < 0) break;

                    int argStart = start + pattern.Length;
                    int end = text.IndexOf("]]", argStart);

                    string arg;
                    if (end > argStart)
                    {
                        arg = text.Substring(argStart, end - argStart);
                        searchStart = end + 2;
                    }
                    else
                    {
                        // No closing ]] - for multiline, take until next [[ or end
                        // For single-line, take until newline
                        if (multilineCommands.Contains(cmdType))
                        {
                            // Find next command or end
                            int nextCmd = -1;
                            foreach (var type in commandTypes)
                            {
                                int pos = text.IndexOf($"[[{type}:", argStart);
                                if (pos > argStart && (nextCmd < 0 || pos < nextCmd))
                                    nextCmd = pos;
                            }

                            if (nextCmd > argStart)
                                arg = text.Substring(argStart, nextCmd - argStart).TrimEnd();
                            else
                                arg = text.Substring(argStart).TrimEnd();
                        }
                        else
                        {
                            int newline = text.IndexOf('\n', argStart);
                            arg = newline > argStart ? text.Substring(argStart, newline - argStart) : text.Substring(argStart);
                        }
                        searchStart = argStart + arg.Length;
                    }

                    // Store position for sorting
                    commands.Add((start, cmdType, arg.Trim()));
                }
            }

            // Sort by position in original text
            return commands.OrderBy(c => c.Position).Select(c => (c.Type, c.Arg)).ToList();
        }

        private string ExtractCommandArg(string text, string commandName)
        {
            string pattern = $"[[{commandName}:";
            int start = text.IndexOf(pattern);
            if (start < 0) return "";
            start += pattern.Length;

            // Try to find closing ]]
            int end = text.IndexOf("]]", start);

            // If no closing brackets, take until newline or end of string
            if (end < 0)
            {
                int newline = text.IndexOf('\n', start);
                end = newline > 0 ? newline : text.Length;
            }

            return text.Substring(start, end - start).Trim();
        }
    }
}
