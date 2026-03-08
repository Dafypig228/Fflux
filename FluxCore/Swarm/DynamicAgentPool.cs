using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluxCore.LLM;
using FluxCore.Swarm.Agents;
using FluxCore.Swarm.Infrastructure;

namespace FluxCore.Swarm
{
    /// <summary>
    /// Configuration for the dynamic agent pool.
    /// </summary>
    public class AgentPoolConfig
    {
        public int MinAgents { get; init; } = 2;
        public int MaxAgents { get; init; } = 50;
        public int TasksPerAgent { get; init; } = 3;
        public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(5);
        public bool AutoScaleEnabled { get; init; } = true;
    }

    /// <summary>
    /// Manages a dynamic pool of CodeAgents, scaling up/down based on workload.
    /// Optimizes API token usage by only running as many agents as needed.
    /// </summary>
    public class DynamicAgentPool : IAsyncDisposable
    {
        private readonly IMessageBus _messageBus;
        private readonly IAgentRegistry _registry;
        private readonly IFileLockManager _lockManager;
        private readonly ILLMService? _llm;
        private readonly AgentPoolConfig _config;
        private readonly Action<string>? _logToUI;

        private readonly List<CodeAgent> _activeAgents = new();
        private readonly object _agentsLock = new();
        private int _agentCounter = 0;

        // Track specialization distribution
        private readonly Dictionary<CodeAgentSpecialization, int> _specializationCounts = new();

        public int ActiveAgentCount
        {
            get { lock (_agentsLock) { return _activeAgents.Count; } }
        }

        public int IdleAgentCount
        {
            get { lock (_agentsLock) { return _activeAgents.Count(a => a.State == AgentState.Idle); } }
        }

        public int WorkingAgentCount
        {
            get { lock (_agentsLock) { return _activeAgents.Count(a => a.State == AgentState.Working); } }
        }

        public DynamicAgentPool(
            IMessageBus messageBus,
            IAgentRegistry registry,
            IFileLockManager lockManager,
            ILLMService? llm = null,
            AgentPoolConfig? config = null,
            Action<string>? logToUI = null)
        {
            _messageBus = messageBus;
            _registry = registry;
            _lockManager = lockManager;
            _llm = llm;
            _config = config ?? new AgentPoolConfig();
            _logToUI = logToUI;

            // Initialize specialization counts
            foreach (CodeAgentSpecialization spec in Enum.GetValues<CodeAgentSpecialization>())
            {
                _specializationCounts[spec] = 0;
            }
        }

        /// <summary>
        /// Estimate the optimal number of agents based on task list.
        /// Uses the dependency graph's max parallelism and task count.
        /// </summary>
        public int EstimateAgentCount(IEnumerable<AgentTask> tasks, DependencyGraph? graph = null)
        {
            var taskList = tasks.ToList();
            if (taskList.Count == 0) return _config.MinAgents;

            // Calculate max parallelism from dependency graph
            int maxParallel = graph?.GetMaxParallelism() ?? taskList.Count;

            // Estimate based on task count (1 agent per N tasks)
            int byTaskCount = (taskList.Count + _config.TasksPerAgent - 1) / _config.TasksPerAgent;

            // Take the minimum of parallelism and task-based estimate
            int optimal = Math.Min(maxParallel, byTaskCount);

            // Clamp to configured bounds
            int result = Math.Clamp(optimal, _config.MinAgents, _config.MaxAgents);

            _logToUI?.Invoke($"[AgentPool] Estimated {result} agents for {taskList.Count} tasks (max parallel: {maxParallel})");

            return result;
        }

        /// <summary>
        /// Estimate agent count based on task capabilities needed.
        /// This version considers what types of agents are needed.
        /// </summary>
        public int EstimateAgentCountByCapabilities(IEnumerable<AgentTask> tasks)
        {
            var taskList = tasks.ToList();
            if (taskList.Count == 0) return _config.MinAgents;

            // Group tasks by required capabilities
            var capabilityGroups = taskList
                .SelectMany(t => t.RequiredCapabilities)
                .GroupBy(c => c)
                .ToDictionary(g => g.Key, g => g.Count());

            // Count distinct capability sets needed
            int distinctCapabilities = capabilityGroups.Count;

            // At minimum, need one agent per distinct capability type
            // Plus extra for parallelism
            int byCapabilities = distinctCapabilities + (taskList.Count / _config.TasksPerAgent);

            return Math.Clamp(byCapabilities, _config.MinAgents, _config.MaxAgents);
        }

        /// <summary>
        /// Scale the agent pool to the target count.
        /// </summary>
        public async Task ScaleToAsync(int targetCount, CancellationToken ct = default)
        {
            targetCount = Math.Clamp(targetCount, _config.MinAgents, _config.MaxAgents);

            int current = ActiveAgentCount;
            _logToUI?.Invoke($"[AgentPool] Scaling from {current} to {targetCount} agents");

            if (targetCount > current)
            {
                await SpawnAgentsAsync(targetCount - current, ct);
            }
            else if (targetCount < current)
            {
                await RetireAgentsAsync(current - targetCount, ct);
            }
        }

        /// <summary>
        /// Spawn additional agents.
        /// </summary>
        public async Task SpawnAgentsAsync(int count, CancellationToken ct = default)
        {
            for (int i = 0; i < count && !ct.IsCancellationRequested; i++)
            {
                var specialization = GetNextSpecialization();
                var agent = CreateAgent(specialization);

                lock (_agentsLock)
                {
                    _activeAgents.Add(agent);
                    _specializationCounts[specialization]++;
                }

                await agent.StartAsync();
                _logToUI?.Invoke($"[AgentPool] Spawned agent {agent.Type}:{specialization} (total: {ActiveAgentCount})");
            }
        }

        /// <summary>
        /// Retire idle agents.
        /// </summary>
        public async Task RetireAgentsAsync(int count, CancellationToken ct = default)
        {
            for (int i = 0; i < count && !ct.IsCancellationRequested; i++)
            {
                CodeAgent? toRetire = null;

                lock (_agentsLock)
                {
                    // Prefer to retire idle agents
                    toRetire = _activeAgents.FirstOrDefault(a => a.State == AgentState.Idle);

                    if (toRetire == null && _activeAgents.Count > _config.MinAgents)
                    {
                        // If no idle agents, mark one for retirement when it becomes idle
                        _logToUI?.Invoke($"[AgentPool] No idle agents to retire, skipping");
                        break;
                    }

                    if (toRetire != null)
                    {
                        _activeAgents.Remove(toRetire);
                    }
                }

                if (toRetire != null)
                {
                    await toRetire.StopAsync();
                    _logToUI?.Invoke($"[AgentPool] Retired agent (total: {ActiveAgentCount})");
                }
            }
        }

        /// <summary>
        /// Retire agents that have been idle for too long.
        /// </summary>
        public async Task RetireIdleAgentsAsync(CancellationToken ct = default)
        {
            List<CodeAgent> toRetire = new();

            lock (_agentsLock)
            {
                // Keep at least MinAgents
                if (_activeAgents.Count <= _config.MinAgents)
                    return;

                // Find agents that are idle
                var idleAgents = _activeAgents
                    .Where(a => a.State == AgentState.Idle)
                    .ToList();

                // Retire excess idle agents (keep MinAgents total)
                int toRetireCount = Math.Min(
                    idleAgents.Count,
                    _activeAgents.Count - _config.MinAgents);

                toRetire = idleAgents.Take(toRetireCount).ToList();

                foreach (var agent in toRetire)
                {
                    _activeAgents.Remove(agent);
                }
            }

            foreach (var agent in toRetire)
            {
                await agent.StopAsync();
            }

            if (toRetire.Count > 0)
            {
                _logToUI?.Invoke($"[AgentPool] Retired {toRetire.Count} idle agents (total: {ActiveAgentCount})");
            }
        }

        /// <summary>
        /// Get the next specialization to use for a new agent.
        /// Balances the distribution of specializations.
        /// </summary>
        private CodeAgentSpecialization GetNextSpecialization()
        {
            // Priority order for specializations (most commonly needed first)
            var priorityOrder = new[]
            {
                CodeAgentSpecialization.GeneralPurpose,
                CodeAgentSpecialization.CSharpDeveloper,
                CodeAgentSpecialization.FileManager,
                CodeAgentSpecialization.PythonDeveloper,
                CodeAgentSpecialization.TestRunner,
                CodeAgentSpecialization.JavaScriptDeveloper,
                CodeAgentSpecialization.CodeReviewer,
                CodeAgentSpecialization.Refactorer,
                CodeAgentSpecialization.DocWriter,
                CodeAgentSpecialization.TypeScriptDeveloper
            };

            lock (_agentsLock)
            {
                // Find the specialization with the lowest count
                return priorityOrder
                    .OrderBy(s => _specializationCounts.GetValueOrDefault(s, 0))
                    .First();
            }
        }

        /// <summary>
        /// Get a specialization based on required capabilities.
        /// </summary>
        public CodeAgentSpecialization GetSpecializationForCapabilities(string[] capabilities)
        {
            // Map capabilities to specializations
            if (capabilities.Contains("csharp") || capabilities.Contains("dotnet"))
                return CodeAgentSpecialization.CSharpDeveloper;

            if (capabilities.Contains("python"))
                return CodeAgentSpecialization.PythonDeveloper;

            if (capabilities.Contains("javascript") || capabilities.Contains("node"))
                return CodeAgentSpecialization.JavaScriptDeveloper;

            if (capabilities.Contains("typescript"))
                return CodeAgentSpecialization.TypeScriptDeveloper;

            if (capabilities.Contains("test") || capabilities.Contains("unittest"))
                return CodeAgentSpecialization.TestRunner;

            if (capabilities.Contains("file-edit") || capabilities.Contains("file-move"))
                return CodeAgentSpecialization.FileManager;

            if (capabilities.Contains("review") || capabilities.Contains("analyze"))
                return CodeAgentSpecialization.CodeReviewer;

            if (capabilities.Contains("refactor"))
                return CodeAgentSpecialization.Refactorer;

            if (capabilities.Contains("documentation"))
                return CodeAgentSpecialization.DocWriter;

            return CodeAgentSpecialization.GeneralPurpose;
        }

        /// <summary>
        /// Create a new CodeAgent with the specified specialization.
        /// </summary>
        private CodeAgent CreateAgent(CodeAgentSpecialization specialization)
        {
            var agentId = $"code-agent-{Interlocked.Increment(ref _agentCounter):D3}";

            return new CodeAgent(
                agentId,
                specialization,
                _messageBus,
                _lockManager,
                _registry,
                _llm,
                _logToUI);
        }

        /// <summary>
        /// Get status report of the agent pool.
        /// </summary>
        public AgentPoolStatus GetStatus()
        {
            lock (_agentsLock)
            {
                return new AgentPoolStatus
                {
                    TotalAgents = _activeAgents.Count,
                    IdleAgents = _activeAgents.Count(a => a.State == AgentState.Idle),
                    WorkingAgents = _activeAgents.Count(a => a.State == AgentState.Working),
                    ErrorAgents = _activeAgents.Count(a => a.State == AgentState.Error),
                    SpecializationDistribution = _specializationCounts.ToDictionary(
                        kv => kv.Key.ToString(),
                        kv => kv.Value),
                    MinAgents = _config.MinAgents,
                    MaxAgents = _config.MaxAgents
                };
            }
        }

        /// <summary>
        /// Shutdown all agents gracefully.
        /// </summary>
        public async Task ShutdownAsync(CancellationToken ct = default)
        {
            _logToUI?.Invoke($"[AgentPool] Shutting down {ActiveAgentCount} agents...");

            List<CodeAgent> agents;
            lock (_agentsLock)
            {
                agents = _activeAgents.ToList();
                _activeAgents.Clear();
            }

            var stopTasks = agents.Select(a => a.StopAsync()).ToList();
            await Task.WhenAll(stopTasks);

            _logToUI?.Invoke($"[AgentPool] All agents stopped");
        }

        public async ValueTask DisposeAsync()
        {
            await ShutdownAsync();
        }
    }

    /// <summary>
    /// Status report for the agent pool.
    /// </summary>
    public class AgentPoolStatus
    {
        public int TotalAgents { get; init; }
        public int IdleAgents { get; init; }
        public int WorkingAgents { get; init; }
        public int ErrorAgents { get; init; }
        public Dictionary<string, int> SpecializationDistribution { get; init; } = new();
        public int MinAgents { get; init; }
        public int MaxAgents { get; init; }
    }
}
