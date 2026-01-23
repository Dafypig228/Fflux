using System;
using System.Collections.Generic;
using System.Linq; // Added for LINQ
using System.IO;   // Added
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FluxCore
{
    /// <summary>
    /// JARVIS Core: Intelligent agent with Plan → Execute → Verify → Reflect loop.
    /// Unlike the old system, this ACTUALLY retries when things fail.
    /// </summary>
    public class JarvisCore
    {
        private readonly GeminiService _gemini;
        private readonly ExecutionAgent _executor;
        private readonly WindowsAutomationAgent _automation;
        private readonly CodeExecutionAgent _codeRunner;
        private readonly SensoryCortex _cortex;  // NEW: For element detection
        private readonly Hippocampus _memory;    // NEW: Long-Term Memory
        private readonly ValidatorAgent _validator; // NEW: Visual Validator
        private readonly Func<string> _getScreenshot;
        private readonly Func<string> _getActiveWindow;
        private readonly Action<string> _logToUI;
        public List<ChatMessage> conversationHistory = new List<ChatMessage>();
        private FluxLogger _logger = new FluxLogger(); // File Logger
        private const int MAX_RETRIES = 3;
        private const int MAX_STEPS = 30; // Increased for complex tasks
        private static readonly string DebugPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "FluxDebug.txt");

        private void DebugLog(string message)
        {
            try
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                File.AppendAllText(DebugPath, $"[{timestamp}] {message}\n");
            }
            catch { }
        }

        public JarvisCore(
            GeminiService gemini,
            ExecutionAgent executor,
            WindowsAutomationAgent automation,
            CodeExecutionAgent codeRunner,
            SensoryCortex cortex,
            Hippocampus memory,
            ValidatorAgent validator,
            Func<string> getScreenshot,
            Func<string> getActiveWindow,
            Action<string> logToUI)
        {
            _gemini = gemini;
            _executor = executor;
            _automation = automation;
            _codeRunner = codeRunner;
            _cortex = cortex;
            _memory = memory;
            _validator = validator;
            _getScreenshot = getScreenshot;
            _getActiveWindow = getActiveWindow;
            _logToUI = logToUI;
        }

        // NEURO-HUD EVENTS
        public event Action<string>? OnStateChanged; // Thinking, Acting, Verifying...
        public event Action<string>? OnThought;
        public event Action<string>? OnAction;
        public event Action<bool, string>? OnValidation; // success, reason

        /// <summary>
        /// Main entry point. Executes a task with intelligent retry and recovery.
        /// </summary>
        public async Task<string> ExecuteTaskAsync(string userRequest)
        {
            var log = new StringBuilder();
            var failedAttempts = new List<string>();
            var successfulActions = new List<string>();
            var detailedLog = new StringBuilder();
            int noCommandCount = 0;
            
            // BUILD CONVERSATION HISTORY within the task
            var conversationHistory = new List<ChatMessage>();

            // 1. RECALL MEMORIES
            var memories = _memory.Recall(userRequest);
            if (memories.Any())
            {
                _logToUI($"[🧠] Recalled {memories.Count} past lessons.");
                foreach(var mem in memories) detailedLog.AppendLine($"[MEMORY]: {mem}");
            }
            
            // Add the user's request as the first message
            conversationHistory.Add(new ChatMessage { Text = userRequest, IsUser = true });
            
            _logToUI($"[🧠 FLUXORIA] Starting task: {userRequest}");
            
            // Track which app we should be working in
            string targetApp = "";
            if (userRequest.ToLower().Contains("chrome") || userRequest.ToLower().Contains("instagram") ||
                userRequest.ToLower().Contains("google") || userRequest.ToLower().Contains("browser"))
                targetApp = "chrome";
            else if (userRequest.ToLower().Contains("telegram"))
                targetApp = "telegram";
            else if (userRequest.ToLower().Contains("whatsapp"))
                targetApp = "whatsapp";

            for (int iteration = 0; iteration < MAX_STEPS; iteration++)
            {
                _logToUI($"[DEBUG] Starting Step {iteration + 1}...");
                _logger.Log($"STARTING STEP {iteration + 1}");
                
                OnStateChanged?.Invoke($"STEP {iteration + 1}");
                await Task.Delay(500); // Breathe

                // 1. Get current screen state SAFE
                string screenshot = "";
                string activeWindow = "Unknown";
                string clickableElements = "";
                try
                {
                    _logger.Log("Capturing Screenshot...");
                    screenshot = _getScreenshot();
                    _logger.Log("Screenshot Captured.");
                    
                    activeWindow = _getActiveWindow();
                    // clickableElements = _cortex?.GetClickableElements() ?? ""; // DISABLED FOR STABILITY
                    clickableElements = "";
                    _logger.Log($"Context: {activeWindow}");
                }
                catch (Exception ex)
                {
                     _logToUI($"[⚠️] Sensory Error: {ex.Message}");
                     _logger.Log($"SENSORY ERROR: {ex.Message}");
                     detailedLog.AppendLine($"[ERROR]: Failed to get context: {ex.Message}");
                }
                
                _logToUI($"[Step {iteration + 1}/{MAX_STEPS}] Thinking...");
                OnStateChanged?.Invoke("THINKING");
                
                // 2. Build context message (what the AI sees NOW)
                _logger.Log("Building Context...");
                string contextMessage = BuildContextMessage(userRequest, failedAttempts, successfulActions, activeWindow, iteration, clickableElements, memories);
                _logger.Log("Context Built.");
                
                // 3. Ask AI with FULL conversation history + current context
                _logger.Log("Asking Gemini...");
                string aiResponse = "";
                try
                {
                    aiResponse = await _gemini.ChatWithHistory(
                        conversationHistory,        // FULL history of this task!
                        contextMessage,             // Current context
                        string.IsNullOrEmpty(screenshot) ? "" : "BASE64:" + screenshot,
                        activeWindow,
                        ""
                    );
                    _logger.Log("Gemini Responded.");
                }
                catch (Exception ex)
                {
                    _logger.Log($"GEMINI ERROR: {ex.Message}");
                    aiResponse = "ERROR: " + ex.Message;
                }

                // Compact logging for debugging
                detailedLog.AppendLine($"\n[STEP {iteration + 1}] Window: {activeWindow}");
                detailedLog.AppendLine($"AI: {aiResponse}");
                
                // ADD AI response to conversation history
                conversationHistory.Add(new ChatMessage { Text = aiResponse, IsUser = false });
                
                // EXTRACT THOUGHT
                string thought = "";
                if (aiResponse.Contains("THOUGHT:"))
                {
                    int start = aiResponse.IndexOf("THOUGHT:") + 8;
                    int end = aiResponse.IndexOf("ACTION:", start);
                    if (end == -1) end = aiResponse.Length;
                    thought = aiResponse.Substring(start, end - start).Trim();
                    // HIDDEN FROM CHAT (Now shown in Neuro-Hud only)
                    // _logToUI($"[🧠] {thought}"); 
                    detailedLog.AppendLine($"[THOUGHT]: {thought}");
                    OnThought?.Invoke(thought);
                }
                else
                {
                    // _logToUI($"[🧠] {aiResponse.Substring(0, Math.Min(50, aiResponse.Length))}...");
                    OnThought?.Invoke(aiResponse);
                }

                // 4. EXTRACT COMMANDS from AI response
                string commandText = aiResponse;
                if (aiResponse.Contains("ACTION:"))
                {
                    // SAFETY: Only parsing commands from the ACTION part ensures we don't 
                    // accidentally execute commands mentioned in the THOUGHT part!
                    commandText = aiResponse.Substring(aiResponse.IndexOf("ACTION:"));
                }

                var commands = ExtractAllCommands(commandText);
                
                // DEBUG: Log what commands were extracted
                DebugLog($"=== STEP {iteration + 1} ===");
                DebugLog($"AI Response: {aiResponse.Substring(0, Math.Min(200, aiResponse.Length))}...");
                DebugLog($"Command Text to Parse: {commandText.Substring(0, Math.Min(150, commandText.Length))}...");
                DebugLog($"Commands Found: {commands.Count}");
                foreach (var c in commands) DebugLog($"  → {c.Type}:{c.Arg.Substring(0, Math.Min(100, c.Arg.Length))}");
                
                // Filter out LOG commands
                var logCommands = commands.Where(c => c.Type == "LOG").ToList();
                var actionCommands = commands.Where(c => c.Type != "LOG").ToList();
                
                // Execute all LOG commands first
                foreach (var logCmd in logCommands)
                {
                    _logToUI($"[📝] {logCmd.Arg}");
                    detailedLog.AppendLine($"[LOG]: {logCmd.Arg}");
                }
                
                // Check for TASK_COMPLETE flag
                bool isTaskComplete = aiResponse.ToUpper().Contains("TASK_COMPLETE") || aiResponse.ToUpper().Contains("TASK_FAILED");

                // 5. BATTERY EXECUTION (Batch Mode)
                bool isAllFileOps = actionCommands.All(c => 
                    c.Type == "MOVE_FILE" || 
                    c.Type == "MAKE_DIR" || 
                    c.Type == "LIST_FILES" ||
                    c.Type == "WRITE_FILE");

                if (isAllFileOps && actionCommands.Count > 0)
                {
                    _logToUI($"[🚀] Batch Executing {actionCommands.Count} file operations...");
                    foreach (var cmd in actionCommands)
                    {
                        string currentAction = $"{cmd.Type}:{cmd.Arg}";
                        _logger.Log($"Batch Executing: {currentAction}");
                        
                        // Execute
                        ExecutionOutcome outcome = await ExecuteWithRetryAsync(cmd.Type, cmd.Arg);
                        
                        if (outcome.Success)
                        {
                            successfulActions.Add($"✓ {currentAction}");
                            detailedLog.AppendLine($"[BATCH OK]: {currentAction}");
                        }
                        else
                        {
                            failedAttempts.Add($"FAILED: {currentAction} → {outcome.Message}");
                            detailedLog.AppendLine($"[BATCH FAIL]: {currentAction} - {outcome.Message}");
                        }
                    }
                    _logToUI($"[✓] Batch Complete.");
                    
                    // IF Task was marked complete, return NOW after batch execution
                    if (isTaskComplete)
                    {
                         return $"Task Completed. {successfulActions.Count} actions performed successfully.";
                    }
                    
                    // Otherwise continue loop (maybe ai wants to list files again?)
                    // But usually batch means done for that step.
                    // For safety, if we did a batch, let's treat it as a significant step.
                    continue; 
                }
                
                // If just TASK_COMPLETE with no actions
                if (isTaskComplete && actionCommands.Count == 0)
                {
                     return $"Task Completed. {successfulActions.Count} actions performed successfully.";
                }

                // STANDARD EXECUTION (Single Action for UI safety)
                if (actionCommands.Count > 0)
                {
                    var cmd = actionCommands[0];
                    string currentAction = $"{cmd.Type}:{cmd.Arg}";
                    
                    // Before KEYS command, verify correct window
                    if (cmd.Type.ToUpper() == "KEYS" && !string.IsNullOrEmpty(targetApp))
                    {
                        string currentWin = _getActiveWindow();
                        if (!currentWin.ToLower().Contains(targetApp.ToLower()) &&
                            !currentWin.ToLower().Contains("chrome") &&
                            !currentWin.ToLower().Contains("instagram"))
                        {
                            _logToUI($"[🔄] Wrong window '{currentWin}', refocusing {targetApp}...");
                            await _executor.OpenApp(targetApp);
                            await Task.Delay(150);
                        }
                    }
                    
                    detailedLog.AppendLine($"[ACTION]: {currentAction}");
                    _logToUI($"[⚡] {currentAction}");
                    OnStateChanged?.Invoke("ACTING");
                    OnAction?.Invoke(currentAction);
                    
                    _logger.Log($"Executing Action: {currentAction}");
                    DebugLog($"EXECUTING: {currentAction}");
                    ExecutionOutcome outcome = await ExecuteWithRetryAsync(cmd.Type, cmd.Arg);
                    DebugLog($"RESULT: Success={outcome.Success}, Message={outcome.Message}");
                    _logger.Log($"Action Completed: {outcome.Success}");
                    _logToUI($"[DEBUG] Command returned. Success: {outcome.Success}");
                    
                    string resultMsg = $"{(outcome.Success ? "✓" : "✗")} {outcome.Message}";
                    detailedLog.AppendLine($"[RESULT]: {resultMsg}");
                    log.AppendLine($"[{(outcome.Success ? "✓" : "✗")}] {cmd.Type}: {outcome.Message}");
                    
                    // ADD result to conversation history so AI knows what happened
                    conversationHistory.Add(new ChatMessage { Text = $"Result of {currentAction}: {resultMsg}", IsUser = true });
                    
                    if (outcome.Success)
                        successfulActions.Add($"✓ {currentAction}");
                    else
                        failedAttempts.Add($"FAILED: {currentAction} → {outcome.Message}");
                    
                    // Wait for UI to update before taking new screenshot
                    if (cmd.Type == "KEYS" && cmd.Arg.ToUpper().Contains("ENTER"))
                        await Task.Delay(1500);
                    else if (cmd.Type == "CLICK" || cmd.Type == "TYPE")
                        await Task.Delay(300);
                    else
                        await Task.Delay(150);
                    
                    noCommandCount = 0;

                    // 6. VISUAL VERIFICATION (NEW - Phase 3)
                    // Only verify impactful actions that change screen state
                    try
                    {
                        // Wait for UI to settle (especially for HIDE_SELF or app launch)
                        await Task.Delay(1000); 

                        string afterScreenshot = "";
                        try 
                        {
                            afterScreenshot = _getScreenshot();
                        }
                        catch (Exception ex)
                        {
                            _logToUI($"[⚠️] Screenshot failed: {ex.Message}");
                        }

                        // Validate if we have both screenshots
                        if (!string.IsNullOrEmpty(screenshot) && !string.IsNullOrEmpty(afterScreenshot))
                        {
                             _logToUI($"[👁️] Verifying action: {cmd.Type}");
                             OnStateChanged?.Invoke("VERIFYING");
                             
                             var validation = await _validator.ValidateVisualAsync(currentAction, screenshot, afterScreenshot);
                             
                             OnValidation?.Invoke(validation.Success, validation.Message);
                             
                             if (!validation.Success)
                             {
                                  _logToUI($"[⚠️] Visual check failed: {validation.Message}");
                                  detailedLog.AppendLine($"[VALIDATOR]: Action might have failed! Reason: {validation.Message}");
                             }
                        }
                    }
                    catch (Exception ex)
                    {
                         _logToUI($"[⚠️] Validation error: {ex.Message}");
                    }

                    // After TASK_COMPLETE with action, we're done
                    if (aiResponse.ToUpper().Contains("TASK_COMPLETE"))
                    {
                        log.AppendLine($"[✓] Task completed after {iteration + 1} steps");
                        
                        // REFLEXION: Learn from this session
                        _logToUI("[🧠] Reflexing on task...");
                        await PerformReflexionAsync(userRequest, conversationHistory, true);

                        log.AppendLine("\n\n=== FULL DEBUG LOG ===");
                        log.Append(detailedLog.ToString());
                        return log.ToString();
                    }
                    
                    // Loop continues - will take fresh screenshot and ask AI what to do next
                }
                else
                {
                    noCommandCount++;
                    if (iteration == 0) return aiResponse;
                    if (noCommandCount >= 3) break;
                    failedAttempts.Add("No action command found.");
                }
            }

            log.AppendLine($"[!] Reached max steps ({MAX_STEPS})");
            log.AppendLine("\n\n=== DETAILED LOG FOR DEBUGGING ===");
            log.Append(detailedLog.ToString());
            return log.ToString();
        }

        /// <summary>
        /// Executes a command with automatic retry using different strategies.
        /// </summary>
        private async Task<ExecutionOutcome> ExecuteWithRetryAsync(string cmdType, string cmdArg)
        {
            string lastError = "";
            
            for (int retry = 0; retry < MAX_RETRIES; retry++)
            {
                try
                {
                    var result = await ExecuteSingleCommandAsync(cmdType, cmdArg, retry);
                    
                    if (result.Success)
                    {
                        return new ExecutionOutcome(true, result.Message);
                    }
                    
                    lastError = result.Message;
                    
                    // Check if it's a safety stop - these need different handling
                    if (result.Message.Contains("SAFETY STOP"))
                    {
                        _logToUI($"[🔄] Safety stop detected, waiting for correct window...");
                        await Task.Delay(1000);
                        
                        // Try to refocus the expected window
                        await _executor.OpenApp(GetExpectedApp(cmdType, cmdArg));
                        await Task.Delay(500);
                        continue;
                    }
                    
                    // Check if element not found - try alternative selectors
                    if (result.Message.Contains("not found") && retry < MAX_RETRIES - 1)
                    {
                        _logToUI($"[🔄] Element not found, trying alternative approach (attempt {retry + 2})...");
                        await Task.Delay(500);
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }
            }
            
            return new ExecutionOutcome(false, $"Failed after {MAX_RETRIES} attempts: {lastError}");
        }

        /// <summary>
        /// Executes a single command, with strategy variation based on retry count.
        /// </summary>
        private async Task<ExecutionResult> ExecuteSingleCommandAsync(string cmdType, string cmdArg, int retryCount)
        {
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
                    await Task.Delay(300);
                }
                else if (cmdType == "CLICK" && retryCount == 2)
                {
                    // REMOVED dangerous Tab+Enter fallback which opens Explorer!
                    // If click fails twice, just report failure so AI can try coordinates.
                    return new ExecutionResult(false, "Click failed (element not found)");
                }
                else if (cmdType == "TYPE" && retryCount > 0)
                {
                    // For type, first try clicking in the window to ensure focus
                    await _automation.ClickElementAsync("500,400");
                    await Task.Delay(200);
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
                    // Check if this looks like a URL
                    bool looksLikeUrl = cmdArg.Contains(".com") || cmdArg.Contains(".org") || 
                                       cmdArg.Contains("http") || cmdArg.Contains("www.") ||
                                       cmdArg.Contains(".net") || cmdArg.Contains(".ru") ||
                                       cmdArg.Contains("/direct/") || cmdArg.Contains("instagram");
                    if (looksLikeUrl)
                    {
                        // DIRECT URL OPENING - bypasses ALL focus issues!
                        try
                        {
                            string url = cmdArg;
                            if (!url.StartsWith("http")) url = "https://" + url;
                            
                            // Use Process.Start to open URL directly in default browser
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = url,
                                UseShellExecute = true
                            });
                            await Task.Delay(2000); // Wait for page to load
                            return new ExecutionResult(true, $"Opened URL: {url}");
                        }
                        catch (Exception ex)
                        {
                            return new ExecutionResult(false, $"Failed to open URL: {ex.Message}");
                        }
                    }
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
                    return await _codeRunner.RunPowerShellAsync(cmdArg);
                    
                case "WRITE_FILE":
                    var parts = cmdArg.Split(new[] { '|' }, 2);
                    if (parts.Length < 2) return new ExecutionResult(false, "Invalid format");
                    return await _codeRunner.WriteFileAsync(parts[0].Trim(), parts[1]);
                
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

                // FILE SYSTEM COMMANDS (For Desktop Cleaning)
                case "LIST_FILES":
                    return await _codeRunner.ListFilesAsync(cmdArg);
                
                case "MOVE_FILE":
                    var moveParts = cmdArg.Split('|');
                    if (moveParts.Length < 2) return new ExecutionResult(false, "Usage: MOVE_FILE:source|dest");
                    return await _codeRunner.MoveFileAsync(moveParts[0].Trim(), moveParts[1].Trim());
                
                case "MAKE_DIR":
                    return await _codeRunner.MakeDirAsync(cmdArg);

                // TIER 1: SMART FILE MANAGEMENT
                case "COPY_FILE":
                    var copyParts = cmdArg.Split('|');
                    if (copyParts.Length < 2) return new ExecutionResult(false, "Usage: COPY_FILE:source|dest");
                    return await _codeRunner.CopyFileAsync(copyParts[0].Trim(), copyParts[1].Trim());
                
                case "DELETE_FILE":
                    return await _codeRunner.DeleteFileAsync(cmdArg);
                
                case "RENAME_FILE":
                    var renameParts = cmdArg.Split('|');
                    if (renameParts.Length < 2) return new ExecutionResult(false, "Usage: RENAME_FILE:path|newName");
                    return await _codeRunner.RenameFileAsync(renameParts[0].Trim(), renameParts[1].Trim());
                
                case "FILE_INFO":
                    return await _codeRunner.GetFileInfoAsync(cmdArg);
                
                case "READ_FILE":
                    return await _codeRunner.ReadFileAsync(cmdArg);

                // TIER 2: SMART FILE DISCOVERY
                case "SEARCH_FILES":
                case "FIND_FILE":
                case "FIND":
                    return await _codeRunner.SearchFilesAsync(cmdArg);

                case "HIDE_SELF":
                case "MINIMIZE_SELF":
                    // Run on thread pool ensuring we don't block command execution loop
                     await Task.Run(() => _automation.MinimizeFlux());
                    return new ExecutionResult(true, "Flux window minimized.");
                    
                default:
                    return new ExecutionResult(false, $"Unknown command: {cmdType}");
            }
        }

        private string BuildContextMessage(string originalGoal, List<string> failures, List<string> successes, string activeWindow, int step, string clickableElements, List<string> memories)
        {
            var sb = new StringBuilder();
            
            sb.AppendLine("You are Fluxoria, an AI that controls this Windows PC.");
            sb.AppendLine("You can SEE the screen. Look at it and decide what to do.");
            // Add Goal
            sb.AppendLine($"GOAL: {originalGoal}");
            
            // Add Memories
            if (memories != null && memories.Any())
            {
                sb.AppendLine();
                sb.AppendLine("PAST LESSONS (REFLEXION):");
                foreach (var m in memories) sb.AppendLine($"- {m}");
            }

            sb.AppendLine();
            sb.AppendLine("═══════════════════════════════════════════════════════════");
            sb.AppendLine("YOU ARE FLUXORIA - AUTONOMOUS AI");
            sb.AppendLine("You control this Windows PC. You can do ANYTHING.");
            sb.AppendLine("═══════════════════════════════════════════════════════════");
            sb.AppendLine();
            sb.AppendLine("CORE TOOLS:");
            sb.AppendLine("  [[POWERSHELL:command]] - Run any PowerShell command");
            sb.AppendLine("  [[PYTHON:code]] - Execute Python code");
            sb.AppendLine("  [[CLICK:text]] or [[CLICK:x,y]] - Click on screen");
            sb.AppendLine("  [[TYPE:text]] - Type text (URLs auto-open in browser)");
            sb.AppendLine("  [[KEYS:combo]] - Keyboard shortcuts (ENTER, CTRL+C, WIN+D)");
            sb.AppendLine("  [[OPEN_APP:name]] - Open/focus application");
            sb.AppendLine("  [[HIDE_SELF]] - Minimize Fluxoria window");
            sb.AppendLine("  [[WAIT:ms]] - Wait milliseconds");
            sb.AppendLine();
            
            // CRITICAL: Tell AI the REAL paths (handles OneDrive!)
            string realDesktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string realDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            sb.AppendLine("★★★ CRITICAL: REAL PATHS (NOT default!) ★★★");
            sb.AppendLine($"  Desktop = {realDesktop}");
            sb.AppendLine($"  Documents = {realDocuments}");
            sb.AppendLine("  USE THESE EXACT PATHS - the default C:\\Users\\...\\Desktop may NOT work!");
            sb.AppendLine();
            
            sb.AppendLine("★★★ YOU FIGURE OUT THE REST ★★★");
            sb.AppendLine("Need to find files? → Write PowerShell: Get-ChildItem -Recurse -Filter '*.pdf'");
            sb.AppendLine("Need to move files? → Write PowerShell: Move-Item 'src' 'dest'");
            sb.AppendLine("Need to analyze data? → Write Python code");
            sb.AppendLine("Need anything else? → Write the code for it!");
            sb.AppendLine();
            sb.AppendLine("NEVER say 'I cannot'. You have a full OS at your command.");
            sb.AppendLine("Errors are feedback. Try a different approach.");
            sb.AppendLine();
            sb.AppendLine($"ACTIVE WINDOW: {activeWindow} | Step: {step + 1}/30");
            
            if (successes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("✓ Completed:");
                foreach (var s in successes.TakeLast(3))
                    sb.AppendLine($"  {s}");
            }
            
            if (failures.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("✗ Errors (try different approach):");
                foreach (var f in failures.TakeLast(2))
                    sb.AppendLine($"  {f}");
            }
            
            if (!string.IsNullOrEmpty(clickableElements))
            {
                sb.AppendLine();
                sb.AppendLine(clickableElements);
            }
            
            if (memories != null && memories.Any())
            {
                sb.AppendLine();
                sb.AppendLine("LESSON FROM MEMORY: " + memories.First());
            }

            sb.AppendLine();
            sb.AppendLine("FORMAT: Think first, then act.");
            sb.AppendLine("THOUGHT: [Your reasoning]");
            sb.AppendLine("ACTION: [[COMMAND:arg]]  or  TASK_COMPLETE");
            
            return sb.ToString();
        }

        /// <summary>
        /// Extracts ALL commands from AI response, in order of appearance.
        /// </summary>
        private List<(string Type, string Arg)> ExtractAllCommands(string text)
        {
            var result = new List<(string Type, string Arg, int Position)>();
            var commandTypes = new[] { "OPEN_APP", "TYPE", "KEYS", "CLICK", "SCROLL", "DRAG", "WINDOW", "WAIT", "LOG", "PYTHON", "POWERSHELL", "PS", "HIDE_SELF", "MINIMIZE_SELF" };
            
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
                    int end = text.IndexOf("]]", argStart);
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

        private string GetExpectedApp(string cmdType, string cmdArg)
        {
            // Infer expected app from command context
            if (cmdArg.ToLower().Contains("instagram")) return "chrome";
            if (cmdArg.ToLower().Contains("telegram")) return "telegram";
            if (cmdArg.ToLower().Contains("notepad")) return "notepad";
            return "chrome"; // default
        }

        private string CleanResponse(string response)
        {
            return response
                .Replace("TASK_COMPLETE", "")
                .Replace("[[", "")
                .Replace("]]", "")
                .Trim();
        }

        private async Task PerformReflexionAsync(string goal, List<ChatMessage> history, bool success)
        {
            try
            {
                // Simple Reflexion Prompt
                var sb = new StringBuilder();
                sb.AppendLine($"You are the Reflexion Module. The agent just finished a task: '{goal}'.");
                sb.AppendLine($"Outcome: {(success ? "SUCCESS" : "FAILURE")}");
                sb.AppendLine("Review the conversation history above.");
                sb.AppendLine("Identify ONE critical lesson or rule that allowed you to succeed (or caused failure).");
                sb.AppendLine("The lesson should be a general rule for future tasks (e.g. 'Use X command for Y app').");
                sb.AppendLine("Output ONLY the lesson text. Keep it under 100 characters.");
                
                string prompt = sb.ToString();
                string lesson = await _gemini.ChatWithHistory(history, prompt, "", "", "");
                
                lesson = lesson?.Replace("Lesson:", "").Trim() ?? "";
                
                if (!string.IsNullOrWhiteSpace(lesson) && lesson.Length > 10)
                {
                    _logToUI($"[🎓] Learned: {lesson}");
                    
                    // Extract trigger keywords from goal
                    string trigger = goal.ToLower().Replace("open", "").Replace("please", "").Trim().Split(' ')[0];
                    if (string.IsNullOrEmpty(trigger)) trigger = "general";
                    
                    await _memory.LearnAsync(trigger, lesson);
                }
            }
            catch (Exception ex)
            {
                _logToUI($"[⚠️] Failed to reflect: {ex.Message}");
            }
        }
    }

    public class ExecutionOutcome
    {
        public bool Success { get; }
        public string Message { get; }
        
        public ExecutionOutcome(bool success, string message)
        {
            Success = success;
            Message = message;
        }
    }

    public class FluxLogger
    {
        private string _path;
        public FluxLogger()
        {
            _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "FluxDebug.txt");
        }

        public void Log(string message)
        {
            try
            {
                File.AppendAllText(_path, $"[{DateTime.Now:HH:mm:ss}] {message}\n");
            }
            catch { }
        }
    }
}
