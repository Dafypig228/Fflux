using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace FluxCore
{
    /// <summary>
    /// A single entry in the commitment store.
    /// </summary>
    public record CommitmentEntry(
        string   Id,
        DateTime CreatedAt,
        DateTime DueAt,
        string   Description,
        string   OriginMessage,
        string   ContextSnapshot,
        string   Status,
        int      Retries);

    /// <summary>
    /// SQLite-backed store for Davos's time-deferred commitments.
    /// Commitments survive app restarts.
    ///
    /// Thread-safety: every public method opens its own connection, so callers
    /// on any thread (including PeriodicTimer loop on ThreadPool) are safe.
    /// </summary>
    public class CommitmentStore : IDisposable
    {
        private readonly string _dbPath;

        public CommitmentStore(string dbPath)
        {
            _dbPath = dbPath;
            EnsureSchema();
        }

        // ── Schema ─────────────────────────────────────────────────────────────

        private void EnsureSchema()
        {
            using var conn = Open();
            using var cmd  = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS commitments (
                    id               TEXT NOT NULL PRIMARY KEY,
                    created_at       TEXT NOT NULL,
                    due_at           TEXT NOT NULL,
                    description      TEXT NOT NULL,
                    origin_message   TEXT NOT NULL,
                    context_snapshot TEXT NOT NULL,
                    status           TEXT NOT NULL DEFAULT 'Pending',
                    retries          INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS idx_due
                    ON commitments (due_at, status);";
            cmd.ExecuteNonQuery();
        }

        // ── Write ──────────────────────────────────────────────────────────────

        /// <summary>Adds a new commitment and returns its generated ID.</summary>
        public string Add(TimeSpan delay, string description, string originMsg, string contextSnapshot)
        {
            var id        = Guid.NewGuid().ToString("N");
            var createdAt = DateTime.UtcNow;
            var dueAt     = createdAt + delay;

            using var conn = Open();
            using var cmd  = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO commitments
                    (id, created_at, due_at, description, origin_message, context_snapshot, status, retries)
                VALUES
                    ($id, $created, $due, $desc, $origin, $ctx, 'Pending', 0)";
            cmd.Parameters.AddWithValue("$id",      id);
            cmd.Parameters.AddWithValue("$created", createdAt.ToString("O"));
            cmd.Parameters.AddWithValue("$due",     dueAt.ToString("O"));
            cmd.Parameters.AddWithValue("$desc",    description);
            cmd.Parameters.AddWithValue("$origin",  originMsg);
            cmd.Parameters.AddWithValue("$ctx",     contextSnapshot);
            cmd.ExecuteNonQuery();
            return id;
        }

        // ── Read + atomic state transition ────────────────────────────────────

        /// <summary>
        /// Returns all commitments whose due_at is in the past and whose status
        /// is Pending, AND atomically marks them as Executing in a single
        /// SQLite transaction — so a second loop tick never sees the same row again.
        /// </summary>
        public IReadOnlyList<CommitmentEntry> GetDue()
        {
            var now     = DateTime.UtcNow.ToString("O");
            var entries = new List<CommitmentEntry>();

            using var conn = Open();
            using var tx   = conn.BeginTransaction();

            // 1. Read
            using (var sel = conn.CreateCommand())
            {
                sel.Transaction = tx;
                sel.CommandText = @"
                    SELECT id, created_at, due_at, description,
                           origin_message, context_snapshot, status, retries
                    FROM   commitments
                    WHERE  status = 'Pending'
                    AND    due_at <= $now";
                sel.Parameters.AddWithValue("$now", now);

                using var reader = sel.ExecuteReader();
                while (reader.Read())
                    entries.Add(Row(reader));
            }

            // 2. Mark Executing (same transaction — atomic)
            if (entries.Count > 0)
            {
                using var upd = conn.CreateCommand();
                upd.Transaction = tx;
                upd.CommandText = @"
                    UPDATE commitments
                    SET    status = 'Executing'
                    WHERE  status = 'Pending'
                    AND    due_at <= $now";
                upd.Parameters.AddWithValue("$now", now);
                upd.ExecuteNonQuery();
            }

            tx.Commit();
            return entries;
        }

        public void MarkDone(string id)    => SetStatus(id, "Done");
        public void MarkFailed(string id)  => SetStatus(id, "Failed");

        public IReadOnlyList<CommitmentEntry> GetAll()
        {
            var entries = new List<CommitmentEntry>();
            using var conn = Open();
            using var cmd  = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, created_at, due_at, description,
                       origin_message, context_snapshot, status, retries
                FROM   commitments
                ORDER  BY due_at DESC";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) entries.Add(Row(reader));
            return entries;
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private void SetStatus(string id, string status)
        {
            using var conn = Open();
            using var cmd  = conn.CreateCommand();
            cmd.CommandText = "UPDATE commitments SET status = $s WHERE id = $id";
            cmd.Parameters.AddWithValue("$s",  status);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }

        private static CommitmentEntry Row(SqliteDataReader r) => new(
            r.GetString(0),
            DateTime.Parse(r.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind),
            DateTime.Parse(r.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind),
            r.GetString(3),
            r.GetString(4),
            r.GetString(5),
            r.GetString(6),
            r.GetInt32(7));

        private SqliteConnection Open()
        {
            var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            return conn;
        }

        public void Dispose()
        {
            // Connections are per-operation (opened and closed in each method),
            // so there is no persistent connection to close here.
            GC.SuppressFinalize(this);
        }
    }
}
