using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FluxCore
{
    public class FileWatcherService : IDisposable
    {
        private record FileEvent(string Action, string Path, DateTime When);

        private readonly List<FileSystemWatcher> _watchers = new();
        private readonly List<FileEvent> _events = new();
        private const int MAX_EVENTS = 50;

        /// <summary>Optional — set after construction to persist events to the data lake.</summary>
        public DataLakeService? DataLake { get; set; }

        public FileWatcherService()
        {
            var watchPaths = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "repos"),
            };

            foreach (var path in watchPaths)
            {
                if (!Directory.Exists(path)) continue;
                try
                {
                    var w = new FileSystemWatcher(path)
                    {
                        NotifyFilter        = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
                        IncludeSubdirectories = true,
                        EnableRaisingEvents = true
                    };
                    w.Created += (_, e) => Record("created",  e.FullPath);
                    w.Changed += (_, e) => Record("modified", e.FullPath);
                    w.Deleted += (_, e) => Record("deleted",  e.FullPath);
                    w.Renamed += (_, e) => Record("renamed",  $"{e.OldName} → {e.Name}");
                    _watchers.Add(w);
                }
                catch { }
            }
        }

        private void Record(string action, string path)
        {
            // Ignore editor temp files and Office lock files
            if (path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return;
            if (Path.GetFileName(path).StartsWith("~$"))                   return;
            if (path.Contains("\\.git\\", StringComparison.OrdinalIgnoreCase)) return;

            var ev = new FileEvent(action, path, DateTime.Now);
            bool persisted = false;
            lock (_events)
            {
                // Deduplicate: same file + action within 2 seconds
                if (_events.Count > 0
                    && _events[^1].Path   == path
                    && _events[^1].Action == action
                    && (ev.When - _events[^1].When).TotalSeconds < 2) return;

                _events.Add(ev);
                if (_events.Count > MAX_EVENTS) _events.RemoveAt(0);
                persisted = true;
            }

            // Persist to data lake (outside lock)
            if (persisted)
                DataLake?.Write("file", $"{action}: {path}", new { action });
        }

        /// <summary>Returns recent file activity formatted for LLM context.</summary>
        public string GetRecentActivity(int count = 10)
        {
            lock (_events)
            {
                if (_events.Count == 0) return "";
                var sb = new StringBuilder();
                sb.AppendLine("=== FILE ACTIVITY ===");
                foreach (var e in _events.TakeLast(count))
                    sb.AppendLine($"  [{e.When:HH:mm:ss}] {e.Action}: {e.Path}");
                return sb.ToString();
            }
        }

        public void Dispose()
        {
            foreach (var w in _watchers) w.Dispose();
        }
    }
}
