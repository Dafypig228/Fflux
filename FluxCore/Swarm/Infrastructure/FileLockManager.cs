using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FluxCore.Swarm.Infrastructure
{
    /// <summary>
    /// Information about a file lock.
    /// </summary>
    public class FileLockInfo
    {
        public string FilePath { get; init; } = "";
        public string OwnerAgentId { get; init; } = "";
        public DateTime AcquiredAt { get; init; }
        public DateTime ExpiresAt { get; init; }
        public bool IsExpired => DateTime.UtcNow > ExpiresAt;
    }

    /// <summary>
    /// Handle to a file lock that can be released or extended.
    /// </summary>
    public interface IFileLock : IAsyncDisposable
    {
        string FilePath { get; }
        string OwnerAgentId { get; }
        DateTime AcquiredAt { get; }
        DateTime ExpiresAt { get; }
        bool IsValid { get; }
        Task<bool> ExtendAsync(TimeSpan extension);
    }

    /// <summary>
    /// Manages file locks to prevent concurrent editing conflicts.
    /// </summary>
    public interface IFileLockManager
    {
        Task<IFileLock?> TryAcquireLockAsync(string filePath, string agentId, TimeSpan timeout);
        Task ReleaseLockAsync(string filePath, string agentId);
        Task ReleaseAllLocksAsync(string agentId);
        Task<bool> IsLockedAsync(string filePath);
        Task<string?> GetLockOwnerAsync(string filePath);
        IAsyncEnumerable<FileLockInfo> GetAllLocksAsync();
        Task<bool> WaitForLockAsync(string filePath, string agentId, TimeSpan timeout, CancellationToken ct = default);
    }

    /// <summary>
    /// Internal lock entry with thread-safe operations.
    /// </summary>
    internal class FileLockEntry
    {
        public string FilePath { get; init; } = "";
        public string OwnerAgentId { get; set; } = "";
        public DateTime AcquiredAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public bool IsExpired => DateTime.UtcNow > ExpiresAt;
    }

    /// <summary>
    /// Concrete file lock handle.
    /// </summary>
    internal class FileLockHandle : IFileLock
    {
        private readonly FileLockManager _manager;
        private bool _isReleased;

        public string FilePath { get; }
        public string OwnerAgentId { get; }
        public DateTime AcquiredAt { get; }
        public DateTime ExpiresAt { get; private set; }
        public bool IsValid => !_isReleased && DateTime.UtcNow <= ExpiresAt;

        public FileLockHandle(FileLockManager manager, string filePath, string ownerAgentId, DateTime acquiredAt, DateTime expiresAt)
        {
            _manager = manager;
            FilePath = filePath;
            OwnerAgentId = ownerAgentId;
            AcquiredAt = acquiredAt;
            ExpiresAt = expiresAt;
        }

        public async Task<bool> ExtendAsync(TimeSpan extension)
        {
            if (_isReleased) return false;

            var newExpiry = DateTime.UtcNow + extension;
            var success = await _manager.ExtendLockAsync(FilePath, OwnerAgentId, newExpiry);

            if (success)
                ExpiresAt = newExpiry;

            return success;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_isReleased)
            {
                _isReleased = true;
                await _manager.ReleaseLockAsync(FilePath, OwnerAgentId);
            }
        }
    }

    /// <summary>
    /// In-memory file lock manager for preventing concurrent file edits.
    /// </summary>
    public class FileLockManager : IFileLockManager
    {
        private readonly ConcurrentDictionary<string, FileLockEntry> _locks = new(StringComparer.OrdinalIgnoreCase);
        private readonly TimeSpan _defaultLockDuration;
        private readonly System.Threading.Timer _expirationTimer;

        public FileLockManager(TimeSpan? defaultLockDuration = null)
        {
            _defaultLockDuration = defaultLockDuration ?? TimeSpan.FromMinutes(5);

            // Periodically clean up expired locks
            _expirationTimer = new System.Threading.Timer(CleanupExpiredLocks, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        public async Task<IFileLock?> TryAcquireLockAsync(string filePath, string agentId, TimeSpan timeout)
        {
            var normalizedPath = NormalizePath(filePath);
            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                var entry = _locks.GetOrAdd(normalizedPath, _ => new FileLockEntry
                {
                    FilePath = normalizedPath
                });

                await entry.Semaphore.WaitAsync();
                try
                {
                    // Check if lock is available (not owned or expired)
                    if (string.IsNullOrEmpty(entry.OwnerAgentId) || entry.IsExpired)
                    {
                        // Acquire the lock
                        var now = DateTime.UtcNow;
                        entry.OwnerAgentId = agentId;
                        entry.AcquiredAt = now;
                        entry.ExpiresAt = now + _defaultLockDuration;

                        return new FileLockHandle(this, normalizedPath, agentId, now, entry.ExpiresAt);
                    }

                    // Check if we already own it
                    if (entry.OwnerAgentId == agentId)
                    {
                        // Extend our existing lock
                        entry.ExpiresAt = DateTime.UtcNow + _defaultLockDuration;
                        return new FileLockHandle(this, normalizedPath, agentId, entry.AcquiredAt, entry.ExpiresAt);
                    }
                }
                finally
                {
                    entry.Semaphore.Release();
                }

                // Lock is held by someone else, wait a bit and retry
                await Task.Delay(100);
            }

            // Timeout - couldn't acquire lock
            return null;
        }

        public async Task ReleaseLockAsync(string filePath, string agentId)
        {
            var normalizedPath = NormalizePath(filePath);

            if (_locks.TryGetValue(normalizedPath, out var entry))
            {
                await entry.Semaphore.WaitAsync();
                try
                {
                    if (entry.OwnerAgentId == agentId)
                    {
                        entry.OwnerAgentId = "";
                        entry.AcquiredAt = default;
                        entry.ExpiresAt = default;
                    }
                }
                finally
                {
                    entry.Semaphore.Release();
                }
            }
        }

        public async Task ReleaseAllLocksAsync(string agentId)
        {
            var locksToRelease = _locks.Values
                .Where(e => e.OwnerAgentId == agentId)
                .Select(e => e.FilePath)
                .ToList();

            foreach (var path in locksToRelease)
            {
                await ReleaseLockAsync(path, agentId);
            }
        }

        public Task<bool> IsLockedAsync(string filePath)
        {
            var normalizedPath = NormalizePath(filePath);

            if (_locks.TryGetValue(normalizedPath, out var entry))
            {
                return Task.FromResult(!string.IsNullOrEmpty(entry.OwnerAgentId) && !entry.IsExpired);
            }

            return Task.FromResult(false);
        }

        public Task<string?> GetLockOwnerAsync(string filePath)
        {
            var normalizedPath = NormalizePath(filePath);

            if (_locks.TryGetValue(normalizedPath, out var entry) && !entry.IsExpired)
            {
                return Task.FromResult<string?>(entry.OwnerAgentId);
            }

            return Task.FromResult<string?>(null);
        }

        public async IAsyncEnumerable<FileLockInfo> GetAllLocksAsync()
        {
            foreach (var entry in _locks.Values)
            {
                if (!string.IsNullOrEmpty(entry.OwnerAgentId) && !entry.IsExpired)
                {
                    yield return new FileLockInfo
                    {
                        FilePath = entry.FilePath,
                        OwnerAgentId = entry.OwnerAgentId,
                        AcquiredAt = entry.AcquiredAt,
                        ExpiresAt = entry.ExpiresAt
                    };
                }

                await Task.Yield(); // Allow other operations
            }
        }

        public async Task<bool> WaitForLockAsync(string filePath, string agentId, TimeSpan timeout, CancellationToken ct = default)
        {
            var lockHandle = await TryAcquireLockAsync(filePath, agentId, timeout);

            if (lockHandle != null)
            {
                return true;
            }

            return false;
        }

        internal async Task<bool> ExtendLockAsync(string filePath, string agentId, DateTime newExpiry)
        {
            var normalizedPath = NormalizePath(filePath);

            if (_locks.TryGetValue(normalizedPath, out var entry))
            {
                await entry.Semaphore.WaitAsync();
                try
                {
                    if (entry.OwnerAgentId == agentId && !entry.IsExpired)
                    {
                        entry.ExpiresAt = newExpiry;
                        return true;
                    }
                }
                finally
                {
                    entry.Semaphore.Release();
                }
            }

            return false;
        }

        private void CleanupExpiredLocks(object? state)
        {
            foreach (var kvp in _locks)
            {
                if (kvp.Value.IsExpired && kvp.Value.Semaphore.Wait(0))
                {
                    try
                    {
                        if (kvp.Value.IsExpired)
                        {
                            kvp.Value.OwnerAgentId = "";
                        }
                    }
                    finally
                    {
                        kvp.Value.Semaphore.Release();
                    }
                }
            }
        }

        private static string NormalizePath(string path)
        {
            // Normalize path for case-insensitive comparison on Windows
            return System.IO.Path.GetFullPath(path).ToLowerInvariant();
        }
    }
}
