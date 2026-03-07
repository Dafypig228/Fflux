using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace FluxCore
{
    /// <summary>
    /// Passively tracks git status across all known repos.
    /// Polls every 10 seconds — lightweight, no filesystem events needed.
    /// </summary>
    public class GitWatcherService : IDisposable
    {
        private readonly List<string> _repoPaths = new();
        private readonly Dictionary<string, RepoState> _states = new();
        private System.Threading.Timer? _timer;
        private readonly object _lock = new();

        private record RepoState(
            string Branch,
            string LastCommit,
            int Ahead,
            int Behind,
            int Unstaged,
            int Staged,
            DateTime PolledAt);

        public GitWatcherService()
        {
            DiscoverRepos();
            // Initial poll, then every 10 seconds
            _timer = new System.Threading.Timer(_ => PollAll(), null, TimeSpan.Zero, TimeSpan.FromSeconds(10));
        }

        private void DiscoverRepos()
        {
            var roots = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "repos"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "projects"),
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            };

            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                try
                {
                    // Direct child dirs that are git repos
                    foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly))
                    {
                        if (Directory.Exists(Path.Combine(dir, ".git")))
                            _repoPaths.Add(dir);

                        // One level deeper (e.g. ~/source/repos/MyProject)
                        try
                        {
                            foreach (var subdir in Directory.GetDirectories(dir, "*", SearchOption.TopDirectoryOnly))
                            {
                                if (Directory.Exists(Path.Combine(subdir, ".git")))
                                    _repoPaths.Add(subdir);
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }

            // Deduplicate
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _repoPaths.RemoveAll(p => !seen.Add(p));
        }

        private void PollAll()
        {
            foreach (var path in _repoPaths)
            {
                try
                {
                    var state = GetRepoState(path);
                    if (state == null) continue;
                    lock (_lock)
                        _states[path] = state;
                }
                catch { }
            }
        }

        private RepoState? GetRepoState(string repoPath)
        {
            string Run(string args)
            {
                var psi = new ProcessStartInfo("git", args)
                {
                    WorkingDirectory       = repoPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(3000);
                return p?.StandardOutput.ReadToEnd().Trim() ?? "";
            }

            var branch     = Run("rev-parse --abbrev-ref HEAD");
            if (string.IsNullOrEmpty(branch) || branch == "HEAD") return null; // detached / not a repo

            var lastCommit = Run("log -1 --pretty=%s");
            var aheadBehind = Run($"rev-list --left-right --count origin/{branch}...HEAD");
            int ahead = 0, behind = 0;
            if (aheadBehind.Contains('\t'))
            {
                var parts = aheadBehind.Split('\t');
                int.TryParse(parts[0], out behind);
                int.TryParse(parts[1], out ahead);
            }

            var status   = Run("status --short");
            int unstaged = status.Split('\n').Count(l => l.Length > 1 && l[1] != ' ');
            int staged   = status.Split('\n').Count(l => l.Length > 1 && l[0] != ' ' && l[0] != '?');

            return new RepoState(branch, lastCommit, ahead, behind, unstaged, staged, DateTime.Now);
        }

        /// <summary>Returns a compact git summary for all tracked repos.</summary>
        public string GetGitSummary()
        {
            Dictionary<string, RepoState> snapshot;
            lock (_lock)
                snapshot = new Dictionary<string, RepoState>(_states);

            if (snapshot.Count == 0) return "";

            var sb = new StringBuilder();
            sb.AppendLine("=== GIT REPOS ===");
            foreach (var (path, s) in snapshot)
            {
                string name   = Path.GetFileName(path);
                string status = "";
                if (s.Staged   > 0) status += $" {s.Staged}staged";
                if (s.Unstaged > 0) status += $" {s.Unstaged}unstaged";
                if (s.Ahead    > 0) status += $" ↑{s.Ahead}";
                if (s.Behind   > 0) status += $" ↓{s.Behind}";
                if (string.IsNullOrEmpty(status)) status = " clean";

                sb.AppendLine($"  {name} [{s.Branch}]{status} — \"{s.LastCommit}\"");
            }
            return sb.ToString();
        }

        public void Dispose() => _timer?.Dispose();
    }
}
