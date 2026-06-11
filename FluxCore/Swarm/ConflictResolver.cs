using System;
using System.Threading;
using System.Threading.Tasks;
using FluxCore.Swarm.Infrastructure;

namespace FluxCore.Swarm
{
    /// <summary>
    /// Resolution strategy for file lock conflicts.
    /// </summary>
    public enum ConflictResolutionStrategy
    {
        /// <summary>Requester should wait for the lock to be released.</summary>
        Wait,

        /// <summary>Requester's task has higher priority - preempt current holder.</summary>
        Preempt,

        /// <summary>Both agents should coordinate to merge their changes.</summary>
        Merge,

        /// <summary>Requester should skip this file and continue with other work.</summary>
        Skip,

        /// <summary>Requester should abort and report failure.</summary>
        Abort
    }

    /// <summary>
    /// Extended lock conflict response with resolution details.
    /// </summary>
    public class ConflictResolution
    {
        public ConflictResolutionStrategy Strategy { get; init; }
        public string? Message { get; init; }
        public string? PreemptedAgentId { get; init; }
        public string? MergeTargetAgentId { get; init; }
        public TimeSpan? EstimatedWaitTime { get; init; }
        public int? SuggestedRetryCount { get; init; }
    }

    /// <summary>
    /// Resolves file lock conflicts between agents using priority-based decisions.
    /// </summary>
    public class ConflictResolver : IAsyncDisposable
    {
        private readonly IMessageBus _messageBus;
        private readonly IAgentRegistry _registry;
        private readonly IFileLockManager _lockManager;
        private readonly Action<string>? _logToUI;
        private IDisposable? _conflictSubscription;

        // Configuration
        private readonly TimeSpan _defaultWaitTime = TimeSpan.FromSeconds(30);
        private readonly int _maxWaitRetries = 3;

        public ConflictResolver(
            IMessageBus messageBus,
            IAgentRegistry registry,
            IFileLockManager lockManager,
            Action<string>? logToUI = null)
        {
            _messageBus = messageBus;
            _registry = registry;
            _lockManager = lockManager;
            _logToUI = logToUI;

            // Subscribe to conflict requests
            _conflictSubscription = _messageBus.Subscribe<LockConflictRequest>(
                "conflict-resolver",
                HandleConflictRequestAsync);
        }

        private async Task HandleConflictRequestAsync(LockConflictRequest request)
        {
            _logToUI?.Invoke($"[ConflictResolver] Received conflict request from {request.RequestingAgentId} for {request.FilePath}");

            var resolution = await ResolveAsync(request);

            // Send response back via message bus
            var response = new LockConflictResponse
            {
                SenderId = "conflict-resolver",
                TargetAgentId = request.RequestingAgentId,
                ShouldProceed = resolution.Strategy == ConflictResolutionStrategy.Preempt,
                PreemptedAgentId = resolution.PreemptedAgentId,
                MergeTargetAgentId = resolution.MergeTargetAgentId,
                EstimatedWaitTime = resolution.EstimatedWaitTime,
                AlternativeAction = resolution.Strategy.ToString()
            };

            await _messageBus.PublishAsync(response);
        }

        /// <summary>
        /// Resolve a lock conflict and determine the best course of action.
        /// </summary>
        public async Task<ConflictResolution> ResolveAsync(LockConflictRequest request, CancellationToken ct = default)
        {
            var currentOwner = await _lockManager.GetLockOwnerAsync(request.FilePath);

            if (string.IsNullOrEmpty(currentOwner))
            {
                // Lock is available now
                return new ConflictResolution
                {
                    Strategy = ConflictResolutionStrategy.Wait,
                    Message = "Lock is now available",
                    EstimatedWaitTime = TimeSpan.Zero
                };
            }

            // Get info about both agents
            var ownerInfo = await _registry.GetAgentAsync(currentOwner);
            var requesterInfo = await _registry.GetAgentAsync(request.RequestingAgentId);

            if (ownerInfo == null)
            {
                // Owner agent no longer exists - lock should be released
                _logToUI?.Invoke($"[ConflictResolver] Lock owner {currentOwner} not found - releasing stale lock");
                await _lockManager.ReleaseLockAsync(request.FilePath, currentOwner);
                return new ConflictResolution
                {
                    Strategy = ConflictResolutionStrategy.Wait,
                    Message = "Released stale lock",
                    EstimatedWaitTime = TimeSpan.Zero
                };
            }

            if (requesterInfo == null)
            {
                return new ConflictResolution
                {
                    Strategy = ConflictResolutionStrategy.Abort,
                    Message = "Requesting agent not registered"
                };
            }

            // Check if owner is in error state
            if (ownerInfo.State == AgentState.Error || ownerInfo.State == AgentState.Terminated)
            {
                _logToUI?.Invoke($"[ConflictResolver] Lock owner {currentOwner} is in {ownerInfo.State} state - releasing lock");
                await _lockManager.ReleaseLockAsync(request.FilePath, currentOwner);
                return new ConflictResolution
                {
                    Strategy = ConflictResolutionStrategy.Wait,
                    Message = "Released lock from errored agent",
                    EstimatedWaitTime = TimeSpan.Zero
                };
            }

            // Priority-based resolution
            // Higher priority tasks can preempt lower priority ones
            // For now, we use a simple wait strategy
            // TODO: Implement actual priority comparison when AgentTask has priority exposed

            // Estimate wait time based on owner's metrics
            var estimatedWait = ownerInfo.Metrics.AverageTaskDuration > 0
                ? TimeSpan.FromSeconds(ownerInfo.Metrics.AverageTaskDuration)
                : _defaultWaitTime;

            // Check if owner has been holding the lock too long
            // (This would require tracking lock acquisition time, which FileLockManager already does)

            _logToUI?.Invoke($"[ConflictResolver] Advising {request.RequestingAgentId} to wait ~{estimatedWait.TotalSeconds:F1}s for {currentOwner}");

            return new ConflictResolution
            {
                Strategy = ConflictResolutionStrategy.Wait,
                Message = $"Wait for {currentOwner} to complete",
                EstimatedWaitTime = estimatedWait,
                SuggestedRetryCount = _maxWaitRetries
            };
        }

        /// <summary>
        /// Request resolution for a lock conflict via the message bus.
        /// Returns the resolution after the ConflictResolver processes it.
        /// </summary>
        public async Task<LockConflictResponse> RequestResolutionAsync(
            string filePath,
            string requestingAgentId,
            TimeSpan timeout,
            CancellationToken ct = default)
        {
            var request = new LockConflictRequest
            {
                SenderId = requestingAgentId,
                FilePath = filePath,
                RequestingAgentId = requestingAgentId,
                Priority = MessagePriority.High
            };

            try
            {
                var response = await _messageBus.RequestAsync<LockConflictRequest, LockConflictResponse>(
                    request,
                    "conflict-resolver",
                    timeout,
                    ct);

                return response;
            }
            catch (TimeoutException)
            {
                _logToUI?.Invoke($"[ConflictResolver] Resolution request timed out for {filePath}");
                return new LockConflictResponse
                {
                    ShouldProceed = false,
                    EstimatedWaitTime = _defaultWaitTime,
                    AlternativeAction = "Wait"
                };
            }
        }

        /// <summary>
        /// Force release a lock (admin operation).
        /// </summary>
        public async Task ForceReleaseAsync(string filePath, string reason)
        {
            var owner = await _lockManager.GetLockOwnerAsync(filePath);
            if (!string.IsNullOrEmpty(owner))
            {
                _logToUI?.Invoke($"[ConflictResolver] Force releasing lock on {filePath} from {owner}: {reason}");
                await _lockManager.ReleaseLockAsync(filePath, owner);
            }
        }

        /// <summary>
        /// Get current lock conflicts (files with waiting agents).
        /// </summary>
        public async Task<int> GetActiveConflictCountAsync()
        {
            int count = 0;
            await foreach (var lockInfo in _lockManager.GetAllLocksAsync())
            {
                // A conflict exists if someone else is waiting
                // For now, just count active locks
                count++;
            }
            return count;
        }

        public ValueTask DisposeAsync()
        {
            _conflictSubscription?.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
