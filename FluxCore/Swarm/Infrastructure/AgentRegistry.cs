using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FluxCore.Swarm.Infrastructure
{
    /// <summary>
    /// Current state of an agent.
    /// </summary>
    public enum AgentState
    {
        Initializing,
        Idle,
        Working,
        Waiting,
        Error,
        Terminated
    }

    /// <summary>
    /// Metrics tracked for each agent.
    /// </summary>
    public class AgentMetrics
    {
        public int TasksCompleted { get; set; }
        public int TasksFailed { get; set; }
        public TimeSpan TotalWorkTime { get; set; }
        public DateTime? LastTaskStarted { get; set; }
        public DateTime? LastTaskCompleted { get; set; }

        public double SuccessRate =>
            TasksCompleted + TasksFailed == 0 ? 1.0 :
            TasksCompleted / (double)(TasksCompleted + TasksFailed);

        public double AverageTaskDuration =>
            TasksCompleted == 0 ? 0 :
            TotalWorkTime.TotalSeconds / TasksCompleted;
    }

    /// <summary>
    /// Information about a registered agent.
    /// </summary>
    public class AgentInfo
    {
        public string AgentId { get; init; } = "";
        public AgentType Type { get; init; }
        public string[] Capabilities { get; init; } = Array.Empty<string>();
        public AgentState State { get; set; } = AgentState.Initializing;
        public string? CurrentTaskId { get; set; }
        public DateTime RegisteredAt { get; init; } = DateTime.UtcNow;
        public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;
        public AgentMetrics Metrics { get; set; } = new();
        public string? LastError { get; set; }
    }

    /// <summary>
    /// Health report for the entire swarm.
    /// </summary>
    public class SwarmHealthReport
    {
        public int TotalAgents { get; init; }
        public int IdleAgents { get; init; }
        public int WorkingAgents { get; init; }
        public int ErrorAgents { get; init; }
        public int TerminatedAgents { get; init; }
        public double AverageSuccessRate { get; init; }
        public int TotalTasksCompleted { get; init; }
        public int TotalTasksFailed { get; init; }
        public DateTime ReportTime { get; init; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Registry for tracking all agents in the swarm.
    /// </summary>
    public interface IAgentRegistry
    {
        Task RegisterAsync(AgentInfo agent);
        Task UnregisterAsync(string agentId);
        Task UpdateStateAsync(string agentId, AgentState state, string? taskId = null);
        Task HeartbeatAsync(string agentId);
        Task RecordTaskCompletionAsync(string agentId, bool success, TimeSpan duration);
        Task<AgentInfo?> GetAgentAsync(string agentId);
        Task<IReadOnlyList<AgentInfo>> GetAgentsByTypeAsync(AgentType type);
        Task<IReadOnlyList<AgentInfo>> GetAgentsByCapabilityAsync(string capability);
        Task<IReadOnlyList<AgentInfo>> GetIdleAgentsAsync();
        Task<IReadOnlyList<AgentInfo>> GetAllAgentsAsync();
        Task<SwarmHealthReport> GetSwarmHealthAsync();

        // Events
        event Action<AgentInfo>? OnAgentRegistered;
        event Action<string>? OnAgentUnregistered;
        event Action<string, AgentState>? OnAgentStateChanged;
    }

    /// <summary>
    /// In-memory implementation of the agent registry.
    /// </summary>
    public class AgentRegistry : IAgentRegistry
    {
        private readonly ConcurrentDictionary<string, AgentInfo> _agents = new();
        private readonly TimeSpan _heartbeatTimeout;
        private readonly System.Threading.Timer _healthCheckTimer;

        public event Action<AgentInfo>? OnAgentRegistered;
        public event Action<string>? OnAgentUnregistered;
        public event Action<string, AgentState>? OnAgentStateChanged;

        public AgentRegistry(TimeSpan? heartbeatTimeout = null)
        {
            _heartbeatTimeout = heartbeatTimeout ?? TimeSpan.FromMinutes(1);

            // Periodically check for dead agents
            _healthCheckTimer = new System.Threading.Timer(CheckAgentHealth, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        public Task RegisterAsync(AgentInfo agent)
        {
            if (_agents.TryAdd(agent.AgentId, agent))
            {
                OnAgentRegistered?.Invoke(agent);
            }
            else
            {
                // Update existing
                _agents[agent.AgentId] = agent;
            }

            return Task.CompletedTask;
        }

        public Task UnregisterAsync(string agentId)
        {
            if (_agents.TryRemove(agentId, out _))
            {
                OnAgentUnregistered?.Invoke(agentId);
            }

            return Task.CompletedTask;
        }

        public Task UpdateStateAsync(string agentId, AgentState state, string? taskId = null)
        {
            if (_agents.TryGetValue(agentId, out var agent))
            {
                var oldState = agent.State;
                agent.State = state;
                agent.CurrentTaskId = taskId;
                agent.LastHeartbeat = DateTime.UtcNow;

                if (state == AgentState.Working && taskId != null)
                {
                    agent.Metrics.LastTaskStarted = DateTime.UtcNow;
                }

                if (oldState != state)
                {
                    OnAgentStateChanged?.Invoke(agentId, state);
                }
            }

            return Task.CompletedTask;
        }

        public Task HeartbeatAsync(string agentId)
        {
            if (_agents.TryGetValue(agentId, out var agent))
            {
                agent.LastHeartbeat = DateTime.UtcNow;

                // If agent was in error state and sends heartbeat, mark as idle
                if (agent.State == AgentState.Error)
                {
                    agent.State = AgentState.Idle;
                    agent.LastError = null;
                }
            }

            return Task.CompletedTask;
        }

        public Task RecordTaskCompletionAsync(string agentId, bool success, TimeSpan duration)
        {
            if (_agents.TryGetValue(agentId, out var agent))
            {
                if (success)
                    agent.Metrics.TasksCompleted++;
                else
                    agent.Metrics.TasksFailed++;

                agent.Metrics.TotalWorkTime += duration;
                agent.Metrics.LastTaskCompleted = DateTime.UtcNow;
            }

            return Task.CompletedTask;
        }

        public Task<AgentInfo?> GetAgentAsync(string agentId)
        {
            _agents.TryGetValue(agentId, out var agent);
            return Task.FromResult(agent);
        }

        public Task<IReadOnlyList<AgentInfo>> GetAgentsByTypeAsync(AgentType type)
        {
            var agents = _agents.Values
                .Where(a => a.Type == type && a.State != AgentState.Terminated)
                .ToList();

            return Task.FromResult<IReadOnlyList<AgentInfo>>(agents);
        }

        public Task<IReadOnlyList<AgentInfo>> GetAgentsByCapabilityAsync(string capability)
        {
            var agents = _agents.Values
                .Where(a => a.Capabilities.Contains(capability) && a.State != AgentState.Terminated)
                .ToList();

            return Task.FromResult<IReadOnlyList<AgentInfo>>(agents);
        }

        public Task<IReadOnlyList<AgentInfo>> GetIdleAgentsAsync()
        {
            var agents = _agents.Values
                .Where(a => a.State == AgentState.Idle)
                .ToList();

            return Task.FromResult<IReadOnlyList<AgentInfo>>(agents);
        }

        public Task<IReadOnlyList<AgentInfo>> GetAllAgentsAsync()
        {
            return Task.FromResult<IReadOnlyList<AgentInfo>>(_agents.Values.ToList());
        }

        public Task<SwarmHealthReport> GetSwarmHealthAsync()
        {
            var agents = _agents.Values.ToList();

            var report = new SwarmHealthReport
            {
                TotalAgents = agents.Count,
                IdleAgents = agents.Count(a => a.State == AgentState.Idle),
                WorkingAgents = agents.Count(a => a.State == AgentState.Working),
                ErrorAgents = agents.Count(a => a.State == AgentState.Error),
                TerminatedAgents = agents.Count(a => a.State == AgentState.Terminated),
                AverageSuccessRate = agents.Count == 0 ? 1.0 : agents.Average(a => a.Metrics.SuccessRate),
                TotalTasksCompleted = agents.Sum(a => a.Metrics.TasksCompleted),
                TotalTasksFailed = agents.Sum(a => a.Metrics.TasksFailed)
            };

            return Task.FromResult(report);
        }

        private void CheckAgentHealth(object? state)
        {
            var now = DateTime.UtcNow;

            foreach (var agent in _agents.Values)
            {
                // Check for missed heartbeats
                if (agent.State != AgentState.Terminated &&
                    agent.State != AgentState.Error &&
                    now - agent.LastHeartbeat > _heartbeatTimeout)
                {
                    agent.State = AgentState.Error;
                    agent.LastError = "Heartbeat timeout";
                    OnAgentStateChanged?.Invoke(agent.AgentId, AgentState.Error);
                }
            }
        }
    }
}
