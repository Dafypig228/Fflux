using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FluxCore.LLM;
using FluxCore.Swarm.Environment;
using FluxCore.Swarm.Infrastructure;

namespace FluxCore.Swarm.Agents
{
    /// <summary>
    /// Visual testing agent that operates in an isolated screen environment (Hyper-V VM or local).
    /// Only ONE ScreenAgent should run at a time to avoid conflicts.
    /// </summary>
    public class ScreenAgent : BaseAgent
    {
        private readonly IScreenEnvironment _screenEnv;
        private readonly ValidatorAgent _validator;
        private readonly ILLMService _llm;
        private readonly int _maxRetries;

        public override AgentType Type => AgentType.Screen;

        public override string[] Capabilities => new[]
        {
            "visual-testing",
            "screenshot",
            "click",
            "double-click",
            "right-click",
            "type",
            "send-keys",
            "scroll",
            "launch-app",
            "window-title",
            "powershell-in-env"
        };

        public ScreenAgent(
            string agentId,
            IScreenEnvironment screenEnvironment,
            IMessageBus messageBus,
            IFileLockManager lockManager,
            IAgentRegistry registry,
            ILLMService llm,
            int maxRetries = 3,
            Action<string>? logToUI = null)
            : base(agentId, messageBus, lockManager, registry, logToUI)
        {
            _screenEnv = screenEnvironment;
            _llm = llm;
            _validator = new ValidatorAgent(llm);
            _maxRetries = maxRetries;
        }

        #region Events for UI

        /// <summary>
        /// Fired when ScreenAgent wants to send a message to the user (for chat UI).
        /// </summary>
        public event EventHandler<string>? MessageToUser;

        /// <summary>
        /// Fired when the current task status changes.
        /// </summary>
        public event EventHandler<string>? StatusChanged;

        /// <summary>
        /// Fired when an action is completed.
        /// </summary>
        public event EventHandler<string>? ActionCompleted;

        /// <summary>
        /// Fired when a new screenshot is captured.
        /// </summary>
        public event EventHandler<string>? ScreenshotCaptured;

        #endregion

        #region Natural Language Chat Handling

        /// <summary>
        /// Handle a natural language message from the user (from the chat UI).
        /// No command syntax needed - just regular conversation.
        /// </summary>
        public async Task<string> HandleUserMessageAsync(string message, CancellationToken ct = default)
        {
            LogToUI($"[{AgentId}] User says: {message}");

            try
            {
                // Get current screenshot for context
                var screenshot = await _screenEnv.CaptureScreenshotAsync(ct);

                // Parse the user's intent using Gemini
                var intent = await ParseUserIntentAsync(message, screenshot, ct);

                if (intent.IsActionRequest)
                {
                    // User wants us to do something
                    var response = await ExecuteNaturalLanguageActionAsync(intent, screenshot, ct);
                    MessageToUser?.Invoke(this, response);
                    return response;
                }
                else
                {
                    // User is asking a question or just chatting
                    var response = await AnswerUserQuestionAsync(message, screenshot, ct);
                    MessageToUser?.Invoke(this, response);
                    return response;
                }
            }
            catch (Exception ex)
            {
                var errorMsg = $"Sorry, I encountered an error: {ex.Message}";
                MessageToUser?.Invoke(this, errorMsg);
                return errorMsg;
            }
        }

        private async Task<UserIntent> ParseUserIntentAsync(string message, string screenshot, CancellationToken ct)
        {
            var prompt = $@"Analyze this user message and determine their intent:

USER MESSAGE: ""{message}""

Determine:
1. Is this an ACTION REQUEST (user wants you to do something like click, type, open, scroll)?
2. Or is this a QUESTION (user wants information or is just chatting)?

If it's an ACTION REQUEST, extract:
- ActionType: click, type, scroll, launch, close, sendkeys, or other
- Target: what to click/type/etc (element description or text)
- Details: any additional context

Respond in this format:
INTENT_TYPE: ACTION or QUESTION
ACTION_TYPE: (if action)
TARGET: (if action)
DETAILS: (if action)
SUMMARY: Brief description of what user wants";

            var response = await _llm.ChatWithHistory(new List<ChatMessage>(), prompt, $"Base64:{screenshot}", "", "");

            // Parse the response
            var intent = new UserIntent { OriginalMessage = message };

            if (response.ToUpper().Contains("INTENT_TYPE: ACTION"))
            {
                intent.IsActionRequest = true;

                // Extract action type
                var actionMatch = System.Text.RegularExpressions.Regex.Match(response, @"ACTION_TYPE:\s*(\w+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (actionMatch.Success)
                    intent.ActionType = actionMatch.Groups[1].Value.ToLower();

                // Extract target
                var targetMatch = System.Text.RegularExpressions.Regex.Match(response, @"TARGET:\s*(.+?)(?:\n|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (targetMatch.Success)
                    intent.Target = targetMatch.Groups[1].Value.Trim();

                // Extract details
                var detailsMatch = System.Text.RegularExpressions.Regex.Match(response, @"DETAILS:\s*(.+?)(?:\n|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (detailsMatch.Success)
                    intent.Details = detailsMatch.Groups[1].Value.Trim();
            }

            // Extract summary
            var summaryMatch = System.Text.RegularExpressions.Regex.Match(response, @"SUMMARY:\s*(.+?)(?:\n|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (summaryMatch.Success)
                intent.Summary = summaryMatch.Groups[1].Value.Trim();

            return intent;
        }

        private async Task<string> ExecuteNaturalLanguageActionAsync(UserIntent intent, string screenshot, CancellationToken ct)
        {
            StatusChanged?.Invoke(this, $"Executing: {intent.Summary}");

            try
            {
                string result;

                switch (intent.ActionType?.ToLower())
                {
                    case "click":
                        var coords = await FindElementAsync(intent.Target ?? "", ct);
                        if (coords.HasValue)
                        {
                            await _screenEnv.ClickAsync(coords.Value.x, coords.Value.y, ct);
                            result = $"Done! I clicked on {intent.Target}.";
                        }
                        else
                        {
                            result = $"I couldn't find '{intent.Target}' on the screen. Could you describe it differently?";
                        }
                        break;

                    case "type":
                        await _screenEnv.TypeTextAsync(intent.Details ?? intent.Target ?? "", ct);
                        result = $"Done! I typed the text.";
                        break;

                    case "scroll":
                        var direction = intent.Details?.ToLower().Contains("up") == true ? -3 : 3;
                        await _screenEnv.ScrollAsync(640, 360, direction, ct);
                        result = $"Done! I scrolled {(direction > 0 ? "down" : "up")}.";
                        break;

                    case "launch":
                        await _screenEnv.LaunchApplicationAsync(intent.Target ?? "", "", ct);
                        result = $"Done! I launched {intent.Target}.";
                        break;

                    case "sendkeys":
                        await _screenEnv.SendKeysAsync(intent.Target ?? "", ct);
                        result = $"Done! I sent the keys.";
                        break;

                    default:
                        result = $"I understood you want me to: {intent.Summary}. Let me try...";
                        // Try to figure out from context
                        break;
                }

                ActionCompleted?.Invoke(this, result);

                // Capture screenshot after action
                var afterScreenshot = await _screenEnv.CaptureScreenshotAsync(ct);
                ScreenshotCaptured?.Invoke(this, afterScreenshot);

                return result;
            }
            catch (Exception ex)
            {
                return $"I tried to {intent.Summary} but encountered an error: {ex.Message}";
            }
        }

        private async Task<string> AnswerUserQuestionAsync(string question, string screenshot, CancellationToken ct)
        {
            var prompt = $@"You are a helpful ScreenAgent assistant. The user is watching your screen and asking:

""{question}""

Look at the current screenshot and answer naturally. Be helpful and conversational.
If they're asking what you see, describe the screen.
If they're asking a general question, answer it.
Keep your response concise but friendly.";

            return await _llm.ChatWithHistory(new List<ChatMessage>(), prompt, $"Base64:{screenshot}", "", "");
        }

        private class UserIntent
        {
            public string OriginalMessage { get; set; } = "";
            public bool IsActionRequest { get; set; }
            public string? ActionType { get; set; }
            public string? Target { get; set; }
            public string? Details { get; set; }
            public string? Summary { get; set; }
        }

        #endregion

        protected override async Task RunLoopAsync(CancellationToken ct)
        {
            LogToUI($"[{AgentId}] Screen agent starting, ensuring environment is ready...");

            try
            {
                await _screenEnv.EnsureRunningAsync(ct);
                LogToUI($"[{AgentId}] Screen environment ready: {_screenEnv.ConnectionInfo}");
            }
            catch (Exception ex)
            {
                LogToUI($"[{AgentId}] Failed to start screen environment: {ex.Message}");
                await SetStateAsync(AgentState.Error);
                return;
            }

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Wait for a visual task from the queue
                    var task = await MessageBus.DequeueTaskAsync(AgentId, Capabilities, ct);

                    if (task == null)
                    {
                        await Task.Delay(100, ct);
                        continue;
                    }

                    // Only accept tasks that require screen access
                    if (!task.RequiresScreenAccess)
                    {
                        LogToUI($"[{AgentId}] Task {task.TaskId} doesn't require screen, re-queuing...");
                        await MessageBus.EnqueueTaskAsync(task, ct);
                        continue;
                    }

                    LogToUI($"[{AgentId}] Received visual task: {task.TaskId} - {task.Description}");
                    await SetStateAsync(AgentState.Working, task.TaskId);

                    var stopwatch = Stopwatch.StartNew();
                    var result = await ExecuteVisualTaskWithRetryAsync(task, ct);
                    stopwatch.Stop();

                    if (result.Success)
                    {
                        await ReportTaskCompletedAsync(
                            task.TaskId,
                            true,
                            result.Output,
                            stopwatch.Elapsed,
                            result.ScreenshotBase64);

                        LogToUI($"[{AgentId}] Task {task.TaskId} completed successfully");
                    }
                    else
                    {
                        await ReportTaskFailedAsync(
                            task.TaskId,
                            result.Error ?? "Unknown error",
                            retryCount: _maxRetries,
                            isPermanent: true,
                            screenshotBase64: result.ScreenshotBase64);

                        LogToUI($"[{AgentId}] Task {task.TaskId} failed: {result.Error}");
                    }

                    await SetStateAsync(AgentState.Idle);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogToUI($"[{AgentId}] Error in run loop: {ex.Message}");
                    await Task.Delay(1000, ct);
                }
            }

            LogToUI($"[{AgentId}] Screen agent loop ended");
        }

        private async Task<AgentExecutionResult> ExecuteVisualTaskWithRetryAsync(AgentTask task, CancellationToken ct)
        {
            AgentExecutionResult? lastResult = null;
            string? lastScreenshot = null;

            for (int attempt = 0; attempt < _maxRetries; attempt++)
            {
                if (attempt > 0)
                {
                    LogToUI($"[{AgentId}] Retry {attempt}/{_maxRetries} for task {task.TaskId}");
                    await Task.Delay(500, ct); // Brief delay before retry
                }

                // Capture screenshot before action
                string beforeScreenshot;
                try
                {
                    beforeScreenshot = await _screenEnv.CaptureScreenshotAsync(ct);
                }
                catch (Exception ex)
                {
                    return new AgentExecutionResult
                    {
                        Success = false,
                        Error = $"Failed to capture before screenshot: {ex.Message}"
                    };
                }

                // Execute the visual task
                var result = await ExecuteVisualTaskAsync(task, ct);

                // Brief delay for UI to settle
                await Task.Delay(300, ct);

                // Capture screenshot after action
                string afterScreenshot;
                try
                {
                    afterScreenshot = await _screenEnv.CaptureScreenshotAsync(ct);
                    lastScreenshot = afterScreenshot;
                }
                catch (Exception ex)
                {
                    return new AgentExecutionResult
                    {
                        Success = false,
                        Error = $"Failed to capture after screenshot: {ex.Message}",
                        ScreenshotBase64 = beforeScreenshot
                    };
                }

                // If the action itself failed, report immediately
                if (!result.Success)
                {
                    lastResult = result;
                    lastResult.ScreenshotBase64 = afterScreenshot;
                    continue; // Try again
                }

                // Validate the result visually
                if (task.RequiresVisualVerification)
                {
                    var validation = await _validator.ValidateVisualAsync(
                        task.Description,
                        beforeScreenshot,
                        afterScreenshot);

                    if (validation.Success)
                    {
                        return new AgentExecutionResult
                        {
                            Success = true,
                            Output = result.Output + $"\nValidation: {validation.Message}",
                            ScreenshotBase64 = afterScreenshot,
                            ValidationResult = validation,
                            Duration = result.Duration
                        };
                    }

                    if (!validation.ShouldRetry)
                    {
                        return new AgentExecutionResult
                        {
                            Success = false,
                            Error = $"Visual validation failed: {validation.Message}",
                            ScreenshotBase64 = afterScreenshot,
                            ValidationResult = validation
                        };
                    }

                    // Validation says retry
                    lastResult = new AgentExecutionResult
                    {
                        Success = false,
                        Error = validation.Message,
                        ScreenshotBase64 = afterScreenshot,
                        ValidationResult = validation
                    };
                    continue;
                }

                // No visual verification needed, action succeeded
                return new AgentExecutionResult
                {
                    Success = true,
                    Output = result.Output,
                    ScreenshotBase64 = afterScreenshot,
                    Duration = result.Duration
                };
            }

            // All retries exhausted
            return lastResult ?? new AgentExecutionResult
            {
                Success = false,
                Error = "All retries exhausted",
                ScreenshotBase64 = lastScreenshot
            };
        }

        private async Task<AgentExecutionResult> ExecuteVisualTaskAsync(AgentTask task, CancellationToken ct)
        {
            var stopwatch = Stopwatch.StartNew();

            try
            {
                string output = task.CommandType.ToUpper() switch
                {
                    "CLICK" => await ExecuteClickAsync(task, ct),
                    "DOUBLE_CLICK" => await ExecuteDoubleClickAsync(task, ct),
                    "RIGHT_CLICK" => await ExecuteRightClickAsync(task, ct),
                    "TYPE" => await ExecuteTypeAsync(task, ct),
                    "SEND_KEYS" or "KEYS" => await ExecuteSendKeysAsync(task, ct),
                    "SCROLL" => await ExecuteScrollAsync(task, ct),
                    "LAUNCH_APP" or "OPEN_APP" => await ExecuteLaunchAppAsync(task, ct),
                    "SCREENSHOT" => await ExecuteScreenshotAsync(ct),
                    "WINDOW_TITLE" => await ExecuteGetWindowTitleAsync(ct),
                    "POWERSHELL" => await ExecutePowerShellAsync(task, ct),
                    "WAIT" => await ExecuteWaitAsync(task, ct),
                    _ => throw new InvalidOperationException($"Unknown visual command: {task.CommandType}")
                };

                stopwatch.Stop();
                return new AgentExecutionResult
                {
                    Success = true,
                    Output = output,
                    Duration = stopwatch.Elapsed
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return new AgentExecutionResult
                {
                    Success = false,
                    Error = ex.Message,
                    Duration = stopwatch.Elapsed
                };
            }
        }

        private async Task<string> ExecuteClickAsync(AgentTask task, CancellationToken ct)
        {
            var coords = ParseCoordinates(task.CommandArgs);
            await _screenEnv.ClickAsync(coords.x, coords.y, ct);
            return $"Clicked at ({coords.x}, {coords.y})";
        }

        private async Task<string> ExecuteDoubleClickAsync(AgentTask task, CancellationToken ct)
        {
            var coords = ParseCoordinates(task.CommandArgs);
            await _screenEnv.DoubleClickAsync(coords.x, coords.y, ct);
            return $"Double-clicked at ({coords.x}, {coords.y})";
        }

        private async Task<string> ExecuteRightClickAsync(AgentTask task, CancellationToken ct)
        {
            var coords = ParseCoordinates(task.CommandArgs);
            await _screenEnv.RightClickAsync(coords.x, coords.y, ct);
            return $"Right-clicked at ({coords.x}, {coords.y})";
        }

        private async Task<string> ExecuteTypeAsync(AgentTask task, CancellationToken ct)
        {
            var text = task.CodeToExecute ?? task.CommandArgs;
            await _screenEnv.TypeTextAsync(text, ct);
            return $"Typed: {text}";
        }

        private async Task<string> ExecuteSendKeysAsync(AgentTask task, CancellationToken ct)
        {
            await _screenEnv.SendKeysAsync(task.CommandArgs, ct);
            return $"Sent keys: {task.CommandArgs}";
        }

        private async Task<string> ExecuteScrollAsync(AgentTask task, CancellationToken ct)
        {
            // Format: x,y,deltaY or just deltaY (use center of screen)
            var parts = task.CommandArgs.Split(',');
            int x, y, deltaY;

            if (parts.Length >= 3)
            {
                x = int.Parse(parts[0].Trim());
                y = int.Parse(parts[1].Trim());
                deltaY = int.Parse(parts[2].Trim());
            }
            else
            {
                // Default to center of screen
                x = 960;
                y = 540;
                deltaY = int.Parse(parts[0].Trim());
            }

            await _screenEnv.ScrollAsync(x, y, deltaY, ct);
            return $"Scrolled {deltaY} at ({x}, {y})";
        }

        private async Task<string> ExecuteLaunchAppAsync(AgentTask task, CancellationToken ct)
        {
            var parts = task.CommandArgs.Split('|');
            var path = parts[0].Trim();
            var args = parts.Length > 1 ? parts[1].Trim() : null;

            var success = await _screenEnv.LaunchApplicationAsync(path, args, ct);

            if (!success)
                throw new InvalidOperationException($"Failed to launch: {path}");

            // Wait for application to start
            await Task.Delay(2000, ct);

            return $"Launched: {path}";
        }

        private async Task<string> ExecuteScreenshotAsync(CancellationToken ct)
        {
            var screenshot = await _screenEnv.CaptureScreenshotAsync(ct);
            return $"Screenshot captured ({screenshot.Length} chars base64)";
        }

        private async Task<string> ExecuteGetWindowTitleAsync(CancellationToken ct)
        {
            var title = await _screenEnv.GetActiveWindowTitleAsync(ct);
            return $"Active window: {title}";
        }

        private async Task<string> ExecutePowerShellAsync(AgentTask task, CancellationToken ct)
        {
            var command = task.CodeToExecute ?? task.CommandArgs;
            var result = await _screenEnv.ExecutePowerShellAsync(command, ct);

            if (!result.Success)
                throw new InvalidOperationException($"PowerShell failed: {result.Output}");

            return result.Output;
        }

        private async Task<string> ExecuteWaitAsync(AgentTask task, CancellationToken ct)
        {
            var ms = int.Parse(task.CommandArgs);
            await Task.Delay(ms, ct);
            return $"Waited {ms}ms";
        }

        private (int x, int y) ParseCoordinates(string args)
        {
            var parts = args.Split(',');
            if (parts.Length < 2)
                throw new ArgumentException($"Invalid coordinates: {args}. Expected format: x,y");

            return (int.Parse(parts[0].Trim()), int.Parse(parts[1].Trim()));
        }

        /// <summary>
        /// Ask the AI to find an element on screen and return its coordinates.
        /// Used when we need to find a UI element visually rather than by coordinates.
        /// </summary>
        public async Task<(int x, int y)?> FindElementAsync(string description, CancellationToken ct)
        {
            var screenshot = await _screenEnv.CaptureScreenshotAsync(ct);

            var prompt = $@"Look at this screenshot and find the UI element described as: ""{description}""

If you can find it, respond with ONLY the coordinates in the format: x,y
The coordinates should be the CENTER of the element.
If you cannot find it, respond with: NOT_FOUND

Screenshot is provided as base64 image.";

            var response = await _llm.ChatWithHistory(new List<ChatMessage>(), prompt, $"Base64:{screenshot}", "", "");

            if (response.Contains("NOT_FOUND"))
                return null;

            try
            {
                var coords = ParseCoordinates(response.Trim());
                return coords;
            }
            catch
            {
                return null;
            }
        }
    }
}
