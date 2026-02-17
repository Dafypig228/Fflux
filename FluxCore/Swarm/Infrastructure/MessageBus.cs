using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace FluxCore.Swarm.Infrastructure
{
    /// <summary>
    /// Message priority levels for task scheduling.
    /// </summary>
    public enum MessagePriority { Low, Normal, High, Critical }

    /// <summary>
    /// Base interface for all messages in the swarm.
    /// </summary>
    public interface IMessage
    {
        string MessageId { get; }
        string SenderId { get; }
        string? TargetAgentId { get; }  // null = broadcast to all
        DateTime Timestamp { get; }
        MessagePriority Priority { get; }
    }

    /// <summary>
    /// Base message implementation with common properties.
    /// </summary>
    public abstract class MessageBase : IMessage
    {
        public string MessageId { get; init; } = Guid.NewGuid().ToString();
        public string SenderId { get; init; } = "";
        public string? TargetAgentId { get; init; }
        public DateTime Timestamp { get; init; } = DateTime.UtcNow;
        public MessagePriority Priority { get; init; } = MessagePriority.Normal;
    }

    /// <summary>
    /// Message sent when a task is completed.
    /// </summary>
    public class TaskCompletedMessage : MessageBase
    {
        public string TaskId { get; init; } = "";
        public bool Success { get; init; }
        public string Result { get; init; } = "";
        public string? ScreenshotBase64 { get; init; }
        public TimeSpan Duration { get; init; }
    }

    /// <summary>
    /// Message sent when a task fails.
    /// </summary>
    public class TaskFailedMessage : MessageBase
    {
        public string TaskId { get; init; } = "";
        public string Error { get; init; } = "";
        public string? ScreenshotBase64 { get; init; }
        public bool IsPermanentFailure { get; init; }
        public int RetryCount { get; init; }
    }

    /// <summary>
    /// Message to assign a task to a specific agent.
    /// </summary>
    public class AssignTaskMessage : MessageBase
    {
        public AgentTask Task { get; init; } = null!;
    }

    /// <summary>
    /// Progress update from an agent.
    /// </summary>
    public class ProgressMessage : MessageBase
    {
        public string TaskId { get; init; } = "";
        public double Progress { get; init; }  // 0.0 - 1.0
        public string Status { get; init; } = "";
    }

    /// <summary>
    /// Request to resolve a file lock conflict.
    /// </summary>
    public class LockConflictRequest : MessageBase
    {
        public string FilePath { get; init; } = "";
        public string RequestingAgentId { get; init; } = "";
    }

    /// <summary>
    /// Response to a lock conflict request.
    /// </summary>
    public class LockConflictResponse : MessageBase
    {
        public bool ShouldProceed { get; init; }
        public string? PreemptedAgentId { get; init; }
        public string? AlternativeAction { get; init; }
        public string? MergeTargetAgentId { get; init; }
        public TimeSpan? EstimatedWaitTime { get; init; }
    }

    /// <summary>
    /// Represents a task that can be executed by an agent.
    /// </summary>
    public class AgentTask
    {
        public string TaskId { get; init; } = Guid.NewGuid().ToString();
        public string Description { get; init; } = "";
        public AgentType RequiredAgentType { get; init; }
        public string[] RequiredCapabilities { get; init; } = Array.Empty<string>();
        public string[] FilesToAccess { get; init; } = Array.Empty<string>();
        public string[] DependsOnTaskIds { get; init; } = Array.Empty<string>();
        public MessagePriority Priority { get; init; } = MessagePriority.Normal;
        public bool RequiresScreenAccess { get; init; }
        public bool RequiresVisualVerification { get; init; }
        public string? AssignedAgentId { get; set; }
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
        public TimeSpan? Timeout { get; init; }

        // Task payload - what to actually do
        public string CommandType { get; init; } = "";
        public string CommandArgs { get; init; } = "";
        public string? CodeToExecute { get; init; }
        public string? TargetFilePath { get; init; }
    }

    /// <summary>
    /// Types of agents in the swarm.
    /// </summary>
    public enum AgentType
    {
        Code,       // Background code agents
        Screen,     // Screen agent with VM access
        Orchestrator // The main coordinator
    }

    /// <summary>
    /// Subscription handle for message bus.
    /// </summary>
    internal class Subscription : IDisposable
    {
        private readonly Action _unsubscribe;
        public Subscription(Action unsubscribe) => _unsubscribe = unsubscribe;
        public void Dispose() => _unsubscribe();
    }

    /// <summary>
    /// Central message bus for inter-agent communication.
    /// Supports pub/sub, request/response, and task queuing.
    /// </summary>
    public interface IMessageBus
    {
        // Pub/Sub
        Task PublishAsync<T>(T message, CancellationToken ct = default) where T : IMessage;
        IDisposable Subscribe<T>(string agentId, Func<T, Task> handler) where T : IMessage;

        // Request/Response (for synchronous-style communication)
        Task<TResponse> RequestAsync<TRequest, TResponse>(
            TRequest request,
            string targetAgentId,
            TimeSpan timeout,
            CancellationToken ct = default)
            where TRequest : IMessage
            where TResponse : IMessage;

        // Task Queue
        Task EnqueueTaskAsync(AgentTask task, CancellationToken ct = default);
        Task<AgentTask?> DequeueTaskAsync(string agentId, string[] capabilities, CancellationToken ct = default);

        // Stats
        int PendingTaskCount { get; }
        int ActiveSubscriptionCount { get; }
    }

    /// <summary>
    /// In-memory implementation of the message bus using Channels.
    /// </summary>
    public class InMemoryMessageBus : IMessageBus
    {
        private readonly ConcurrentDictionary<Type, ConcurrentBag<(string AgentId, Delegate Handler)>> _subscriptions = new();
        private readonly Channel<AgentTask> _taskQueue;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<IMessage>> _pendingRequests = new();
        private readonly int _taskQueueCapacity;

        public int PendingTaskCount => _taskQueue.Reader.Count;
        public int ActiveSubscriptionCount => _subscriptions.Values.Sum(bag => bag.Count);

        public InMemoryMessageBus(int taskQueueCapacity = 1000)
        {
            _taskQueueCapacity = taskQueueCapacity;
            _taskQueue = Channel.CreateBounded<AgentTask>(new BoundedChannelOptions(taskQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait
            });
        }

        public async Task PublishAsync<T>(T message, CancellationToken ct = default) where T : IMessage
        {
            var messageType = typeof(T);

            // Check if this is a response to a pending request
            if (_pendingRequests.TryGetValue(message.MessageId, out var tcs))
            {
                tcs.TrySetResult(message);
                _pendingRequests.TryRemove(message.MessageId, out _);
                return;
            }

            // Broadcast to all subscribers of this message type
            if (_subscriptions.TryGetValue(messageType, out var handlers))
            {
                var tasks = new List<Task>();

                foreach (var (agentId, handler) in handlers)
                {
                    // Skip if message is targeted to a specific agent and this isn't it
                    if (message.TargetAgentId != null && message.TargetAgentId != agentId)
                        continue;

                    if (handler is Func<T, Task> typedHandler)
                    {
                        tasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                await typedHandler(message);
                            }
                            catch (Exception ex)
                            {
                                // Log error but don't crash the bus
                                System.Diagnostics.Debug.WriteLine($"[MessageBus] Handler error for {agentId}: {ex.Message}");
                            }
                        }, ct));
                    }
                }

                await Task.WhenAll(tasks);
            }
        }

        public IDisposable Subscribe<T>(string agentId, Func<T, Task> handler) where T : IMessage
        {
            var messageType = typeof(T);
            var bag = _subscriptions.GetOrAdd(messageType, _ => new ConcurrentBag<(string, Delegate)>());
            bag.Add((agentId, handler));

            return new Subscription(() =>
            {
                // Note: ConcurrentBag doesn't support direct removal, so we rebuild
                // This is acceptable for the expected low subscription churn
                if (_subscriptions.TryGetValue(messageType, out var currentBag))
                {
                    var newBag = new ConcurrentBag<(string AgentId, Delegate Handler)>(
                        currentBag.Where(x => x.AgentId != agentId || x.Handler != (Delegate)handler));
                    _subscriptions.TryUpdate(messageType, newBag, currentBag);
                }
            });
        }

        public async Task<TResponse> RequestAsync<TRequest, TResponse>(
            TRequest request,
            string targetAgentId,
            TimeSpan timeout,
            CancellationToken ct = default)
            where TRequest : IMessage
            where TResponse : IMessage
        {
            var tcs = new TaskCompletionSource<IMessage>();
            _pendingRequests[request.MessageId] = tcs;

            try
            {
                // Publish the request
                await PublishAsync(request, ct);

                // Wait for response with timeout
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);

                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(timeout, cts.Token));

                if (completedTask == tcs.Task)
                {
                    return (TResponse)await tcs.Task;
                }

                throw new TimeoutException($"Request to {targetAgentId} timed out after {timeout}");
            }
            finally
            {
                _pendingRequests.TryRemove(request.MessageId, out _);
            }
        }

        public async Task EnqueueTaskAsync(AgentTask task, CancellationToken ct = default)
        {
            await _taskQueue.Writer.WriteAsync(task, ct);
        }

        public async Task<AgentTask?> DequeueTaskAsync(string agentId, string[] capabilities, CancellationToken ct = default)
        {
            // Simple implementation: just get next task
            // A more sophisticated version would match capabilities
            try
            {
                if (await _taskQueue.Reader.WaitToReadAsync(ct))
                {
                    if (_taskQueue.Reader.TryRead(out var task))
                    {
                        // Check if agent has required capabilities
                        bool hasCapabilities = task.RequiredCapabilities.Length == 0 ||
                                               task.RequiredCapabilities.All(req => capabilities.Contains(req));

                        if (hasCapabilities)
                        {
                            task.AssignedAgentId = agentId;
                            return task;
                        }
                        else
                        {
                            // Put it back (not ideal, but simple)
                            await _taskQueue.Writer.WriteAsync(task, ct);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation
            }

            return null;
        }
    }
}
