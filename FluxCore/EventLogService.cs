using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace FluxCore
{
    /// <summary>
    /// Watches Windows Application + System event logs for Error and Warning entries.
    /// Uses EntryWritten event — zero polling cost.
    /// Keeps the last 30 entries in memory.
    /// </summary>
    public class EventLogService : IDisposable
    {
        private readonly EventLog? _appLog;
        private readonly EventLog? _sysLog;
        private readonly List<string> _entries = new();
        private readonly object _lock = new();
        private const int MAX = 30;

        /// <summary>Optional — set after construction to persist events to the data lake.</summary>
        public DataLakeService? DataLake { get; set; }

        public bool IsAvailable { get; private set; }

        public EventLogService()
        {
            try
            {
                _appLog = new EventLog("Application") { EnableRaisingEvents = true };
                _sysLog = new EventLog("System")      { EnableRaisingEvents = true };

                _appLog.EntryWritten += OnEntry;
                _sysLog.EntryWritten += OnEntry;
                IsAvailable = true;
            }
            catch { /* No permission or unavailable — fail silently */ }
        }

        private void OnEntry(object sender, EntryWrittenEventArgs e)
        {
            try
            {
                var entry = e.Entry;
                if (entry == null) return;

                // Only care about errors and warnings
                if (entry.EntryType != EventLogEntryType.Error &&
                    entry.EntryType != EventLogEntryType.Warning) return;

                // Skip noise sources
                string src = entry.Source ?? "";
                if (src.Equals("Microsoft-Windows-Search", StringComparison.OrdinalIgnoreCase)) return;
                if (src.Equals("Windows Search Service",   StringComparison.OrdinalIgnoreCase)) return;

                string level   = entry.EntryType == EventLogEntryType.Error ? "ERROR" : "WARN";
                string message = entry.Message ?? "";
                if (message.Length > 300) message = message[..300] + "…";

                string line = $"[{DateTime.Now:HH:mm}] {level} | {src}: {message.Replace('\n', ' ')}";

                lock (_lock)
                {
                    _entries.Add(line);
                    if (_entries.Count > MAX) _entries.RemoveAt(0);
                }

                // Persist to data lake
                DataLake?.Write("eventlog", $"{src}: {message}",
                    new { source = src, level });
            }
            catch { }
        }

        /// <summary>Returns recent errors/warnings formatted for LLM context.</summary>
        public string GetRecentErrors(int count = 5)
        {
            lock (_lock)
            {
                if (_entries.Count == 0) return "";
                var sb = new StringBuilder("=== EVENT LOG ===\n");
                foreach (var e in _entries.TakeLast(count))
                    sb.AppendLine($"  {e}");
                return sb.ToString();
            }
        }

        public void Dispose()
        {
            try
            {
                if (_appLog != null) { _appLog.EntryWritten -= OnEntry; _appLog.Dispose(); }
                if (_sysLog != null) { _sysLog.EntryWritten -= OnEntry; _sysLog.Dispose(); }
            }
            catch { }
        }
    }
}
