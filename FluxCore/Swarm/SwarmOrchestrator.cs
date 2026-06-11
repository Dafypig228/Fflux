using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluxCore.LLM;
using FluxCore.Swarm.Agents;
using FluxCore.Swarm.Environment;
using FluxCore.Swarm.Infrastructure;
using FluxCore.Swarm.UI;
using Application = System.Windows.Application;

namespace FluxCore.Swarm
{
    /// <summary>
    /// Result of executing a goal with the swarm.
    /// </summary>
    public record SwarmExecutionResult
    {
        public bool Success { get; init; }
        public int TotalTasks { get; init; }
        public int CompletedTasks { get; init; }
        public int FailedTasks { get; init; }
        public TimeSpan Duration { get; init; }
        public string? Error { get; init; }
        public List<TaskCompletedMessage> CompletedTaskDetails { get; init; } = new();
        public List<TaskFailedMessage> FailedTaskDetails { get; init; } = new();
        public string? FinalScreenshot { get; init; }
    }

    /// <summary>
    /// Configuration for the swarm orchestrator.
    /// </summary>
    public class SwarmConfig
    {
        public AgentPoolConfig AgentPoolConfig { get; init; } = new();
        public TimeSpan TaskTimeout { get; init; } = TimeSpan.FromMinutes(10);
        public TimeSpan OverallTimeout { get; init; } = TimeSpan.FromHours(1);
        public bool EnableScreenAgent { get; init; } = true;
        public string? HyperVVmName { get; init; }
        public bool UseLocalScreenFallback { get; init; } = true;
    }

    /// <summary>
    /// Main orchestrator for the multi-agent swarm.
    /// Receives high-level goals, decomposes them into tasks, and coordinates agent execution.
    /// </summary>
    public class SwarmOrchestrator : IAsyncDisposable
    {
        private readonly IMessageBus _messageBus;
        private readonly IAgentRegistry _registry;
        private readonly IFileLockManager _lockManager;
        private readonly TaskDecomposer _decomposer;
        private readonly DependencyGraph _dependencyGraph;
        private readonly DynamicAgentPool _agentPool;
        private readonly ConflictResolver _conflictResolver;
        private readonly ILLMService _llm;
        private readonly SwarmConfig _config;
        private readonly Action<string>? _logToUI;

        // Screen agent (single instance)
        private ScreenAgent? _screenAgent;
        private IScreenEnvironment? _screenEnvironment;
        private ScreenAgentWindow? _screenAgentWindow;

        // Event subscriptions
        private IDisposable? _completionSubscription;
        private IDisposable? _failureSubscription;
        private IDisposable? _progressSubscription;

        // Execution state
        private readonly ConcurrentDictionary<string, TaskCompletedMessage> _completedTasks = new();
        private readonly ConcurrentDictionary<string, TaskFailedMessage> _failedTasks = new();

        public SwarmOrchestrator(
            IMessageBus messageBus,
            IAgentRegistry registry,
            IFileLockManager lockManager,
            ILLMService llm,
            SwarmConfig? config = null,
            Action<string>? logToUI = null)
        {
            _messageBus = messageBus;
            _registry = registry;
            _lockManager = lockManager;
            _llm = llm;
            _config = config ?? new SwarmConfig();
            _logToUI = logToUI;

            _decomposer = new TaskDecomposer(llm, logToUI);
            _dependencyGraph = new DependencyGraph(logToUI);
            _agentPool = new DynamicAgentPool(messageBus, registry, lockManager, llm, _config.AgentPoolConfig, logToUI);
            _conflictResolver = new ConflictResolver(messageBus, registry, lockManager, logToUI);

            // Subscribe to task events
            _completionSubscription = _messageBus.Subscribe<TaskCompletedMessage>("orchestrator", HandleTaskCompleted);
            _failureSubscription = _messageBus.Subscribe<TaskFailedMessage>("orchestrator", HandleTaskFailed);
            _progressSubscription = _messageBus.Subscribe<ProgressMessage>("orchestrator", HandleProgress);
        }

        /// <summary>
        /// Execute a high-level goal using the agent swarm.
        /// </summary>
        public async Task<SwarmExecutionResult> ExecuteGoalAsync(
            string goal,
            string workingDirectory,
            CancellationToken ct = default)
        {
            var startTime = DateTime.UtcNow;
            _logToUI?.Invoke($"[Orchestrator] Starting goal execution: {goal}");

            // Clear previous state
            _completedTasks.Clear();
            _failedTasks.Clear();
            _dependencyGraph.Clear();

            try
            {
                // 1. Decompose goal into tasks
                _logToUI?.Invoke("[Orchestrator] Step 1: Decomposing goal into tasks...");
                var decomposition = await _decomposer.DecomposeAsync(goal, workingDirectory, ct);

                if (!decomposition.Success)
                {
                    return new SwarmExecutionResult
                    {
                        Success = false,
                        Error = $"Failed to decompose goal: {decomposition.Error}",
                        Duration = DateTime.UtcNow - startTime
                    };
                }

                var tasks = decomposition.Tasks;
                _logToUI?.Invoke($"[Orchestrator] Decomposed into {tasks.Count} tasks (complexity: {decomposition.EstimatedComplexity})");

                if (tasks.Count == 0)
                {
                    return new SwarmExecutionResult
                    {
                        Success = false,
                        Error = "No tasks generated from goal",
                        Duration = DateTime.UtcNow - startTime
                    };
                }

                // 2. Load tasks into dependency graph
                _logToUI?.Invoke("[Orchestrator] Step 2: Building dependency graph...");
                _dependencyGraph.LoadTasks(tasks);
                _logToUI?.Invoke(_dependencyGraph.ToDebugString());

                // 3. Estimate and scale agent pool
                _logToUI?.Invoke("[Orchestrator] Step 3: Scaling agent pool...");
                int agentCount = _agentPool.EstimateAgentCount(tasks, _dependencyGraph);
                await _agentPool.ScaleToAsync(agentCount, ct);

                // 4. Initialize screen agent if needed
                bool hasScreenTasks = tasks.Any(t => t.RequiresScreenAccess);
                if (hasScreenTasks && _config.EnableScreenAgent)
                {
                    _logToUI?.Invoke("[Orchestrator] Step 4: Initializing screen agent...");
                    await InitializeScreenAgentAsync(ct);
                }
                else
                {
                    _logToUI?.Invoke("[Orchestrator] Step 4: No screen tasks - skipping screen agent");
                }

                // 5. Execute tasks
                _logToUI?.Invoke("[Orchestrator] Step 5: Executing tasks...");
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(_config.OverallTimeout);

                // Start the dependency graph execution (enqueues ready tasks)
                var executionTask = _dependencyGraph.ExecuteAsync(_messageBus, timeoutCts.Token);

                // Wait for all tasks to complete
                await WaitForAllTasksAsync(tasks.Count, timeoutCts.Token);

                // 6. Collect results
                _logToUI?.Invoke("[Orchestrator] Step 6: Collecting results...");
                var duration = DateTime.UtcNow - startTime;

                var result = new SwarmExecutionResult
                {
                    Success = _failedTasks.IsEmpty,
                    TotalTasks = tasks.Count,
                    CompletedTasks = _completedTasks.Count,
                    FailedTasks = _failedTasks.Count,
                    Duration = duration,
                    CompletedTaskDetails = _completedTasks.Values.ToList(),
                    FailedTaskDetails = _failedTasks.Values.ToList()
                };

                if (!result.Success && _failedTasks.Count > 0)
                {
                    result = result with { Error = _failedTasks.Values.First().Error };
                }

                _logToUI?.Invoke($"[Orchestrator] Execution complete: {result.CompletedTasks} completed, {result.FailedTasks} failed in {duration.TotalSeconds:F1}s");

                return result;
            }
            catch (OperationCanceledException)
            {
                _logToUI?.Invoke("[Orchestrator] Execution cancelled");
                return new SwarmExecutionResult
                {
                    Success = false,
                    Error = "Execution cancelled",
                    TotalTasks = _dependencyGraph.GetAllTasks().Count,
                    CompletedTasks = _completedTasks.Count,
                    FailedTasks = _failedTasks.Count,
                    Duration = DateTime.UtcNow - startTime
                };
            }
            catch (Exception ex)
            {
                _logToUI?.Invoke($"[Orchestrator] Execution error: {ex.Message}");
                return new SwarmExecutionResult
                {
                    Success = false,
                    Error = ex.Message,
                    Duration = DateTime.UtcNow - startTime
                };
            }
        }

        /// <summary>
        /// Execute a simple task without full decomposition.
        /// </summary>
        public async Task<SwarmExecutionResult> ExecuteSimpleTaskAsync(
            string description,
            string commandType,
            string commandArgs,
            string? codeToExecute = null,
            CancellationToken ct = default)
        {
            var task = _decomposer.CreateSimpleTask(description, commandType, commandArgs, codeToExecute);
            return await ExecuteTasksAsync(new[] { task }, ct);
        }

        /// <summary>
        /// Execute a list of pre-defined tasks.
        /// </summary>
        public async Task<SwarmExecutionResult> ExecuteTasksAsync(
            IEnumerable<AgentTask> tasks,
            CancellationToken ct = default)
        {
            var startTime = DateTime.UtcNow;
            var taskList = tasks.ToList();

            _completedTasks.Clear();
            _failedTasks.Clear();
            _dependencyGraph.Clear();
            _dependencyGraph.LoadTasks(taskList);

            int agentCount = _agentPool.EstimateAgentCount(taskList, _dependencyGraph);
            await _agentPool.ScaleToAsync(agentCount, ct);

            await _dependencyGraph.ExecuteAsync(_messageBus, ct);
            await WaitForAllTasksAsync(taskList.Count, ct);

            return new SwarmExecutionResult
            {
                Success = _failedTasks.IsEmpty,
                TotalTasks = taskList.Count,
                CompletedTasks = _completedTasks.Count,
                FailedTasks = _failedTasks.Count,
                Duration = DateTime.UtcNow - startTime,
                CompletedTaskDetails = _completedTasks.Values.ToList(),
                FailedTaskDetails = _failedTasks.Values.ToList()
            };
        }

        /// <summary>
        /// Initialize the screen agent with the appropriate environment.
        /// </summary>
        private async Task InitializeScreenAgentAsync(CancellationToken ct)
        {
            if (_screenAgent != null)
            {
                _logToUI?.Invoke("[Orchestrator] Screen agent already initialized");
                return;
            }

            // Try Hyper-V first if configured
            if (!string.IsNullOrEmpty(_config.HyperVVmName))
            {
                try
                {
                    _logToUI?.Invoke($"[Orchestrator] Initializing Hyper-V environment: {_config.HyperVVmName}");
                    _screenEnvironment = new HyperVEnvironment(_config.HyperVVmName);
                    await _screenEnvironment.EnsureRunningAsync(ct);
                }
                catch (Exception ex)
                {
                    _logToUI?.Invoke($"[Orchestrator] Hyper-V failed: {ex.Message}");
                    _screenEnvironment = null;
                }
            }

            // Fall back to local screen if enabled
            if (_screenEnvironment == null && _config.UseLocalScreenFallback)
            {
                _logToUI?.Invoke("[Orchestrator] Using local screen environment (WARNING: may interrupt user)");
                _screenEnvironment = new LocalScreenEnvironment();
                await _screenEnvironment.EnsureRunningAsync(ct);
            }

            if (_screenEnvironment == null)
            {
                _logToUI?.Invoke("[Orchestrator] No screen environment available - screen tasks will fail");
                return;
            }

            // Create and start screen agent
            _screenAgent = new ScreenAgent(
                "screen-agent-001",
                _screenEnvironment,
                _messageBus,
                _lockManager,
                _registry,
                _llm,
                maxRetries: 3,
                logToUI: _logToUI);

            await _screenAgent.StartAsync();
            _logToUI?.Invoke($"[Orchestrator] Screen agent started: {_screenEnvironment.ConnectionInfo}");

            // Open the ScreenAgent UI window so user can watch and chat
            OpenScreenAgentWindow();
        }

        /// <summary>
        /// Opens the ScreenAgent window on the UI thread so the user can:
        /// 1. Watch the VM screen in real-time
        /// 2. Chat with the ScreenAgent naturally (no commands needed)
        /// </summary>
        private void OpenScreenAgentWindow()
        {
            if (_screenAgent == null || _screenEnvironment == null)
                return;

            try
            {
                // Open window on UI thread
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    _screenAgentWindow = new ScreenAgentWindow(_screenAgent, _screenEnvironment, _llm);

                    // Wire up events
                    _screenAgentWindow.StopRequested += (s, e) =>
                    {
                        _logToUI?.Invoke("[Orchestrator] User requested stop via ScreenAgent window");
                        // The window will handle stopping the agent
                    };

                    _screenAgentWindow.UserMessageSent += (s, message) =>
                    {
                        _logToUI?.Invoke($"[Orchestrator] User chat: {message}");
                    };

                    _screenAgentWindow.Show();
                    _logToUI?.Invoke("[Orchestrator] ScreenAgent window opened - user can now watch and chat");
                });
            }
            catch (Exception ex)
            {
                _logToUI?.Invoke($"[Orchestrator] Failed to open ScreenAgent window: {ex.Message}");
            }
        }

        /// <summary>
        /// Wait for all tasks to complete.
        /// </summary>
        private async Task WaitForAllTasksAsync(int totalTasks, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                int completed = _completedTasks.Count;
                int failed = _failedTasks.Count;
                int total = completed + failed;

                if (total >= totalTasks)
                    break;

                // Also check dependency graph status
                if (_dependencyGraph.IsComplete())
                    break;

                await Task.Delay(100, ct);
            }
        }

        /// <summary>
        /// Handle task completion event.
        /// </summary>
        private Task HandleTaskCompleted(TaskCompletedMessage msg)
        {
            _completedTasks[msg.TaskId] = msg;
            _dependencyGraph.MarkCompleted(msg.TaskId);
            _logToUI?.Invoke($"[Orchestrator] Task completed: {msg.TaskId} ({(msg.Success ? "SUCCESS" : "FAILED")})");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Handle task failure event.
        /// </summary>
        private Task HandleTaskFailed(TaskFailedMessage msg)
        {
            _failedTasks[msg.TaskId] = msg;
            _dependencyGraph.MarkFailed(msg.TaskId);
            _logToUI?.Invoke($"[Orchestrator] Task failed: {msg.TaskId} - {msg.Error}");

            // Mark tasks that depend on this one as failed too
            foreach (var dependentId in _dependencyGraph.GetBlockedBy(msg.TaskId))
            {
                if (!_failedTasks.ContainsKey(dependentId) && !_completedTasks.ContainsKey(dependentId))
                {
                    _dependencyGraph.MarkFailed(dependentId);
                    _failedTasks[dependentId] = new TaskFailedMessage
                    {
                        TaskId = dependentId,
                        Error = $"Blocked by failed task: {msg.TaskId}",
                        IsPermanentFailure = true
                    };
                }
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Handle progress update event.
        /// </summary>
        private Task HandleProgress(ProgressMessage msg)
        {
            _logToUI?.Invoke($"[Orchestrator] Progress: {msg.TaskId} - {msg.Progress:P0} - {msg.Status}");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Get current swarm status.
        /// </summary>
        public SwarmStatus GetStatus()
        {
            return new SwarmStatus
            {
                AgentPoolStatus = _agentPool.GetStatus(),
                PendingTasks = _messageBus.PendingTaskCount,
                CompletedTasks = _completedTasks.Count,
                FailedTasks = _failedTasks.Count,
                GraphStatus = new GraphStatus
                {
                    TotalTasks = _dependencyGraph.GetAllTasks().Count,
                    CompletedTasks = _dependencyGraph.GetCompletedCount(),
                    FailedTasks = _dependencyGraph.GetFailedCount(),
                    IsComplete = _dependencyGraph.IsComplete(),
                    MaxParallelism = _dependencyGraph.GetMaxParallelism()
                },
                ScreenAgentActive = _screenAgent?.State == AgentState.Working,
                ScreenEnvironment = _screenEnvironment?.ConnectionInfo
            };
        }

        /// <summary>
        /// Shutdown the orchestrator and all agents.
        /// </summary>
        public async Task ShutdownAsync(CancellationToken ct = default)
        {
            _logToUI?.Invoke("[Orchestrator] Shutting down...");

            // Stop subscriptions
            _completionSubscription?.Dispose();
            _failureSubscription?.Dispose();
            _progressSubscription?.Dispose();

            // Stop screen agent
            if (_screenAgent != null)
            {
                await _screenAgent.StopAsync();
            }

            // Shutdown agent pool
            await _agentPool.ShutdownAsync(ct);

            // Dispose screen environment
            if (_screenEnvironment != null)
            {
                await _screenEnvironment.DisposeAsync();
            }

            // Dispose conflict resolver
            await _conflictResolver.DisposeAsync();

            _logToUI?.Invoke("[Orchestrator] Shutdown complete");
        }

        public async ValueTask DisposeAsync()
        {
            await ShutdownAsync();
        }
    }

    /// <summary>
    /// Status report for the swarm.
    /// </summary>
    public class SwarmStatus
    {
        public AgentPoolStatus AgentPoolStatus { get; init; } = new();
        public int PendingTasks { get; init; }
        public int CompletedTasks { get; init; }
        public int FailedTasks { get; init; }
        public GraphStatus GraphStatus { get; init; } = new();
        public bool ScreenAgentActive { get; init; }
        public string? ScreenEnvironment { get; init; }
    }

    /// <summary>
    /// Status of the dependency graph.
    /// </summary>
    public class GraphStatus
    {
        public int TotalTasks { get; init; }
        public int CompletedTasks { get; init; }
        public int FailedTasks { get; init; }
        public bool IsComplete { get; init; }
        public int MaxParallelism { get; init; }
    }
}
