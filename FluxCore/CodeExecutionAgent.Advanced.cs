using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace FluxCore
{
    public partial class CodeExecutionAgent
    {
        // =========================================
        // BACKGROUND PROCESS TRACKING
        // =========================================

        private readonly ConcurrentDictionary<int, (Process Process, string LogPath)> _backgroundProcesses = new();

        // =========================================
        // RUN_CSHARP — Roslyn in-process scripting
        // =========================================

        /// <summary>
        /// Compiles and runs a C# script string inside the current process.
        /// The script runs with access to all currently-loaded assemblies (no explicit AddReferences needed).
        /// Properties of <paramref name="globals"/> are accessible directly in script scope by name.
        ///
        /// Safety: executed via Task.Run with a 30-second timeout.
        /// An infinite loop will be abandoned (not killed, since .NET has no Thread.Abort),
        /// but JarvisCore will not be blocked — it returns a timeout error and continues.
        /// </summary>
        public async Task<ExecutionResult> RunCSharpAsync(string code, ScriptGlobals globals)
        {
            // All loaded assemblies — includes WTelegramClient, Newtonsoft, etc. automatically
            var refs = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(a.Location));

            var options = ScriptOptions.Default
                .AddImports(
                    "System",
                    "System.Linq",
                    "System.Collections.Generic",
                    "System.Net.Http",
                    "System.Text",
                    "System.Text.Json",
                    "System.Threading.Tasks",
                    "System.IO")
                .AddReferences(refs);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            // Run on a separate thread so a blocking script doesn't freeze JarvisCore.
            // If timed out we abandon the task — it may leak CPU/memory until app restart,
            // but JarvisCore is unblocked immediately.
            var scriptTask = Task.Run(async () =>
                await CSharpScript.EvaluateAsync<string>(code, options, globals,
                    cancellationToken: cts.Token));

            if (await Task.WhenAny(scriptTask, Task.Delay(30_000)) != scriptTask)
                return new ExecutionResult(false,
                    "RUN_CSHARP timed out (30s) — possible infinite loop or blocking call. " +
                    "Make sure async calls use 'await' and don't spin indefinitely.");

            try
            {
                string? output = await scriptTask;
                return new ExecutionResult(true, output ?? "(script returned null)");
            }
            catch (CompilationErrorException ex)
            {
                return new ExecutionResult(false,
                    $"Compile error: {ex.Message}\n" +
                    "Remember: write TOP-LEVEL statements, not class definitions. " +
                    "Use 'return \"result\";' as the last statement.");
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"Runtime error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // =========================================
        // START_BACKGROUND — spawn long-running processes
        // =========================================

        /// <summary>
        /// Starts a long-running background process (bot, trading script, etc.) and tracks its PID.
        /// Arg format: "command,logPath"  e.g. "python bot.py,C:\logs\bot.log"
        ///                            or  "node bot.js,C:\logs\bot.log"
        /// Returns: "Started PID=1234 → log: C:\logs\bot.log"
        /// </summary>
        public Task<ExecutionResult> StartBackgroundAsync(string arg)
        {
            int lastComma = arg.LastIndexOf(',');
            if (lastComma < 0)
                return Task.FromResult(new ExecutionResult(false,
                    "Format: [[START_BACKGROUND:command,logPath]] e.g. [[START_BACKGROUND:python bot.py,C:\\logs\\bot.log]]"));

            string command  = arg[..lastComma].Trim();
            string logPath  = arg[(lastComma + 1)..].Trim();

            // Parse executable and arguments
            string executable;
            string arguments;
            if (command.StartsWith('"'))
            {
                int closingQuote = command.IndexOf('"', 1);
                executable = closingQuote > 0 ? command[1..closingQuote] : command;
                arguments  = closingQuote > 0 ? command[(closingQuote + 1)..].Trim() : "";
            }
            else
            {
                int firstSpace = command.IndexOf(' ');
                executable = firstSpace > 0 ? command[..firstSpace] : command;
                arguments  = firstSpace > 0 ? command[(firstSpace + 1)..] : "";
            }

            try
            {
                // Ensure log directory exists
                string? logDir = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrEmpty(logDir))
                    Directory.CreateDirectory(logDir);

                var psi = new ProcessStartInfo
                {
                    FileName               = executable,
                    Arguments              = arguments,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding  = Encoding.UTF8,
                };

                var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

                // Write stdout+stderr to log file asynchronously
                var logWriter = new StreamWriter(logPath, append: true, encoding: Encoding.UTF8)
                    { AutoFlush = true };

                process.OutputDataReceived += (_, e) => { if (e.Data != null) logWriter.WriteLine(e.Data); };
                process.ErrorDataReceived  += (_, e) => { if (e.Data != null) logWriter.WriteLine($"[ERR] {e.Data}"); };
                process.Exited             += (_, _) => { logWriter.WriteLine($"[EXIT] Process exited at {DateTime.Now}"); logWriter.Dispose(); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                _backgroundProcesses[process.Id] = (process, logPath);
                DataLake?.Write("background", $"STARTED pid={process.Id} cmd={command}", new { pid = process.Id, log = logPath });

                return Task.FromResult(new ExecutionResult(true,
                    $"Started PID={process.Id} → log: {logPath}\n" +
                    $"Use [[CHECK_BACKGROUND:{process.Id}]] to check status, [[READ_LOG:{logPath}]] to read output, [[STOP_BACKGROUND:{process.Id}]] to stop."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(new ExecutionResult(false, $"Failed to start process: {ex.Message}"));
            }
        }

        // =========================================
        // READ_LOG — tail a log file
        // =========================================

        /// <summary>
        /// Reads the last N lines from a log file.
        /// Arg format: "logPath" or "logPath,lineCount"
        /// </summary>
        public ExecutionResult ReadLog(string arg)
        {
            string logPath;
            int lineCount = 50;

            int lastComma = arg.LastIndexOf(',');
            if (lastComma > 0 && int.TryParse(arg[(lastComma + 1)..].Trim(), out int parsedCount))
            {
                logPath   = arg[..lastComma].Trim();
                lineCount = Math.Clamp(parsedCount, 1, 500);
            }
            else
            {
                logPath = arg.Trim();
            }

            if (!File.Exists(logPath))
                return new ExecutionResult(false, $"Log file not found: {logPath}");

            try
            {
                // Read all lines and return last N
                var lines = File.ReadAllLines(logPath, Encoding.UTF8);
                var tail  = lines.TakeLast(lineCount).ToArray();
                string content = string.Join("\n", tail);

                if (content.Length > MAX_OUTPUT_LENGTH)
                    content = "...(truncated)\n" + content[^MAX_OUTPUT_LENGTH..];

                return new ExecutionResult(true,
                    $"[Last {tail.Length} lines of {Path.GetFileName(logPath)}]\n{content}");
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"Could not read log: {ex.Message}");
            }
        }

        // =========================================
        // CHECK_BACKGROUND — check if process is alive
        // =========================================

        /// <summary>
        /// Returns the status of a background process by PID.
        /// Arg: PID as string e.g. "1234"
        /// </summary>
        public ExecutionResult CheckBackground(string arg)
        {
            if (!int.TryParse(arg.Trim(), out int pid))
                return new ExecutionResult(false, $"Invalid PID: {arg}");

            if (!_backgroundProcesses.TryGetValue(pid, out var entry))
                return new ExecutionResult(true, $"PID {pid}: not tracked (either never started or already removed)");

            try
            {
                if (entry.Process.HasExited)
                {
                    _backgroundProcesses.TryRemove(pid, out _);
                    return new ExecutionResult(true,
                        $"PID {pid}: exited (code {entry.Process.ExitCode}) — log: {entry.LogPath}");
                }
                return new ExecutionResult(true,
                    $"PID {pid}: running (started {entry.Process.StartTime:HH:mm:ss}) — log: {entry.LogPath}");
            }
            catch
            {
                _backgroundProcesses.TryRemove(pid, out _);
                return new ExecutionResult(true, $"PID {pid}: process no longer exists");
            }
        }

        // =========================================
        // STOP_BACKGROUND — kill a background process
        // =========================================

        /// <summary>
        /// Kills a background process by PID.
        /// Arg: PID as string e.g. "1234"
        /// </summary>
        public ExecutionResult StopBackground(string arg)
        {
            if (!int.TryParse(arg.Trim(), out int pid))
                return new ExecutionResult(false, $"Invalid PID: {arg}");

            if (!_backgroundProcesses.TryRemove(pid, out var entry))
                return new ExecutionResult(false, $"PID {pid} not found in tracked processes");

            try
            {
                if (!entry.Process.HasExited)
                {
                    entry.Process.Kill(entireProcessTree: true);
                    DataLake?.Write("background", $"STOPPED pid={pid}", new { pid });
                    return new ExecutionResult(true, $"PID {pid} stopped. Log was: {entry.LogPath}");
                }
                return new ExecutionResult(true, $"PID {pid} had already exited (code {entry.Process.ExitCode})");
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"Failed to stop PID {pid}: {ex.Message}");
            }
        }
    }
}
