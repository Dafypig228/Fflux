using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluxCore.Swarm.Infrastructure;

namespace FluxCore.Swarm.Agents
{
    /// <summary>
    /// Specializations for code agents.
    /// Each specialization has different capabilities and expertise.
    /// </summary>
    public enum CodeAgentSpecialization
    {
        // Language specialists
        CSharpDeveloper,
        PythonDeveloper,
        JavaScriptDeveloper,
        TypeScriptDeveloper,

        // Task specialists
        FileManager,
        TestRunner,
        CodeReviewer,
        Refactorer,
        DocWriter,

        // General purpose
        GeneralPurpose
    }

    /// <summary>
    /// Background code agent that can write/edit code, run scripts, and manage files.
    /// Does NOT require screen access - runs entirely in the background.
    /// </summary>
    public class CodeAgent : BaseAgent
    {
        private readonly CodeAgentSpecialization _specialization;
        private readonly CodeExecutionAgent _codeRunner;
        private readonly GeminiService? _llm;

        public override AgentType Type => AgentType.Code;

        public override string[] Capabilities => _specialization switch
        {
            CodeAgentSpecialization.CSharpDeveloper =>
                new[] { "csharp", "dotnet", "wpf", "file-edit", "compile" },
            CodeAgentSpecialization.PythonDeveloper =>
                new[] { "python", "pip", "file-edit", "script" },
            CodeAgentSpecialization.JavaScriptDeveloper =>
                new[] { "javascript", "node", "npm", "file-edit" },
            CodeAgentSpecialization.TypeScriptDeveloper =>
                new[] { "typescript", "javascript", "node", "npm", "file-edit" },
            CodeAgentSpecialization.FileManager =>
                new[] { "file-edit", "file-move", "file-copy", "file-delete", "directory" },
            CodeAgentSpecialization.TestRunner =>
                new[] { "test", "unittest", "pytest", "xunit", "compile" },
            CodeAgentSpecialization.CodeReviewer =>
                new[] { "review", "analyze", "suggest" },
            CodeAgentSpecialization.Refactorer =>
                new[] { "refactor", "rename", "extract", "file-edit" },
            CodeAgentSpecialization.DocWriter =>
                new[] { "documentation", "readme", "comments", "file-edit" },
            _ => new[] { "file-edit", "script", "general" }
        };

        public CodeAgent(
            string agentId,
            CodeAgentSpecialization specialization,
            IMessageBus messageBus,
            IFileLockManager lockManager,
            IAgentRegistry registry,
            GeminiService? llm = null,
            Action<string>? logToUI = null)
            : base(agentId, messageBus, lockManager, registry, logToUI)
        {
            _specialization = specialization;
            _codeRunner = new CodeExecutionAgent();
            _llm = llm;
        }

        protected override async Task RunLoopAsync(CancellationToken ct)
        {
            LogToUI($"[{AgentId}] Code agent loop started (Specialization: {_specialization})");

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Wait for a task from the queue
                    var task = await MessageBus.DequeueTaskAsync(AgentId, Capabilities, ct);

                    if (task == null)
                    {
                        await Task.Delay(100, ct); // Brief wait before checking again
                        continue;
                    }

                    LogToUI($"[{AgentId}] Received task: {task.TaskId} - {task.Description}");

                    await SetStateAsync(AgentState.Working, task.TaskId);

                    var stopwatch = Stopwatch.StartNew();

                    try
                    {
                        var result = await ExecuteTaskAsync(task, ct);
                        stopwatch.Stop();

                        await ReportTaskCompletedAsync(
                            task.TaskId,
                            result.Success,
                            result.Output,
                            stopwatch.Elapsed);

                        LogToUI($"[{AgentId}] Task {task.TaskId} completed: {(result.Success ? "SUCCESS" : "FAILED")}");
                    }
                    catch (Exception ex)
                    {
                        stopwatch.Stop();

                        await ReportTaskFailedAsync(
                            task.TaskId,
                            ex.Message,
                            retryCount: 0,
                            isPermanent: false);

                        LogToUI($"[{AgentId}] Task {task.TaskId} failed with error: {ex.Message}");
                    }
                    finally
                    {
                        await SetStateAsync(AgentState.Idle);
                    }
                }
                catch (OperationCanceledException)
                {
                    break; // Normal cancellation
                }
                catch (Exception ex)
                {
                    LogToUI($"[{AgentId}] Error in run loop: {ex.Message}");
                    await Task.Delay(1000, ct); // Brief delay before continuing
                }
            }

            LogToUI($"[{AgentId}] Code agent loop ended");
        }

        private async Task<AgentExecutionResult> ExecuteTaskAsync(AgentTask task, CancellationToken ct)
        {
            // Acquire locks for files we need to access
            var acquiredLocks = new List<IFileLock>();

            try
            {
                foreach (var file in task.FilesToAccess)
                {
                    var lockHandle = await AcquireFileLockAsync(file, TimeSpan.FromSeconds(30));

                    if (lockHandle == null)
                    {
                        // Could not acquire lock - report conflict
                        var owner = await LockManager.GetLockOwnerAsync(file);
                        return new AgentExecutionResult
                        {
                            Success = false,
                            Error = $"Could not acquire lock on {file} (owned by {owner})"
                        };
                    }

                    acquiredLocks.Add(lockHandle);
                }

                // Execute based on command type
                return task.CommandType.ToUpper() switch
                {
                    "WRITE_FILE" => await ExecuteWriteFileAsync(task),
                    "READ_FILE" => await ExecuteReadFileAsync(task),
                    "EDIT_FILE" => await ExecuteEditFileAsync(task),
                    "LIST_FILES" => await ExecuteListFilesAsync(task),
                    "MOVE_FILE" => await ExecuteMoveFileAsync(task),
                    "COPY_FILE" => await ExecuteCopyFileAsync(task),
                    "DELETE_FILE" => await ExecuteDeleteFileAsync(task),
                    "MAKE_DIR" => await ExecuteMakeDirAsync(task),
                    "PYTHON" => await ExecutePythonAsync(task),
                    "POWERSHELL" => await ExecutePowerShellAsync(task),
                    "COMPILE" => await ExecuteCompileAsync(task),
                    "TEST" => await ExecuteTestAsync(task),
                    _ => new AgentExecutionResult
                    {
                        Success = false,
                        Error = $"Unknown command type: {task.CommandType}"
                    }
                };
            }
            finally
            {
                // Release all acquired locks
                foreach (var lockHandle in acquiredLocks)
                {
                    await lockHandle.DisposeAsync();
                }
            }
        }

        private async Task<AgentExecutionResult> ExecuteWriteFileAsync(AgentTask task)
        {
            var result = await _codeRunner.WriteFileAsync(task.TargetFilePath!, task.CodeToExecute!);
            return new AgentExecutionResult
            {
                Success = result.Success,
                Output = result.Message,
                Error = result.Success ? null : result.Message
            };
        }

        private async Task<AgentExecutionResult> ExecuteReadFileAsync(AgentTask task)
        {
            var result = await _codeRunner.ReadFileAsync(task.TargetFilePath!);
            return new AgentExecutionResult
            {
                Success = result.Success,
                Output = result.Message,
                Error = result.Success ? null : result.Message
            };
        }

        private async Task<AgentExecutionResult> ExecuteEditFileAsync(AgentTask task)
        {
            // For complex edits, we'd use the LLM to generate the edit
            // For now, just overwrite with new content
            var result = await _codeRunner.WriteFileAsync(task.TargetFilePath!, task.CodeToExecute!);
            return new AgentExecutionResult
            {
                Success = result.Success,
                Output = result.Message,
                Error = result.Success ? null : result.Message
            };
        }

        private async Task<AgentExecutionResult> ExecuteListFilesAsync(AgentTask task)
        {
            var result = await _codeRunner.ListFilesAsync(task.CommandArgs);
            return new AgentExecutionResult
            {
                Success = result.Success,
                Output = result.Message,
                Error = result.Success ? null : result.Message
            };
        }

        private async Task<AgentExecutionResult> ExecuteMoveFileAsync(AgentTask task)
        {
            var parts = task.CommandArgs.Split('|');
            if (parts.Length < 2)
            {
                return new AgentExecutionResult
                {
                    Success = false,
                    Error = "MOVE_FILE requires source|destination format"
                };
            }

            var result = await _codeRunner.MoveFileAsync(parts[0].Trim(), parts[1].Trim());
            return new AgentExecutionResult
            {
                Success = result.Success,
                Output = result.Message,
                Error = result.Success ? null : result.Message
            };
        }

        private async Task<AgentExecutionResult> ExecuteCopyFileAsync(AgentTask task)
        {
            var parts = task.CommandArgs.Split('|');
            if (parts.Length < 2)
            {
                return new AgentExecutionResult
                {
                    Success = false,
                    Error = "COPY_FILE requires source|destination format"
                };
            }

            var result = await _codeRunner.CopyFileAsync(parts[0].Trim(), parts[1].Trim());
            return new AgentExecutionResult
            {
                Success = result.Success,
                Output = result.Message,
                Error = result.Success ? null : result.Message
            };
        }

        private async Task<AgentExecutionResult> ExecuteDeleteFileAsync(AgentTask task)
        {
            var result = await _codeRunner.DeleteFileAsync(task.CommandArgs);
            return new AgentExecutionResult
            {
                Success = result.Success,
                Output = result.Message,
                Error = result.Success ? null : result.Message
            };
        }

        private async Task<AgentExecutionResult> ExecuteMakeDirAsync(AgentTask task)
        {
            var result = await _codeRunner.MakeDirAsync(task.CommandArgs);
            return new AgentExecutionResult
            {
                Success = result.Success,
                Output = result.Message,
                Error = result.Success ? null : result.Message
            };
        }

        private async Task<AgentExecutionResult> ExecutePythonAsync(AgentTask task)
        {
            var code = task.CodeToExecute ?? task.CommandArgs;
            var result = await _codeRunner.RunPythonAsync(code);
            return new AgentExecutionResult
            {
                Success = result.Success,
                Output = result.Message,
                Error = result.Success ? null : result.Message
            };
        }

        private async Task<AgentExecutionResult> ExecutePowerShellAsync(AgentTask task)
        {
            var code = task.CodeToExecute ?? task.CommandArgs;
            var result = await _codeRunner.RunPowerShellAsync(code);
            return new AgentExecutionResult
            {
                Success = result.Success,
                Output = result.Message,
                Error = result.Success ? null : result.Message
            };
        }

        private async Task<AgentExecutionResult> ExecuteCompileAsync(AgentTask task)
        {
            // Run dotnet build
            var result = await _codeRunner.RunPowerShellAsync($"dotnet build \"{task.CommandArgs}\"");
            return new AgentExecutionResult
            {
                Success = result.Success && !result.Message.Contains("error"),
                Output = result.Message,
                Error = result.Success ? null : result.Message
            };
        }

        private async Task<AgentExecutionResult> ExecuteTestAsync(AgentTask task)
        {
            // Run dotnet test
            var result = await _codeRunner.RunPowerShellAsync($"dotnet test \"{task.CommandArgs}\" --no-build");
            return new AgentExecutionResult
            {
                Success = result.Success && !result.Message.Contains("Failed"),
                Output = result.Message,
                Error = result.Success ? null : result.Message
            };
        }
    }
}
