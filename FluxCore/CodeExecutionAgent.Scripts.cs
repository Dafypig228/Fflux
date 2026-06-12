using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace FluxCore
{
    public partial class CodeExecutionAgent
    {
        // =========================================
        // NODE.JS EXECUTION
        // =========================================

        public async Task<ExecutionResult> RunNodeAsync(string code)
        {
            try
            {
                string tempFile = Path.Combine(_tempDir, $"script_{Guid.NewGuid():N}.js");
                await File.WriteAllTextAsync(tempFile, code);

                try
                {
                    string nodePath = FindNode();
                    if (string.IsNullOrEmpty(nodePath))
                        return new ExecutionResult(false, "Node.js not found. Install Node.js and add to PATH.");

                    return await RunProcessAsync(nodePath, $"\"{tempFile}\"");
                }
                finally
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"Node.js error: {ex.Message}");
            }
        }

        private string FindNode()
        {
            var paths = new[] { "node", "node.exe", @"C:\Program Files\nodejs\node.exe" };
            foreach (var path in paths)
            {
                try
                {
                    var psi = new ProcessStartInfo(path, "--version")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true
                    };
                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        proc.WaitForExit(2000);
                        if (proc.ExitCode == 0) return path;
                    }
                }
                catch { }
            }
            return "";
        }

        // =========================================
        // PYTHON EXECUTION
        // =========================================

        public async Task<ExecutionResult> RunPythonAsync(string code)
        {
            try
            {
                string tempFile = Path.Combine(_tempDir, $"script_{Guid.NewGuid():N}.py");
                await File.WriteAllTextAsync(tempFile, code);

                try
                {
                    string pythonPath = FindPython();
                    if (string.IsNullOrEmpty(pythonPath))
                        return new ExecutionResult(false, "Python not found. Install Python and add to PATH.");

                    var result = await RunProcessAsync(pythonPath, $"\"{tempFile}\"");
                    return result;
                }
                finally
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"Python error: {ex.Message}");
            }
        }

        private string FindPython()
        {
            var paths = new[]
            {
                "python",
                "python3",
                @"C:\Python311\python.exe",
                @"C:\Python310\python.exe",
                @"C:\Python39\python.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Python\Python311\python.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Python\Python310\python.exe"),
            };

            foreach (var path in paths)
            {
                try
                {
                    var psi = new ProcessStartInfo(path, "--version")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true
                    };
                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        proc.WaitForExit(2000);
                        if (proc.ExitCode == 0)
                            return path;
                    }
                }
                catch { }
            }
            return "";
        }

        // =========================================
        // POWERSHELL EXECUTION
        // =========================================

        public async Task<ExecutionResult> RunPowerShellAsync(string command)
        {
            // SELF-PROTECTION: Block commands that would kill our own process or parent debugger
            string cmdLower = command.ToLower();
            bool isKillCmd = cmdLower.Contains("taskkill") || cmdLower.Contains("stop-process") ||
                             (cmdLower.Contains("kill") && cmdLower.Contains("-name"));
            if (isKillCmd)
            {
                string[] forbidden = { "devenv", "fluxcore", "davos", "flux" };
                if (forbidden.Any(f => cmdLower.Contains(f)))
                {
                    return new ExecutionResult(false,
                        "BLOCKED: Cannot kill own process or parent debugger. Use a different approach.");
                }
            }

            try
            {
                // CRITICAL: Use UTF-8 encoding for Cyrillic/Unicode support.
                // The PSDefaultParameterValues line forces FILE writes (Out-File, >, >>,
                // Set-Content, Add-Content, Export-Csv) to UTF-8 too — Windows PowerShell 5.1
                // defaults Out-File/> to UTF-16 LE, which Python then reads as NUL-riddled
                // garbage (' P r o c e s s '). Verified empirically via -EncodedCommand on
                // this machine: with the preference set, both Out-File -Append and > emit
                // EF BB BF (UTF-8 BOM). Python must read these files with encoding='utf-8-sig'
                // (documented in the <grounding> prompt section).
                string utf8Prefix =
                    "$OutputEncoding = [Console]::InputEncoding = [Console]::OutputEncoding = [System.Text.Encoding]::UTF8; " +
                    "$PSDefaultParameterValues['Out-File:Encoding']='utf8'; " +
                    "$PSDefaultParameterValues['Set-Content:Encoding']='utf8'; " +
                    "$PSDefaultParameterValues['Add-Content:Encoding']='utf8'; " +
                    "$PSDefaultParameterValues['Export-Csv:Encoding']='utf8'; ";
                string fullCommand = utf8Prefix + command;

                byte[] commandBytes = Encoding.Unicode.GetBytes(fullCommand);
                string encodedCommand = Convert.ToBase64String(commandBytes);

                return await RunProcessAsync("powershell.exe", $"-NoProfile -NonInteractive -EncodedCommand {encodedCommand}");
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"PowerShell error: {ex.Message}");
            }
        }

        // =========================================
        // CMD EXECUTION
        // =========================================

        public async Task<ExecutionResult> RunCmdAsync(string command)
        {
            try
            {
                return await RunProcessAsync("cmd.exe", $"/c {command}");
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"CMD error: {ex.Message}");
            }
        }

        // =========================================
        // PROCESS RUNNER
        // =========================================

        private async Task<ExecutionResult> RunProcessAsync(string executable, string arguments)
        {
            var output = new StringBuilder();
            var error = new StringBuilder();

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = arguments,
                    WorkingDirectory = Path.GetTempPath(),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                // Python writes cp1252 to redirected pipes by default — force UTF-8 to match
                // the UTF-8 decoder above (harmless for powershell/cmd/node).
                psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";

                using var process = new Process { StartInfo = psi };

                process.OutputDataReceived += (s, e) => { if (e.Data != null) output.AppendLine(e.Data); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) error.AppendLine(e.Data); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                var completed = await Task.Run(() => process.WaitForExit(TIMEOUT_MS));

                if (!completed)
                {
                    try { process.Kill(); } catch { }
                    return new ExecutionResult(false, $"Execution timed out ({TIMEOUT_MS / 1000}s limit)");
                }

                string resultOutput = output.ToString().Trim();
                string resultError = error.ToString().Trim();

                // Head+tail truncation: the conclusion of a script prints LAST — the old
                // head-only cut destroyed exactly the part the model needed.
                resultOutput = OutputTrim.Middle(resultOutput, 1500, MAX_OUTPUT_LENGTH - 1500);

                // Nonzero exit code is ALWAYS a failure, even with empty stderr — the old
                // check (exit != 0 AND stderr non-empty) reported silent `exit 1` as success.
                // Include stdout in the failure too: prints before a crash are evidence.
                if (process.ExitCode != 0)
                {
                    var fail = new StringBuilder($"Exit code {process.ExitCode}");
                    if (resultError.Length > 0)
                        fail.Append($"\nSTDERR: {OutputTrim.Middle(resultError, 500, 1500)}");
                    if (resultOutput.Length > 0)
                        fail.Append($"\nSTDOUT: {resultOutput}");
                    return new ExecutionResult(false, fail.ToString());
                }

                // Exit 0 with stderr content: surface it labeled instead of swallowing it
                // (warnings, partial errors). Skip powershell.exe's CLIXML stream noise —
                // it's serialized progress/verbose records, not plain-text errors.
                if (resultError.Length > 0 && !resultError.StartsWith("#< CLIXML"))
                {
                    string stderrNote = $"STDERR (exit 0): {OutputTrim.Middle(resultError, 300, 700)}";
                    resultOutput = resultOutput.Length > 0 ? $"{resultOutput}\n{stderrNote}" : stderrNote;
                }

                return new ExecutionResult(true, string.IsNullOrEmpty(resultOutput) ? "(no output)" : resultOutput);
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"Process error: {ex.Message}");
            }
        }
    }
}
