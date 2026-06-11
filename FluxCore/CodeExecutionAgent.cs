using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FluxCore
{
    /// <summary>
    /// Code Execution Sandbox: Runs Python, PowerShell, and CMD commands.
    /// Also provides direct file writing capabilities.
    /// </summary>
    public partial class CodeExecutionAgent
    {
        private readonly string _tempDir;
        private const int MAX_OUTPUT_LENGTH = 5000;
        private const int TIMEOUT_MS = 60000; // 60 seconds — batch file ops need more time

        // ── Terminal history ring buffer (Phase F4) ────────────────────────────
        private record TerminalEntry(string Command, string Output, string Cwd, int ExitCode, DateTime When);
        private readonly List<TerminalEntry> _terminalHistory = new();
        private readonly object _termLock = new();
        private const int MAX_TERMINAL_HISTORY = 20;

        /// <summary>Optional — set after construction to persist terminal output to the data lake.</summary>
        public DataLakeService? DataLake { get; set; }

        public CodeExecutionAgent()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "FluxSandbox");
            Directory.CreateDirectory(_tempDir);
        }

        /// <summary>
        /// Called internally after every command execution to record to the terminal ring buffer.
        /// </summary>
        internal void RecordTerminal(string command, string output, string cwd, int exitCode)
        {
            var entry = new TerminalEntry(command, output, cwd, exitCode, DateTime.Now);
            lock (_termLock)
            {
                _terminalHistory.Add(entry);
                if (_terminalHistory.Count > MAX_TERMINAL_HISTORY)
                    _terminalHistory.RemoveAt(0);
            }
            // Persist to data lake
            DataLake?.Write("terminal",
                $"$ {command}\n{output}",
                new { cwd, exit_code = exitCode });
        }

        /// <summary>Returns recent terminal commands + output formatted for LLM context.</summary>
        public string GetRecentTerminalOutput(int count = 10)
        {
            lock (_termLock)
            {
                if (_terminalHistory.Count == 0) return "";
                var sb = new StringBuilder("=== TERMINAL HISTORY ===\n");
                foreach (var e in _terminalHistory.TakeLast(count))
                {
                    sb.AppendLine($"  [{e.When:HH:mm}] $ {e.Command}  (exit:{e.ExitCode})");
                    if (!string.IsNullOrWhiteSpace(e.Output))
                    {
                        var trimmed = e.Output.Length > 300 ? e.Output[..300] + "…" : e.Output;
                        sb.AppendLine($"    {trimmed.Replace("\n", "\n    ")}");
                    }
                }
                return sb.ToString();
            }
        }

        /// <summary>
        /// Sanitizes a path - handles OneDrive, wrong usernames, relative paths.
        /// Also searches common directories if file not found.
        /// </summary>
        private string SanitizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path == ".")
                return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

            path = Environment.ExpandEnvironmentVariables(path);

            string actualProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string actualDesktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string actualDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string wrongDesktopPath = Path.Combine(actualProfile, "Desktop");

            // Handle common shortcuts
            if (path.Equals("Desktop", StringComparison.OrdinalIgnoreCase))
                return actualDesktop;
            if (path.Equals("Documents", StringComparison.OrdinalIgnoreCase))
                return actualDocuments;
            if (path.Equals("Downloads", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(actualProfile, "Downloads");
            if (path.Equals("Pictures", StringComparison.OrdinalIgnoreCase))
                return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (path.Equals("Videos", StringComparison.OrdinalIgnoreCase))
                return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (path.Equals("Music", StringComparison.OrdinalIgnoreCase))
                return Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);

            // Fix wrong username
            if (path.StartsWith(@"C:\Users\User", StringComparison.OrdinalIgnoreCase))
                path = path.Replace(@"C:\Users\User", actualProfile, StringComparison.OrdinalIgnoreCase);

            // Fix wrong Desktop path (OneDrive)
            if (!actualDesktop.Equals(wrongDesktopPath, StringComparison.OrdinalIgnoreCase) &&
                path.StartsWith(wrongDesktopPath, StringComparison.OrdinalIgnoreCase))
            {
                path = path.Replace(wrongDesktopPath, actualDesktop, StringComparison.OrdinalIgnoreCase);
            }

            // Fix wrong Documents path (OneDrive)
            string wrongDocumentsPath = Path.Combine(actualProfile, "Documents");
            if (!actualDocuments.Equals(wrongDocumentsPath, StringComparison.OrdinalIgnoreCase) &&
                path.StartsWith(wrongDocumentsPath, StringComparison.OrdinalIgnoreCase))
            {
                path = path.Replace(wrongDocumentsPath, actualDocuments, StringComparison.OrdinalIgnoreCase);
            }

            // Handle relative paths - try to find the file
            if (!Path.IsPathRooted(path))
            {
                string[] searchLocations = new[]
                {
                    actualDesktop,
                    actualDocuments,
                    Path.Combine(actualProfile, "Downloads"),
                    @"C:\Users\Public\Desktop",
                    Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                };

                foreach (var location in searchLocations)
                {
                    string candidate = Path.Combine(location, path);
                    if (File.Exists(candidate) || Directory.Exists(candidate))
                        return candidate;

                    if (File.Exists(candidate + ".lnk"))
                        return candidate + ".lnk";
                }

                // Default to Desktop if not found
                path = Path.Combine(actualDesktop, path);
            }

            return path;
        }

        public static string GetCommonPathsInfo()
        {
            var sb = new StringBuilder();
            sb.AppendLine("YOUR KNOWN PATHS (USE THESE EXACT PATHS):");
            sb.AppendLine($"  Desktop: {Environment.GetFolderPath(Environment.SpecialFolder.Desktop)}");
            sb.AppendLine($"  Documents: {Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)}");
            sb.AppendLine($"  Downloads: {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")}");
            sb.AppendLine($"  Pictures: {Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)}");
            sb.AppendLine($"  Public Desktop: C:\\Users\\Public\\Desktop");
            return sb.ToString();
        }

        private string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            double size = bytes;
            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }
            return $"{size:0.##} {sizes[order]}";
        }
    }
}
