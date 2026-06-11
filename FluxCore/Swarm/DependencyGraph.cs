using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluxCore.Swarm.Infrastructure;

namespace FluxCore.Swarm
{
    /// <summary>
    /// Manages task dependencies and ensures tasks execute in the correct order.
    /// Supports parallel execution of independent tasks.
    /// </summary>
    public class DependencyGraph
    {
        private readonly Dictionary<string, AgentTask> _tasks = new();
        private readonly Dictionary<string, HashSet<string>> _dependsOn = new();
        private readonly Dictionary<string, HashSet<string>> _dependents = new();
        private readonly ConcurrentDictionary<string, TaskStatus> _status = new();
        private readonly ConcurrentDictionary<string, bool> _enqueued = new();
        private readonly Action<string>? _logToUI;

        public enum TaskStatus
        {
            Pending,
            Enqueued,
            Running,
            Completed,
            Failed
        }

        public DependencyGraph(Action<string>? logToUI = null)
        {
            _logToUI = logToUI;
        }

        /// <summary>
        /// Load tasks into the dependency graph.
        /// </summary>
        public void LoadTasks(IEnumerable<AgentTask> tasks)
        {
            Clear();

            foreach (var task in tasks)
            {
                _tasks[task.TaskId] = task;
                _status[task.TaskId] = TaskStatus.Pending;
                _dependsOn[task.TaskId] = new HashSet<string>(task.DependsOnTaskIds);
                _dependents[task.TaskId] = new HashSet<string>();
            }

            // Build reverse dependency map (who depends on me)
            foreach (var task in _tasks.Values)
            {
                foreach (var depId in task.DependsOnTaskIds)
                {
                    if (_dependents.ContainsKey(depId))
                    {
                        _dependents[depId].Add(task.TaskId);
                    }
                }
            }

            // Validate: check for missing dependencies
            foreach (var task in _tasks.Values)
            {
                foreach (var depId in task.DependsOnTaskIds)
                {
                    if (!_tasks.ContainsKey(depId))
                    {
                        _logToUI?.Invoke($"[DependencyGraph] Warning: Task {task.TaskId} depends on unknown task {depId}");
                    }
                }
            }

            _logToUI?.Invoke($"[DependencyGraph] Loaded {_tasks.Count} tasks with {_dependsOn.Values.Sum(d => d.Count)} dependencies");
        }

        /// <summary>
        /// Clear all tasks and state.
        /// </summary>
        public void Clear()
        {
            _tasks.Clear();
            _dependsOn.Clear();
            _dependents.Clear();
            _status.Clear();
            _enqueued.Clear();
        }

        /// <summary>
        /// Get tasks that are ready to execute (all dependencies completed).
        /// </summary>
        public IEnumerable<AgentTask> GetReadyTasks()
        {
            foreach (var task in _tasks.Values)
            {
                if (_status.TryGetValue(task.TaskId, out var status) && status == TaskStatus.Pending)
                {
                    if (!_enqueued.ContainsKey(task.TaskId) && AreDependenciesCompleted(task.TaskId))
                    {
                        yield return task;
                    }
                }
            }
        }

        /// <summary>
        /// Check if all dependencies for a task are completed.
        /// </summary>
        public bool AreDependenciesCompleted(string taskId)
        {
            if (!_dependsOn.TryGetValue(taskId, out var deps))
                return true;

            return deps.All(depId =>
            {
                if (_status.TryGetValue(depId, out var status))
                {
                    return status == TaskStatus.Completed;
                }
                // Unknown dependency - treat as completed (with warning logged during load)
                return true;
            });
        }

        /// <summary>
        /// Mark a task as enqueued (sent to message bus).
        /// </summary>
        public void MarkEnqueued(string taskId)
        {
            _enqueued[taskId] = true;
            _status[taskId] = TaskStatus.Enqueued;
        }

        /// <summary>
        /// Mark a task as running.
        /// </summary>
        public void MarkRunning(string taskId)
        {
            _status[taskId] = TaskStatus.Running;
        }

        /// <summary>
        /// Mark a task as completed.
        /// </summary>
        public void MarkCompleted(string taskId)
        {
            _status[taskId] = TaskStatus.Completed;
            _logToUI?.Invoke($"[DependencyGraph] Task {taskId} completed. {GetCompletedCount()}/{_tasks.Count} done.");
        }

        /// <summary>
        /// Mark a task as failed.
        /// </summary>
        public void MarkFailed(string taskId)
        {
            _status[taskId] = TaskStatus.Failed;
            _logToUI?.Invoke($"[DependencyGraph] Task {taskId} failed.");
        }

        /// <summary>
        /// Get the current status of a task.
        /// </summary>
        public TaskStatus GetTaskStatus(string taskId)
        {
            return _status.GetValueOrDefault(taskId, TaskStatus.Pending);
        }

        /// <summary>
        /// Get a task by ID.
        /// </summary>
        public AgentTask? GetTask(string taskId)
        {
            return _tasks.GetValueOrDefault(taskId);
        }

        /// <summary>
        /// Get all tasks.
        /// </summary>
        public IReadOnlyList<AgentTask> GetAllTasks()
        {
            return _tasks.Values.ToList();
        }

        /// <summary>
        /// Get count of completed tasks.
        /// </summary>
        public int GetCompletedCount()
        {
            return _status.Values.Count(s => s == TaskStatus.Completed);
        }

        /// <summary>
        /// Get count of failed tasks.
        /// </summary>
        public int GetFailedCount()
        {
            return _status.Values.Count(s => s == TaskStatus.Failed);
        }

        /// <summary>
        /// Check if all tasks are done (completed or failed).
        /// </summary>
        public bool IsComplete()
        {
            return _status.Values.All(s => s == TaskStatus.Completed || s == TaskStatus.Failed);
        }

        /// <summary>
        /// Get the maximum parallelism (how many tasks can run at once).
        /// This is used to estimate optimal agent count.
        /// </summary>
        public int GetMaxParallelism()
        {
            if (_tasks.Count == 0) return 0;

            // Calculate levels using topological sort
            var levels = new Dictionary<string, int>();
            var visited = new HashSet<string>();

            int GetLevel(string taskId)
            {
                if (levels.ContainsKey(taskId))
                    return levels[taskId];

                if (visited.Contains(taskId))
                {
                    // Cycle detected
                    return 0;
                }

                visited.Add(taskId);

                if (!_dependsOn.TryGetValue(taskId, out var deps) || deps.Count == 0)
                {
                    levels[taskId] = 0;
                    return 0;
                }

                int maxDepLevel = deps
                    .Where(d => _tasks.ContainsKey(d))
                    .Select(GetLevel)
                    .DefaultIfEmpty(-1)
                    .Max();

                levels[taskId] = maxDepLevel + 1;
                return levels[taskId];
            }

            foreach (var taskId in _tasks.Keys)
            {
                GetLevel(taskId);
            }

            // Count tasks at each level
            var levelCounts = levels
                .GroupBy(kv => kv.Value)
                .ToDictionary(g => g.Key, g => g.Count());

            // Maximum parallelism is the largest level
            return levelCounts.Values.DefaultIfEmpty(1).Max();
        }

        /// <summary>
        /// Execute tasks in dependency order, enqueueing to the message bus as they become ready.
        /// </summary>
        public async Task ExecuteAsync(IMessageBus messageBus, CancellationToken ct)
        {
            _logToUI?.Invoke($"[DependencyGraph] Starting execution of {_tasks.Count} tasks (max parallelism: {GetMaxParallelism()})");

            while (!IsComplete() && !ct.IsCancellationRequested)
            {
                var readyTasks = GetReadyTasks().ToList();

                foreach (var task in readyTasks)
                {
                    MarkEnqueued(task.TaskId);
                    await messageBus.EnqueueTaskAsync(task, ct);
                    _logToUI?.Invoke($"[DependencyGraph] Enqueued task {task.TaskId}: {task.Description}");
                }

                // Brief delay before checking again
                await Task.Delay(50, ct);
            }

            if (ct.IsCancellationRequested)
            {
                _logToUI?.Invoke($"[DependencyGraph] Execution cancelled");
            }
            else
            {
                _logToUI?.Invoke($"[DependencyGraph] Execution complete: {GetCompletedCount()} completed, {GetFailedCount()} failed");
            }
        }

        /// <summary>
        /// Get tasks that are blocked by a specific task.
        /// </summary>
        public IEnumerable<string> GetBlockedBy(string taskId)
        {
            if (_dependents.TryGetValue(taskId, out var blocked))
            {
                return blocked;
            }
            return Enumerable.Empty<string>();
        }

        /// <summary>
        /// Get a visual representation of the dependency graph for debugging.
        /// </summary>
        public string ToDebugString()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== Dependency Graph ===");

            foreach (var task in _tasks.Values.OrderBy(t => t.TaskId))
            {
                var status = _status.GetValueOrDefault(task.TaskId, TaskStatus.Pending);
                var deps = _dependsOn.GetValueOrDefault(task.TaskId, new HashSet<string>());
                var depsStr = deps.Count > 0 ? $" → depends on [{string.Join(", ", deps)}]" : " → no dependencies";

                sb.AppendLine($"  [{status}] {task.TaskId}: {task.Description}{depsStr}");
            }

            sb.AppendLine($"Max Parallelism: {GetMaxParallelism()}");
            return sb.ToString();
        }
    }
}
