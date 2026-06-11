using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace FluxCore
{
    /// <summary>
    /// Reads Windows toast notifications from the system notification database.
    /// No package identity required — reads wpndatabase.db directly as read-only.
    /// </summary>
    public class NotificationService : IDisposable
    {
        private readonly string _dbPath;
        private System.Threading.Timer? _timer;
        private readonly List<string> _entries = new();
        private readonly object _lock = new();
        private long _lastRowId = 0;
        private const int MAX = 50;

        public bool IsAvailable { get; private set; }

        /// <summary>Optional — set after construction to persist events to the data lake.</summary>
        public DataLakeService? DataLake { get; set; }

        public NotificationService()
        {
            _dbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Windows", "Notifications", "wpndatabase.db");

            if (!File.Exists(_dbPath)) return;

            IsAvailable = true;
            LoadInitialRowId();
            _timer = new System.Threading.Timer(_ => PollNew(), null,
                TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }

        private void LoadInitialRowId()
        {
            try
            {
                using var conn = OpenReadOnly();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT MAX(rowid) FROM Notification";
                var result = cmd.ExecuteScalar();
                _lastRowId = result is long l ? l : 0;
            }
            catch { IsAvailable = false; }
        }

        private void PollNew()
        {
            try
            {
                using var conn = OpenReadOnly();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT n.rowid, n.ArrivalTime, n.Payload, h.PrimaryId
                    FROM Notification n
                    LEFT JOIN NotificationHandler h ON n.HandlerID = h.ID
                    WHERE n.rowid > $lastId
                    ORDER BY n.ArrivalTime ASC
                    LIMIT 25";
                cmd.Parameters.AddWithValue("$lastId", _lastRowId);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    long rowid   = reader.GetInt64(0);
                    long arrTime = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                    string? xml  = reader.IsDBNull(2) ? null : reader.GetString(2);
                    string appId = reader.IsDBNull(3) ? "?" : reader.GetString(3);

                    string text = ParseToastXml(xml);
                    if (string.IsNullOrWhiteSpace(text)) { _lastRowId = Math.Max(_lastRowId, rowid); continue; }

                    DateTime when = arrTime > 0
                        ? TryParseTime(arrTime)
                        : DateTime.Now;

                    string cleanApp = CleanAppId(appId);
                    string entry = $"[{when:HH:mm}] {cleanApp}: {text}";
                    lock (_lock)
                    {
                        _entries.Add(entry);
                        if (_entries.Count > MAX) _entries.RemoveAt(0);
                    }

                    // Persist to data lake (outside lock)
                    DataLake?.Write("notification", text, new { app = cleanApp });
                    _lastRowId = Math.Max(_lastRowId, rowid);
                }
            }
            catch { }
        }

        private static DateTime TryParseTime(long raw)
        {
            // Windows stores ArrivalTime as FILETIME (100ns intervals since 1601-01-01)
            // Values > 1e17 are FILETIME, smaller values are Unix timestamps
            try
            {
                if (raw > 1_000_000_000_000_0000L) // FILETIME range
                    return DateTime.FromFileTimeUtc(raw).ToLocalTime();
                return DateTimeOffset.FromUnixTimeSeconds(raw).LocalDateTime;
            }
            catch { return DateTime.Now; }
        }

        private static string ParseToastXml(string? xml)
        {
            if (string.IsNullOrEmpty(xml)) return "";
            try
            {
                var doc = XElement.Parse(xml);
                var texts = doc.Descendants("text")
                    .Select(t => t.Value.Trim())
                    .Where(t => t.Length > 0)
                    .Take(2);
                return string.Join(" — ", texts);
            }
            catch { return ""; }
        }

        private static string CleanAppId(string appId)
        {
            // "Microsoft.Windows.Cortana_8wekyb3d8bbwe!CortanaUI" → "Cortana"
            // "chrome.exe" → "chrome"
            if (appId.Contains('!')) appId = appId.Split('!')[0];
            if (appId.Contains('_')) appId = appId.Split('_')[0];
            var parts = appId.Split('.');
            var name  = parts.LastOrDefault() ?? appId;
            return name.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
        }

        private SqliteConnection OpenReadOnly()
        {
            // Shared cache + read-only avoids locking Windows' active DB
            var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly;Cache=Shared");
            conn.Open();
            return conn;
        }

        public string GetRecentNotifications(int count = 15)
        {
            lock (_lock)
            {
                if (_entries.Count == 0) return "";
                var sb = new StringBuilder("=== NOTIFICATIONS ===\n");
                foreach (var e in _entries.TakeLast(count))
                    sb.AppendLine($"  {e}");
                return sb.ToString();
            }
        }

        public void Dispose() => _timer?.Dispose();
    }
}
