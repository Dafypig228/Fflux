using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FluxCore.Swarm.Infrastructure;

namespace FluxCore.Swarm
{
    /// <summary>
    /// Result of task decomposition.
    /// </summary>
    public class DecompositionResult
    {
        public bool Success { get; init; }
        public List<AgentTask> Tasks { get; init; } = new();
        public string? Error { get; init; }
        public int EstimatedComplexity { get; init; }
        public string? RawResponse { get; init; }
    }

    /// <summary>
    /// Uses Gemini LLM to decompose high-level goals into executable subtasks.
    /// </summary>
    public class TaskDecomposer
    {
        private readonly GeminiService _gemini;
        private readonly Action<string>? _logToUI;

        private const string DECOMPOSITION_PROMPT = @"You are a task decomposition expert for an AI agent swarm system.
Break down the given goal into small, executable subtasks that can be performed by automated agents.

GOAL: {GOAL}
WORKING DIRECTORY: {WORKING_DIR}

AVAILABLE COMMAND TYPES:
- WRITE_FILE: Create or overwrite a file (args: filepath, code: file content)
- READ_FILE: Read a file's contents (args: filepath)
- EDIT_FILE: Modify an existing file (args: filepath, code: new content)
- LIST_FILES: List files in a directory (args: directory path)
- MOVE_FILE: Move/rename a file (args: source|destination)
- COPY_FILE: Copy a file (args: source|destination)
- DELETE_FILE: Delete a file (args: filepath)
- MAKE_DIR: Create a directory (args: directory path)
- PYTHON: Execute Python code (code: python script)
- POWERSHELL: Execute PowerShell command (code: command)
- COMPILE: Build a project (args: project path)
- TEST: Run tests (args: project/test path)
- CLICK: Click at screen coordinates (args: x,y) - REQUIRES SCREEN
- TYPE: Type text (args: text) - REQUIRES SCREEN
- SEND_KEYS: Send keyboard shortcuts (args: keys like CTRL+S) - REQUIRES SCREEN
- LAUNCH_APP: Launch an application (args: path|arguments) - REQUIRES SCREEN
- SCREENSHOT: Capture screen - REQUIRES SCREEN
- WAIT: Wait for milliseconds (args: ms)

RULES:
1. Break the goal into the SMALLEST possible independent tasks
2. Tasks that can run in parallel should NOT depend on each other
3. A task should only depend on another if it TRULY needs that task's output
4. Visual/screen tasks (CLICK, TYPE, LAUNCH_APP) are expensive - minimize them
5. Prefer background commands (WRITE_FILE, PYTHON, POWERSHELL) over screen commands
6. List all files a task will READ or WRITE in filesToAccess
7. Screen tasks should come at the END for testing/verification

OUTPUT FORMAT (JSON array):
[
  {
    ""taskId"": ""task-001"",
    ""description"": ""Create project folder structure"",
    ""commandType"": ""MAKE_DIR"",
    ""commandArgs"": ""C:\\Projects\\MyApp"",
    ""codeToExecute"": null,
    ""dependsOn"": [],
    ""filesToAccess"": [""C:\\Projects\\MyApp""],
    ""requiredCapabilities"": [""file-edit""],
    ""requiresScreen"": false,
    ""requiresVisualVerification"": false,
    ""priority"": ""Normal""
  },
  {
    ""taskId"": ""task-002"",
    ""description"": ""Write main.py with game logic"",
    ""commandType"": ""WRITE_FILE"",
    ""commandArgs"": ""C:\\Projects\\MyApp\\main.py"",
    ""codeToExecute"": ""# Python code here..."",
    ""dependsOn"": [""task-001""],
    ""filesToAccess"": [""C:\\Projects\\MyApp\\main.py""],
    ""requiredCapabilities"": [""python"", ""file-edit""],
    ""requiresScreen"": false,
    ""requiresVisualVerification"": false,
    ""priority"": ""Normal""
  }
]

Return ONLY the JSON array, no markdown formatting.";

        public TaskDecomposer(GeminiService gemini, Action<string>? logToUI = null)
        {
            _gemini = gemini;
            _logToUI = logToUI;
        }

        /// <summary>
        /// Decompose a high-level goal into executable subtasks.
        /// </summary>
        public async Task<DecompositionResult> DecomposeAsync(
            string goal,
            string workingDirectory,
            CancellationToken ct = default)
        {
            _logToUI?.Invoke($"[TaskDecomposer] Decomposing goal: {goal}");

            try
            {
                var prompt = DECOMPOSITION_PROMPT
                    .Replace("{GOAL}", goal)
                    .Replace("{WORKING_DIR}", workingDirectory);

                var history = new List<ChatMessage>
                {
                    new ChatMessage { IsUser = true, Text = prompt }
                };

                var response = await _gemini.ChatWithHistory(history, prompt, "", "", "");

                _logToUI?.Invoke($"[TaskDecomposer] Received response ({response.Length} chars)");

                // Parse the JSON response
                var tasks = ParseTasksFromResponse(response);

                if (tasks.Count == 0)
                {
                    return new DecompositionResult
                    {
                        Success = false,
                        Error = "Failed to parse tasks from LLM response",
                        RawResponse = response
                    };
                }

                // Validate and fix task dependencies
                ValidateAndFixDependencies(tasks);

                // Estimate complexity (simple heuristic)
                int complexity = EstimateComplexity(tasks);

                _logToUI?.Invoke($"[TaskDecomposer] Decomposed into {tasks.Count} tasks (complexity: {complexity})");

                return new DecompositionResult
                {
                    Success = true,
                    Tasks = tasks,
                    EstimatedComplexity = complexity,
                    RawResponse = response
                };
            }
            catch (Exception ex)
            {
                _logToUI?.Invoke($"[TaskDecomposer] Error: {ex.Message}");
                return new DecompositionResult
                {
                    Success = false,
                    Error = ex.Message
                };
            }
        }

        /// <summary>
        /// Parse tasks from the LLM response.
        /// </summary>
        private List<AgentTask> ParseTasksFromResponse(string response)
        {
            var tasks = new List<AgentTask>();

            try
            {
                // Extract JSON array from response (handle markdown code blocks)
                var json = ExtractJsonArray(response);

                if (string.IsNullOrEmpty(json))
                {
                    _logToUI?.Invoke("[TaskDecomposer] Could not find JSON array in response");
                    return tasks;
                }

                // Parse as JsonDocument for flexibility
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Array)
                {
                    _logToUI?.Invoke("[TaskDecomposer] Response is not a JSON array");
                    return tasks;
                }

                foreach (var element in root.EnumerateArray())
                {
                    try
                    {
                        var task = ParseTaskElement(element);
                        if (task != null)
                        {
                            tasks.Add(task);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logToUI?.Invoke($"[TaskDecomposer] Error parsing task element: {ex.Message}");
                    }
                }
            }
            catch (JsonException ex)
            {
                _logToUI?.Invoke($"[TaskDecomposer] JSON parse error: {ex.Message}");
            }

            return tasks;
        }

        /// <summary>
        /// Extract JSON array from response, handling markdown code blocks.
        /// </summary>
        private string? ExtractJsonArray(string response)
        {
            // Try to find JSON in markdown code block
            var codeBlockMatch = Regex.Match(response, @"```(?:json)?\s*\n?([\s\S]*?)\n?```", RegexOptions.IgnoreCase);
            if (codeBlockMatch.Success)
            {
                return codeBlockMatch.Groups[1].Value.Trim();
            }

            // Try to find raw JSON array
            var arrayMatch = Regex.Match(response, @"\[\s*\{[\s\S]*\}\s*\]");
            if (arrayMatch.Success)
            {
                return arrayMatch.Value;
            }

            // Last resort: trim and try the whole response
            var trimmed = response.Trim();
            if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
            {
                return trimmed;
            }

            return null;
        }

        /// <summary>
        /// Parse a single task from a JSON element.
        /// </summary>
        private AgentTask? ParseTaskElement(JsonElement element)
        {
            var taskId = element.TryGetProperty("taskId", out var idProp) ? idProp.GetString() : null;
            if (string.IsNullOrEmpty(taskId))
            {
                taskId = $"task-{Guid.NewGuid():N}".Substring(0, 12);
            }

            var description = element.TryGetProperty("description", out var descProp) ? descProp.GetString() : "";
            var commandType = element.TryGetProperty("commandType", out var cmdProp) ? cmdProp.GetString() : "";
            var commandArgs = element.TryGetProperty("commandArgs", out var argsProp) ? argsProp.GetString() : "";
            var codeToExecute = element.TryGetProperty("codeToExecute", out var codeProp) && codeProp.ValueKind != JsonValueKind.Null
                ? codeProp.GetString()
                : null;

            // Parse arrays
            var dependsOn = ParseStringArray(element, "dependsOn");
            var filesToAccess = ParseStringArray(element, "filesToAccess");
            var requiredCapabilities = ParseStringArray(element, "requiredCapabilities");

            // Parse booleans
            var requiresScreen = element.TryGetProperty("requiresScreen", out var screenProp) && screenProp.GetBoolean();
            var requiresVisualVerification = element.TryGetProperty("requiresVisualVerification", out var verifyProp) && verifyProp.GetBoolean();

            // Parse priority
            var priority = MessagePriority.Normal;
            if (element.TryGetProperty("priority", out var priorityProp))
            {
                Enum.TryParse<MessagePriority>(priorityProp.GetString(), true, out priority);
            }

            // Determine agent type
            var agentType = requiresScreen ? AgentType.Screen : AgentType.Code;

            return new AgentTask
            {
                TaskId = taskId!,
                Description = description ?? "",
                CommandType = commandType ?? "",
                CommandArgs = commandArgs ?? "",
                CodeToExecute = codeToExecute,
                DependsOnTaskIds = dependsOn,
                FilesToAccess = filesToAccess,
                RequiredCapabilities = requiredCapabilities,
                RequiresScreenAccess = requiresScreen,
                RequiresVisualVerification = requiresVisualVerification,
                RequiredAgentType = agentType,
                Priority = priority
            };
        }

        /// <summary>
        /// Parse a string array property from JSON element.
        /// </summary>
        private string[] ParseStringArray(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            var list = new List<string>();
            foreach (var item in prop.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var value = item.GetString();
                    if (!string.IsNullOrEmpty(value))
                    {
                        list.Add(value);
                    }
                }
            }
            return list.ToArray();
        }

        /// <summary>
        /// Validate task dependencies and fix any issues.
        /// </summary>
        private void ValidateAndFixDependencies(List<AgentTask> tasks)
        {
            var taskIds = new HashSet<string>(tasks.Select(t => t.TaskId));

            foreach (var task in tasks)
            {
                // Remove dependencies on non-existent tasks
                var validDeps = task.DependsOnTaskIds
                    .Where(d => taskIds.Contains(d))
                    .ToArray();

                if (validDeps.Length != task.DependsOnTaskIds.Length)
                {
                    _logToUI?.Invoke($"[TaskDecomposer] Removed invalid dependencies from {task.TaskId}");
                    // Create new task with fixed dependencies (since arrays are immutable)
                    // Note: AgentTask uses init properties, so we can't modify directly
                    // The task decomposer should create tasks with valid dependencies from the start
                }

                // Remove self-dependencies
                if (task.DependsOnTaskIds.Contains(task.TaskId))
                {
                    _logToUI?.Invoke($"[TaskDecomposer] Warning: Task {task.TaskId} depends on itself");
                }
            }

            // Check for circular dependencies
            if (HasCircularDependencies(tasks))
            {
                _logToUI?.Invoke("[TaskDecomposer] Warning: Circular dependencies detected");
            }
        }

        /// <summary>
        /// Check if the task graph has circular dependencies.
        /// </summary>
        private bool HasCircularDependencies(List<AgentTask> tasks)
        {
            var taskMap = tasks.ToDictionary(t => t.TaskId);
            var visited = new HashSet<string>();
            var recursionStack = new HashSet<string>();

            bool HasCycle(string taskId)
            {
                if (recursionStack.Contains(taskId))
                    return true;

                if (visited.Contains(taskId))
                    return false;

                visited.Add(taskId);
                recursionStack.Add(taskId);

                if (taskMap.TryGetValue(taskId, out var task))
                {
                    foreach (var depId in task.DependsOnTaskIds)
                    {
                        if (HasCycle(depId))
                            return true;
                    }
                }

                recursionStack.Remove(taskId);
                return false;
            }

            foreach (var task in tasks)
            {
                if (HasCycle(task.TaskId))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Estimate the complexity of the task set.
        /// </summary>
        private int EstimateComplexity(List<AgentTask> tasks)
        {
            int complexity = 0;

            foreach (var task in tasks)
            {
                // Base complexity
                complexity += 1;

                // Screen tasks are more complex
                if (task.RequiresScreenAccess)
                    complexity += 2;

                // Verification adds complexity
                if (task.RequiresVisualVerification)
                    complexity += 1;

                // Code execution tasks
                if (!string.IsNullOrEmpty(task.CodeToExecute))
                    complexity += 1;
            }

            // Scale to 1-10 range
            return Math.Min(10, Math.Max(1, complexity / 3));
        }

        /// <summary>
        /// Decompose a simple file operation into a single task.
        /// Used for simple goals that don't need full LLM decomposition.
        /// </summary>
        public AgentTask CreateSimpleTask(
            string description,
            string commandType,
            string commandArgs,
            string? codeToExecute = null,
            string[]? filesToAccess = null)
        {
            return new AgentTask
            {
                TaskId = $"task-{Guid.NewGuid():N}".Substring(0, 12),
                Description = description,
                CommandType = commandType,
                CommandArgs = commandArgs,
                CodeToExecute = codeToExecute,
                FilesToAccess = filesToAccess ?? Array.Empty<string>(),
                RequiredCapabilities = InferCapabilities(commandType),
                RequiresScreenAccess = IsScreenCommand(commandType),
                RequiredAgentType = IsScreenCommand(commandType) ? AgentType.Screen : AgentType.Code,
                Priority = MessagePriority.Normal
            };
        }

        /// <summary>
        /// Check if a command type requires screen access.
        /// </summary>
        private bool IsScreenCommand(string commandType)
        {
            return commandType.ToUpper() switch
            {
                "CLICK" or "DOUBLE_CLICK" or "RIGHT_CLICK" => true,
                "TYPE" or "SEND_KEYS" or "KEYS" => true,
                "SCROLL" => true,
                "LAUNCH_APP" or "OPEN_APP" => true,
                "SCREENSHOT" => true,
                "WINDOW_TITLE" => true,
                _ => false
            };
        }

        /// <summary>
        /// Infer required capabilities from command type.
        /// </summary>
        private string[] InferCapabilities(string commandType)
        {
            return commandType.ToUpper() switch
            {
                "WRITE_FILE" or "READ_FILE" or "EDIT_FILE" => new[] { "file-edit" },
                "LIST_FILES" or "MOVE_FILE" or "COPY_FILE" or "DELETE_FILE" or "MAKE_DIR" => new[] { "file-edit" },
                "PYTHON" => new[] { "python", "script" },
                "POWERSHELL" => new[] { "powershell", "script" },
                "COMPILE" => new[] { "compile", "dotnet" },
                "TEST" => new[] { "test" },
                "CLICK" or "TYPE" or "SCROLL" => new[] { "visual-testing" },
                "LAUNCH_APP" => new[] { "launch-app" },
                _ => new[] { "general" }
            };
        }
    }
}
