using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FluxCore.SelfCoding
{
    public class GitManager
    {
        private readonly string _repoRoot;
        private string _baseBranch = "";

        public GitManager(string repoRoot)
        {
            _repoRoot = repoRoot;
        }

        public async Task<string> CreateBranchAsync(string name)
        {
            _baseBranch = (await RunGit("branch --show-current")).Trim();
            string safe = Regex.Replace(name, @"[^a-zA-Z0-9\-/]", "-").ToLower();
            if (safe.Length > 40) safe = safe.Substring(0, 40);
            string branchName = $"davos/{safe}";
            await RunGit($"checkout -b {branchName}");
            return branchName;
        }

        public async Task CommitAsync(string message)
        {
            await RunGit("add -A");
            // Escape double quotes in message
            message = message.Replace("\"", "\\\"");
            await RunGit($"commit -m \"{message}\"");
        }

        public async Task<string> GetDiffAsync()
        {
            return await RunGit($"diff {_baseBranch}..HEAD");
        }

        public async Task MergeToCurrentAsync()
        {
            string featureBranch = (await RunGit("branch --show-current")).Trim();
            await RunGit($"checkout {_baseBranch}");
            await RunGit($"merge {featureBranch}");
            await RunGit($"branch -d {featureBranch}");
        }

        public async Task AbandonBranchAsync()
        {
            string current = (await RunGit("branch --show-current")).Trim();
            if (string.IsNullOrEmpty(current) || current == _baseBranch) return;
            await RunGit($"checkout {_baseBranch}");
            await RunGit($"branch -D {current}");
        }

        private async Task<string> RunGit(string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = args,
                WorkingDirectory = _repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return "";

            string output = await process.StandardOutput.ReadToEndAsync();
            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(error))
                System.Diagnostics.Debug.WriteLine($"[Git] Warning: {error.Trim()}");

            return output;
        }
    }
}
