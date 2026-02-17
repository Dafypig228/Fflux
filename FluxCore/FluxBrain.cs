using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace FluxCore
{
    /// <summary>
    /// FluxBrain: Central intelligence router.
    /// Uses Gemini as a fast intent classifier, routes requests to the correct handler,
    /// supports parallel execution (chat while working), never drops requests.
    /// </summary>
    public class FluxBrain : IAsyncDisposable
    {
        // --- Dependencies ---
        private readonly GeminiService _gemini;
        private readonly JarvisCore _jarvis;
        private readonly MemoryService? _memory;
        private readonly Hippocampus? _hippocampus;
        private readonly SensoryCortex? _cortex;

        // --- Request Queue (replaces _isProcessing boolean) ---
        private readonly Channel<BrainRequest> _requestQueue;
        private readonly CancellationTokenSource _cts = new();
        private Task? _processLoopTask;

        // --- Concurrent Task Tracking ---
        private readonly ConcurrentDictionary<string, RunningTask> _runningTasks = new();
        private int _taskIdCounter = 0;

        // --- Conversation History (thread-safe, separate from task history) ---
        private readonly List<ChatMessage> _conversationHistory = new();
        private readonly SemaphoreSlim _historyLock = new(1, 1);
        private const int MAX_HISTORY = 50;

        // --- UI Callbacks ---
        public event Action<string, bool>? OnMessage;        // text, isUser
        public event Action<string>? OnStatusChanged;

        public FluxBrain(
            GeminiService gemini,
            JarvisCore jarvis,
            MemoryService? memory,
            Hippocampus? hippocampus,
            SensoryCortex? cortex)
        {
            _gemini = gemini;
            _jarvis = jarvis;
            _memory = memory;
            _hippocampus = hippocampus;
            _cortex = cortex;

            _requestQueue = Channel.CreateBounded<BrainRequest>(
                new BoundedChannelOptions(100)
                {
                    FullMode = BoundedChannelFullMode.Wait
                });
        }

        /// <summary>Start the brain's processing loop. Call once at startup.</summary>
        public void Start()
        {
            _processLoopTask = Task.Run(() => ProcessLoopAsync(_cts.Token));
        }

        /// <summary>
        /// Submit a request. NEVER blocks, NEVER drops.
        /// Replaces the old _isProcessing boolean gate.
        /// </summary>
        public async Task SubmitAsync(string userText, RequestPriority priority = RequestPriority.Normal)
        {
            var request = new BrainRequest
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                Text = userText,
                Priority = priority,
                Timestamp = DateTime.UtcNow
            };

            await _requestQueue.Writer.WriteAsync(request);
        }

        /// <summary>The main processing loop. Runs forever on a background thread.</summary>
        private async Task ProcessLoopAsync(CancellationToken ct)
        {
            await foreach (var request in _requestQueue.Reader.ReadAllAsync(ct))
            {
                try
                {
                    OnStatusChanged?.Invoke("Thinking...");
                    await ProcessSingleRequestAsync(request, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    OnMessage?.Invoke($"Error: {ex.Message}", false);
                }
                finally
                {
                    if (_runningTasks.IsEmpty || _runningTasks.Values.All(t => t.CompletedAt.HasValue))
                        OnStatusChanged?.Invoke("Ready");
                }
            }
        }

        /// <summary>Process one request: classify then route.</summary>
        private async Task ProcessSingleRequestAsync(BrainRequest request, CancellationToken ct)
        {
            // Step 1: FAST CLASSIFICATION via Gemini (no screenshot, minimal prompt)
            var intent = await ClassifyIntentAsync(request.Text, ct);

            System.Diagnostics.Debug.WriteLine($"[BRAIN] Intent: {intent.Type} (conf: {intent.Confidence:F2}) — {intent.Summary}");

            // Step 2: ROUTE based on classification
            switch (intent.Type)
            {
                case IntentType.Chat:
                    await HandleChatAsync(request, intent, ct);
                    break;

                case IntentType.PcTask:
                    await HandlePcTaskAsync(request, intent, ct);
                    break;

                case IntentType.TaskQuery:
                    await HandleTaskQueryAsync(request, intent, ct);
                    break;

                case IntentType.MultiTask:
                    // For now, fall back to PcTask (Swarm wiring is Phase 2)
                    await HandlePcTaskAsync(request, intent, ct);
                    break;
            }
        }

        // ===================================================================
        // CLASSIFICATION
        // ===================================================================

        /// <summary>
        /// Uses Gemini with a minimal prompt (NO screenshot!) to classify intent.
        /// This is one fast API call that determines the routing.
        /// </summary>
        private async Task<ClassifiedIntent> ClassifyIntentAsync(string userText, CancellationToken ct)
        {
            // Build minimal context: what tasks are running?
            string runningTasksSummary = _runningTasks.IsEmpty
                ? "None"
                : string.Join("; ", _runningTasks.Values
                    .Where(t => !t.CompletedAt.HasValue)
                    .Select(t => $"[{t.Id}] {t.Description} ({t.Status})"));

            string classifierPrompt = $@"You are a fast intent classifier for an AI assistant that controls Windows.

RUNNING TASKS: {runningTasksSummary}

USER SAYS: ""{userText}""

Classify into EXACTLY ONE category:
CHAT - Greeting, casual conversation, general knowledge question, joke, opinion, asking about the AI itself
PC_TASK - Wants you to DO something on the computer (open app, click, type, move files, run code, search web, browse)
TASK_QUERY - Asking about a currently running task (status, progress, what's happening, is it done)
MULTI_TASK - Complex goal requiring multiple parallel subtasks (build project, organize entire folder, create full application)

Respond in EXACTLY this format (3 lines, nothing else):
INTENT: CHAT|PC_TASK|TASK_QUERY|MULTI_TASK
CONFIDENCE: 0.95
SUMMARY: One sentence describing what user wants";

            try
            {
                string response = await _gemini.GenerateText(classifierPrompt);
                return ParseClassification(response, userText);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BRAIN] Classifier error: {ex.Message}");
                // On error, default to Chat (safe fallback — never produce commands by mistake)
                return new ClassifiedIntent
                {
                    OriginalText = userText,
                    Type = IntentType.Chat,
                    Confidence = 0.5,
                    Summary = userText
                };
            }
        }

        private ClassifiedIntent ParseClassification(string response, string originalText)
        {
            var intent = new ClassifiedIntent
            {
                OriginalText = originalText,
                Type = IntentType.Chat,   // safe default
                Confidence = 0.5,
                Summary = originalText
            };

            foreach (var line in response.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();

                if (trimmed.StartsWith("INTENT:", StringComparison.OrdinalIgnoreCase))
                {
                    var value = trimmed.Substring(7).Trim().ToUpper();
                    intent.Type = value switch
                    {
                        "CHAT" => IntentType.Chat,
                        "PC_TASK" => IntentType.PcTask,
                        "TASK_QUERY" => IntentType.TaskQuery,
                        "MULTI_TASK" => IntentType.MultiTask,
                        _ => IntentType.Chat
                    };
                }
                else if (trimmed.StartsWith("CONFIDENCE:", StringComparison.OrdinalIgnoreCase))
                {
                    var confStr = trimmed.Substring(11).Trim();
                    if (double.TryParse(confStr, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var conf))
                        intent.Confidence = conf;
                }
                else if (trimmed.StartsWith("SUMMARY:", StringComparison.OrdinalIgnoreCase))
                {
                    intent.Summary = trimmed.Substring(8).Trim();
                }
            }

            // SELF-VERIFICATION: Low confidence on non-chat → default to Chat (safe)
            // Phase 2 will add consensus voting (3 calls, majority wins)
            if (intent.Confidence < 0.7 && intent.Type != IntentType.Chat)
            {
                System.Diagnostics.Debug.WriteLine($"[BRAIN] Low confidence ({intent.Confidence:F2}) on {intent.Type}, falling back to Chat");
                intent.Type = IntentType.Chat;
            }

            return intent;
        }

        // ===================================================================
        // HANDLERS
        // ===================================================================

        /// <summary>Handle pure conversation — no screenshot, no commands, no JARVIS.</summary>
        private async Task HandleChatAsync(BrainRequest request, ClassifiedIntent intent, CancellationToken ct)
        {
            OnStatusChanged?.Invoke("Chatting...");

            // Add to conversation history
            await AddToHistoryAsync(request.Text, true);

            // Build a CLEAN chat prompt — NO command instructions!
            string chatSystemPrompt = @"You are Fluxoria, a helpful and warm AI assistant.
You speak Russian and English fluently — always respond in the same language the user uses.
You can have conversations, answer questions, tell jokes, share knowledge.
You do NOT need to control the PC right now — just talk naturally.
If the user asks you to DO something on the PC, tell them you understand and you'll do it.
Keep responses concise but helpful. Be natural, not robotic.";

            // Get conversation history for context
            var history = await GetHistorySnapshotAsync();

            // Call Gemini with CLEAN prompt (no screenshot, no command format!)
            string response = await _gemini.ChatWithHistory(
                history,
                request.Text,
                "",                  // NO screen context
                chatSystemPrompt,    // Clean conversational system prompt
                "",                  // NO memory block
                chatSystemPrompt     // systemInstructionOverride — bypasses command-forcing prompt
            );

            // Clean up any accidental command markers (defense in depth)
            response = CleanConversationalResponse(response);

            await AddToHistoryAsync(response, false);
            OnMessage?.Invoke(response, false);
        }

        /// <summary>Handle a PC task — route to JarvisCore on a background Task.</summary>
        private async Task HandlePcTaskAsync(BrainRequest request, ClassifiedIntent intent, CancellationToken ct)
        {
            string taskId = $"task-{Interlocked.Increment(ref _taskIdCounter)}";

            var runningTask = new RunningTask
            {
                Id = taskId,
                Description = intent.Summary,
                UserRequest = request.Text,
                Status = "Starting...",
                StartedAt = DateTime.UtcNow
            };

            _runningTasks[taskId] = runningTask;
            OnStatusChanged?.Invoke($"Working: {intent.Summary}");

            // Acknowledge immediately (don't wait for task to finish)
            // JarvisCore.OnResponse will fire the actual result when done
            // So we only send a brief ack if there are other tasks already running
            if (_runningTasks.Values.Count(t => !t.CompletedAt.HasValue) > 1)
            {
                OnMessage?.Invoke($"On it — {intent.Summary}", false);
            }

            // FIRE on background thread — ProcessLoop continues accepting new requests
            _ = Task.Run(async () =>
            {
                try
                {
                    runningTask.Status = "Executing...";
                    string result = await _jarvis.ExecuteTaskAsync(request.Text);
                    runningTask.Status = "Completed";
                    runningTask.Result = result;
                    // JarvisCore.OnResponse already fires the chat message
                }
                catch (Exception ex)
                {
                    runningTask.Status = $"Failed: {ex.Message}";
                    OnMessage?.Invoke($"Task failed: {ex.Message}", false);
                }
                finally
                {
                    runningTask.CompletedAt = DateTime.UtcNow;

                    // Keep in _runningTasks for 5 minutes so user can ask about it
                    _ = Task.Delay(TimeSpan.FromMinutes(5)).ContinueWith(t =>
                    {
                        _runningTasks.TryRemove(taskId, out var removed);
                    });

                    if (_runningTasks.Values.All(t => t.CompletedAt.HasValue))
                        OnStatusChanged?.Invoke("Ready");
                }
            }, ct);
        }

        /// <summary>Handle a question about a running/recent task.</summary>
        private async Task HandleTaskQueryAsync(BrainRequest request, ClassifiedIntent intent, CancellationToken ct)
        {
            if (_runningTasks.IsEmpty)
            {
                OnMessage?.Invoke("No tasks are running right now. What would you like me to do?", false);
                return;
            }

            // Build task status report
            var sb = new StringBuilder();
            foreach (var task in _runningTasks.Values)
            {
                var duration = (task.CompletedAt ?? DateTime.UtcNow) - task.StartedAt;
                sb.AppendLine($"[{task.Id}] {task.Description}");
                sb.AppendLine($"  Status: {task.Status}");
                sb.AppendLine($"  Duration: {duration.TotalSeconds:F0}s");
                if (task.Result != null)
                    sb.AppendLine($"  Result: {task.Result[..Math.Min(200, task.Result.Length)]}");
            }

            // Use Gemini to generate a natural answer about the tasks
            string queryPrompt = $@"The user is asking about running tasks. Answer naturally and concisely in the same language the user uses.

TASKS:
{sb}

USER ASKS: ""{request.Text}""

Your response:";

            string response = await _gemini.GenerateText(queryPrompt);
            OnMessage?.Invoke(response, false);
        }

        // ===================================================================
        // HELPERS
        // ===================================================================

        /// <summary>Remove accidental command markers from chat responses.</summary>
        private string CleanConversationalResponse(string response)
        {
            // Remove [[COMMAND:...]] patterns
            response = System.Text.RegularExpressions.Regex.Replace(response, @"\[\[[^\]]+\]\]", "");
            // Remove THOUGHT:/ACTION:/TASK_COMPLETE markers
            response = response.Replace("THOUGHT:", "").Replace("ACTION:", "").Replace("TASK_COMPLETE", "");
            response = response.Trim();

            if (string.IsNullOrWhiteSpace(response) || response.Length < 2)
                response = "I'm here. What would you like me to do?";

            return response;
        }

        private async Task AddToHistoryAsync(string text, bool isUser)
        {
            await _historyLock.WaitAsync();
            try
            {
                _conversationHistory.Add(new ChatMessage { Text = text, IsUser = isUser });
                if (_conversationHistory.Count > MAX_HISTORY)
                    _conversationHistory.RemoveAt(0);
            }
            finally
            {
                _historyLock.Release();
            }
        }

        private async Task<List<ChatMessage>> GetHistorySnapshotAsync()
        {
            await _historyLock.WaitAsync();
            try
            {
                return new List<ChatMessage>(_conversationHistory);
            }
            finally
            {
                _historyLock.Release();
            }
        }

        // ===================================================================
        // PUBLIC API
        // ===================================================================

        /// <summary>Get status of all running tasks.</summary>
        public IReadOnlyCollection<RunningTask> GetRunningTasks()
            => _runningTasks.Values.ToList().AsReadOnly();

        /// <summary>Check if any task is actively executing.</summary>
        public bool IsWorking => _runningTasks.Values.Any(t => !t.CompletedAt.HasValue);

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            if (_processLoopTask != null)
            {
                try { await _processLoopTask.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch { }
            }
            _cts.Dispose();
            _historyLock.Dispose();
        }
    }

    // ===================================================================
    // SUPPORTING TYPES
    // ===================================================================

    public enum RequestPriority { Low, Normal, High, Critical }

    public enum IntentType { Chat, PcTask, TaskQuery, MultiTask }

    public class BrainRequest
    {
        public string Id { get; init; } = "";
        public string Text { get; init; } = "";
        public RequestPriority Priority { get; init; }
        public DateTime Timestamp { get; init; }
    }

    public class ClassifiedIntent
    {
        public string OriginalText { get; init; } = "";
        public IntentType Type { get; set; }
        public double Confidence { get; set; }
        public string Summary { get; set; } = "";
    }

    public class RunningTask
    {
        public string Id { get; init; } = "";
        public string Description { get; init; } = "";
        public string UserRequest { get; init; } = "";
        public string Status { get; set; } = "";
        public DateTime StartedAt { get; init; }
        public DateTime? CompletedAt { get; set; }
        public string? Result { get; set; }
    }
}
