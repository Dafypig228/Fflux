using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace FluxCore
{
    /// <summary>
    /// Code Execution Sandbox: Runs Python, PowerShell, and CMD commands.
    /// Also provides direct file writing capabilities.
    /// </summary>
    public class CodeExecutionAgent
    {
        private readonly string _tempDir;
        private const int MAX_OUTPUT_LENGTH = 5000;
        private const int TIMEOUT_MS = 30000; // 30 seconds

        public CodeExecutionAgent()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "FluxSandbox");
            Directory.CreateDirectory(_tempDir);
        }

        // =========================================
        // FILE SYSTEM MANAGEMENT (Clean Desktop)
        // =========================================

        public async Task<ExecutionResult> ListFilesAsync(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || path == ".")
                    path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
                
                path = Environment.ExpandEnvironmentVariables(path);
                
                // FIX: AI often guesses "C:\Users\User", replace with actual profile
                string actualProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (path.StartsWith(@"C:\Users\User", StringComparison.OrdinalIgnoreCase))
                {
                    path = path.Replace(@"C:\Users\User", actualProfile, StringComparison.OrdinalIgnoreCase);
                }

                // FIX: AI might guess wrong Desktop path (e.g., C:\Users\adila\Desktop when OneDrive is used)
                string actualDesktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string wrongDesktopPath = Path.Combine(actualProfile, "Desktop");
                if (!actualDesktop.Equals(wrongDesktopPath, StringComparison.OrdinalIgnoreCase) &&
                    path.StartsWith(wrongDesktopPath, StringComparison.OrdinalIgnoreCase))
                {
                    path = path.Replace(wrongDesktopPath, actualDesktop, StringComparison.OrdinalIgnoreCase);
                }

                // If path is just "Desktop", fix it
                if (path.Equals("Desktop", StringComparison.OrdinalIgnoreCase))
                    path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));

                // DEBUG: Log the actual path being used
                string debugPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "FluxDebug.txt");
                File.AppendAllText(debugPath, $"[LIST] Path: {path}\n");

                var sb = new StringBuilder();
                
                // Check if this is the Desktop - if so, also include Public Desktop
                string userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string publicDesktop = @"C:\Users\Public\Desktop";
                bool isDesktopQuery = path.Equals(userDesktop, StringComparison.OrdinalIgnoreCase) ||
                                      path.EndsWith("Desktop", StringComparison.OrdinalIgnoreCase);

                if (isDesktopQuery)
                {
                    sb.AppendLine($"=== DESKTOP CONTENTS (USE EXACT NAMES BELOW FOR MOVE_FILE) ===");
                    
                    // User Desktop
                    if (Directory.Exists(userDesktop))
                    {
                        var userDirs = Directory.GetDirectories(userDesktop);
                        var userFiles = Directory.GetFiles(userDesktop);
                        sb.AppendLine($"--- User Desktop ({userDesktop}) ---");
                        foreach (var d in userDirs.Take(20)) sb.AppendLine($"[DIR]  {Path.GetFileName(d)}");
                        foreach (var f in userFiles.Take(50)) sb.AppendLine($"[FILE] {Path.GetFileName(f)}");
                    }
                    
                    // Public Desktop (shared shortcuts)
                    if (Directory.Exists(publicDesktop))
                    {
                        var pubDirs = Directory.GetDirectories(publicDesktop);
                        var pubFiles = Directory.GetFiles(publicDesktop);
                        sb.AppendLine($"--- Public Desktop ({publicDesktop}) ---");
                        foreach (var d in pubDirs.Take(20)) sb.AppendLine($"[DIR]  {Path.GetFileName(d)}");
                        foreach (var f in pubFiles.Take(50)) sb.AppendLine($"[FILE] {Path.GetFileName(f)}");
                    }
                    
                    return new ExecutionResult(true, sb.ToString());
                }
                
                // Standard directory listing for non-Desktop paths
                if (!Directory.Exists(path)) return new ExecutionResult(false, $"Directory not found: {path}");

                var files = Directory.GetFiles(path);
                var dirs = Directory.GetDirectories(path);
                
                sb.AppendLine($"Contents of {path}:");
                foreach (var d in dirs.Take(20)) sb.AppendLine($"[DIR]  {Path.GetFileName(d)}");
                foreach (var f in files.Take(50)) sb.AppendLine($"[FILE] {Path.GetFileName(f)}");
                
                if (files.Length > 50) sb.AppendLine($"... and {files.Length - 50} more files.");
                
                return new ExecutionResult(true, sb.ToString());
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"List failed: {ex.Message}");
            }
        }

        public async Task<ExecutionResult> MoveFileAsync(string source, string dest)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(source) || source == ".") source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
                
                source = Environment.ExpandEnvironmentVariables(source);
                dest = Environment.ExpandEnvironmentVariables(dest);

                string actualProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (source.StartsWith(@"C:\Users\User", StringComparison.OrdinalIgnoreCase)) 
                    source = source.Replace(@"C:\Users\User", actualProfile, StringComparison.OrdinalIgnoreCase);
                if (dest.StartsWith(@"C:\Users\User", StringComparison.OrdinalIgnoreCase)) 
                    dest = dest.Replace(@"C:\Users\User", actualProfile, StringComparison.OrdinalIgnoreCase);

                // FIX: AI might guess wrong Desktop path (e.g., C:\Users\adila\Desktop when OneDrive is used)
                string actualDesktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string wrongDesktopPath = Path.Combine(actualProfile, "Desktop");
                if (!actualDesktop.Equals(wrongDesktopPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (source.StartsWith(wrongDesktopPath, StringComparison.OrdinalIgnoreCase))
                        source = source.Replace(wrongDesktopPath, actualDesktop, StringComparison.OrdinalIgnoreCase);
                    if (dest.StartsWith(wrongDesktopPath, StringComparison.OrdinalIgnoreCase))
                        dest = dest.Replace(wrongDesktopPath, actualDesktop, StringComparison.OrdinalIgnoreCase);
                }

                // If source is relative, assume Desktop
                if (!Path.IsPathRooted(source))
                    source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), source);
                
                // If dest is relative, assume Desktop
                if (!Path.IsPathRooted(dest))
                    dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), dest);

                // DEBUG: Log the actual paths being used
                string debugPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "FluxDebug.txt");
                File.AppendAllText(debugPath, $"[MOVE] Source: {source} | Dest: {dest}\n");

                string userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string publicDesktop = @"C:\Users\Public\Desktop";

                // Smart file resolution: try multiple locations and extensions
                string? resolvedSource = null;
                string[] candidatePaths = new[]
                {
                    source,                                         // Exact path
                    source + ".lnk",                               // Add .lnk extension
                    Path.Combine(publicDesktop, Path.GetFileName(source)),           // Try Public Desktop
                    Path.Combine(publicDesktop, Path.GetFileName(source) + ".lnk"),  // Public Desktop + .lnk
                    Path.Combine(userDesktop, Path.GetFileName(source)),             // User Desktop (if relative)
                    Path.Combine(userDesktop, Path.GetFileName(source) + ".lnk"),    // User Desktop + .lnk
                };

                foreach (var candidate in candidatePaths)
                {
                    if (File.Exists(candidate))
                    {
                        resolvedSource = candidate;
                        if (candidate != source)
                        {
                            File.AppendAllText(debugPath, $"[MOVE] Resolved to: {candidate}\n");
                        }
                        break;
                    }
                }

                if (resolvedSource == null)
                {
                    File.AppendAllText(debugPath, $"[MOVE] FAILED - Tried: {string.Join(", ", candidatePaths)}\n");
                    return new ExecutionResult(false, $"Source file not found: {source}");
                }
                
                source = resolvedSource;
                
                // If dest is a directory, append filename
                if (Directory.Exists(dest))
                    dest = Path.Combine(dest, Path.GetFileName(source));

                // Ensure dest dir exists
                string? dir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.Move(source, dest, true); // Overwrite allowed
                
                return new ExecutionResult(true, $"Moved to: {dest}");
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"Move failed: {ex.Message}");
            }
        }

        public async Task<ExecutionResult> MakeDirAsync(string path)
        {
            try
            {
                path = Environment.ExpandEnvironmentVariables(path);
                
                string actualProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (path.StartsWith(@"C:\Users\User", StringComparison.OrdinalIgnoreCase)) 
                    path = path.Replace(@"C:\Users\User", actualProfile, StringComparison.OrdinalIgnoreCase);

                // FIX: AI might guess wrong Desktop path (e.g., C:\Users\adila\Desktop when OneDrive is used)
                string actualDesktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string wrongDesktopPath = Path.Combine(actualProfile, "Desktop");
                if (!actualDesktop.Equals(wrongDesktopPath, StringComparison.OrdinalIgnoreCase) &&
                    path.StartsWith(wrongDesktopPath, StringComparison.OrdinalIgnoreCase))
                {
                    path = path.Replace(wrongDesktopPath, actualDesktop, StringComparison.OrdinalIgnoreCase);
                }

                // If path is relative, assume Desktop
                if (!Path.IsPathRooted(path))
                    path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), path);

                // DEBUG: Log the actual path being used
                string debugPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "FluxDebug.txt");
                File.AppendAllText(debugPath, $"[MKDIR] Path: {path}\n");

                Directory.CreateDirectory(path);
                return new ExecutionResult(true, $"Created directory: {path}");
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"MkDir failed: {ex.Message}");
            }
        }

        // =========================================
        // DIRECT FILE OPERATIONS
        // =========================================

        /// <summary>
        /// Writes content directly to a file.
        /// </summary>
        public async Task<ExecutionResult> WriteFileAsync(string path, string content)
        {
            try
            {
                // Expand environment variables
                path = Environment.ExpandEnvironmentVariables(path);
                
                // Create directory if needed
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                await File.WriteAllTextAsync(path, content, Encoding.UTF8);
                
                return new ExecutionResult(true, $"File written: {path} ({content.Length} chars)");
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"Write failed: {ex.Message}");
            }
        }

        // =========================================
        // CLIPBOARD
        // =========================================

        /// <summary>
        /// Copies text to clipboard.
        /// </summary>
        public ExecutionResult SetClipboard(string text)
        {
            try
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    System.Windows.Clipboard.SetText(text);
                });
                return new ExecutionResult(true, $"Copied to clipboard: {text.Substring(0, Math.Min(50, text.Length))}...");
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"Clipboard failed: {ex.Message}");
            }
        }

        // =========================================
        // DOWNLOAD FILE
        // =========================================

        /// <summary>
        /// Downloads a file from URL.
        /// </summary>
        public async Task<ExecutionResult> DownloadFileAsync(string url, string savePath)
        {
            try
            {
                savePath = Environment.ExpandEnvironmentVariables(savePath);
                
                string? dir = Path.GetDirectoryName(savePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                using var client = new System.Net.Http.HttpClient();
                client.Timeout = TimeSpan.FromSeconds(60);
                
                var response = await client.GetAsync(url);
                response.EnsureSuccessStatusCode();
                
                var bytes = await response.Content.ReadAsByteArrayAsync();
                await File.WriteAllBytesAsync(savePath, bytes);
                
                return new ExecutionResult(true, $"Downloaded: {savePath} ({bytes.Length} bytes)");
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"Download failed: {ex.Message}");
            }
        }

        // =========================================
        // NODE.JS EXECUTION
        // =========================================

        /// <summary>
        /// Runs Node.js code and returns output.
        /// </summary>
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

        /// <summary>
        /// Runs Python code and returns output.
        /// </summary>
        public async Task<ExecutionResult> RunPythonAsync(string code)
        {
            try
            {
                // Create temp file
                string tempFile = Path.Combine(_tempDir, $"script_{Guid.NewGuid():N}.py");
                await File.WriteAllTextAsync(tempFile, code);

                try
                {
                    // Try to find Python
                    string pythonPath = FindPython();
                    if (string.IsNullOrEmpty(pythonPath))
                        return new ExecutionResult(false, "Python not found. Install Python and add to PATH.");

                    var result = await RunProcessAsync(pythonPath, $"\"{tempFile}\"");
                    return result;
                }
                finally
                {
                    // Cleanup
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
            // Check common paths
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

        /// <summary>
        /// Runs PowerShell commands and returns output.
        /// </summary>
        public async Task<ExecutionResult> RunPowerShellAsync(string command)
        {
            try
            {
                // Escape for PowerShell
                string escapedCmd = command.Replace("\"", "\\\"");
                return await RunProcessAsync("powershell.exe", $"-NoProfile -NonInteractive -Command \"{escapedCmd}\"");
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"PowerShell error: {ex.Message}");
            }
        }

        // =========================================
        // CMD EXECUTION
        // =========================================

        /// <summary>
        /// Runs CMD commands and returns output.
        /// </summary>
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
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

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
                    return new ExecutionResult(false, "Execution timed out (30s limit)");
                }

                string resultOutput = output.ToString().Trim();
                string resultError = error.ToString().Trim();

                // Truncate if too long
                if (resultOutput.Length > MAX_OUTPUT_LENGTH)
                    resultOutput = resultOutput.Substring(0, MAX_OUTPUT_LENGTH) + "\n...[truncated]";

                if (process.ExitCode != 0 && !string.IsNullOrEmpty(resultError))
                    return new ExecutionResult(false, $"Exit code {process.ExitCode}: {resultError}");

                return new ExecutionResult(true, string.IsNullOrEmpty(resultOutput) ? "(no output)" : resultOutput);
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"Process error: {ex.Message}");
            }
        }
    }
}
