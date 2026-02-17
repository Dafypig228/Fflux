using System;
using System.Threading;
using System.Threading.Tasks;
using FluxCore.Swarm.Infrastructure;

namespace FluxCore.Swarm.Agents
{
    /// <summary>
    /// Result of an agent executing a task.
    /// </summary>
    public class AgentExecutionResult
    {
        public bool Success { get; init; }
        public string Output { get; init; } = "";
        public string? Error { get; init; }
        public string? ScreenshotBase64 { get; set; }  // Changed to set for reassignment
        public FluxCore.ValidationResult? ValidationResult { get; set; }  // Use FluxCore.ValidationResult
        public TimeSpan Duration { get; init; }
    }

    /// <summary>
    /// Abstract base class for all agents in the swarm.
    /// Provides common functionality for registration, heartbeat, and task execution.
    /// </summary>
    public abstract class BaseAgent : IAsyncDisposable
    {
        protected readonly string AgentId;
        protected readonly IMessageBus MessageBus;
        protected readonly IFileLockManager LockManager;
        protected readonly IAgentRegistry Registry;
        protected readonly Action<string> LogToUI;

        private CancellationTokenSource? _cts;
        private Task? _runLoopTask;
        private System.Threading.Timer? _heartbeatTimer;
        private readonly TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(10);

        public AgentState State { get; protected set; } = AgentState.Initializing;
        public abstract string[] Capabilities { get; }
        public abstract AgentType Type { get; }

        protected BaseAgent(
            string agentId,
            IMessageBus messageBus,
            IFileLockManager lockManager,
            IAgentRegistry registry,
            Action<string>? logToUI = null)
        {
            AgentId = agentId;
            MessageBus = messageBus;
            LockManager = lockManager;
            Registry = registry;
            LogToUI = logToUI ?? (msg => System.Diagnostics.Debug.WriteLine($"[{AgentId}] {msg}"));
        }

        /// <summary>
        /// Start the agent's main execution loop.
        /// </summary>
        public async Task StartAsync()
        {
            LogToUI($"Starting agent {AgentId}...");

            // Register with the registry
            await Registry.RegisterAsync(new AgentInfo
            {
                AgentId = AgentId,
                Type = Type,
                Capabilities = Capabilities,
                State = AgentState.Idle
            });

            State = AgentState.Idle;
            await Registry.UpdateStateAsync(AgentId, State);

            // Start heartbeat
            _heartbeatTimer = new System.Threading.Timer(async _ =>
            {
                try
                {
                    await Registry.HeartbeatAsync(AgentId);
                }
                catch { /* Ignore heartbeat errors */ }
            }, null, _heartbeatInterval, _heartbeatInterval);

            // Start the main run loop
            _cts = new CancellationTokenSource();
            _runLoopTask = RunLoopAsync(_cts.Token);

            LogToUI($"Agent {AgentId} started successfully.");
        }

        /// <summary>
        /// Stop the agent gracefully.
        /// </summary>
        public async Task StopAsync()
        {
            LogToUI($"Stopping agent {AgentId}...");

            _cts?.Cancel();

            if (_runLoopTask != null)
            {
                try
                {
                    await _runLoopTask.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (TimeoutException)
                {
                    LogToUI($"Agent {AgentId} did not stop gracefully, forcing termination.");
                }
                catch (OperationCanceledException)
                {
                    // Expected
                }
            }

            _heartbeatTimer?.Dispose();

            // Release all locks held by this agent
            await LockManager.ReleaseAllLocksAsync(AgentId);

            State = AgentState.Terminated;
            await Registry.UpdateStateAsync(AgentId, State);
            await Registry.UnregisterAsync(AgentId);

            LogToUI($"Agent {AgentId} stopped.");
        }

        /// <summary>
        /// The main execution loop. Override this in derived classes.
        /// </summary>
        protected abstract Task RunLoopAsync(CancellationToken ct);

        /// <summary>
        /// Report progress on the current task.
        /// </summary>
        protected async Task ReportProgressAsync(string taskId, double progress, string status)
        {
            await MessageBus.PublishAsync(new ProgressMessage
            {
                SenderId = AgentId,
                TaskId = taskId,
                Progress = progress,
                Status = status
            });
        }

        /// <summary>
        /// Report task completion.
        /// </summary>
        protected async Task ReportTaskCompletedAsync(string taskId, bool success, string result, TimeSpan duration, string? screenshotBase64 = null)
        {
            await MessageBus.PublishAsync(new TaskCompletedMessage
            {
                SenderId = AgentId,
                TaskId = taskId,
                Success = success,
                Result = result,
                Duration = duration,
                ScreenshotBase64 = screenshotBase64
            });

            await Registry.RecordTaskCompletionAsync(AgentId, success, duration);
        }

        /// <summary>
        /// Report task failure.
        /// </summary>
        protected async Task ReportTaskFailedAsync(string taskId, string error, int retryCount = 0, bool isPermanent = false, string? screenshotBase64 = null)
        {
            await MessageBus.PublishAsync(new TaskFailedMessage
            {
                SenderId = AgentId,
                TaskId = taskId,
                Error = error,
                RetryCount = retryCount,
                IsPermanentFailure = isPermanent,
                ScreenshotBase64 = screenshotBase64
            });
        }

        /// <summary>
        /// Acquire a file lock with timeout.
        /// </summary>
        protected async Task<IFileLock?> AcquireFileLockAsync(string path, TimeSpan timeout)
        {
            return await LockManager.TryAcquireLockAsync(path, AgentId, timeout);
        }

        /// <summary>
        /// Update agent state in the registry.
        /// </summary>
        protected async Task SetStateAsync(AgentState state, string? taskId = null)
        {
            State = state;
            await Registry.UpdateStateAsync(AgentId, state, taskId);
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            _cts?.Dispose();
            _heartbeatTimer?.Dispose();
        }
    }
}
