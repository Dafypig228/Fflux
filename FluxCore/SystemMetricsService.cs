using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;

namespace FluxCore
{
    /// <summary>
    /// Polls CPU, RAM, and network metrics every 5 seconds.
    /// Uses PerformanceCounter — no WMI, no admin required.
    /// </summary>
    public class SystemMetricsService : IDisposable
    {
        private PerformanceCounter? _cpu;
        private PerformanceCounter? _netSent;
        private PerformanceCounter? _netRecv;
        private System.Threading.Timer? _timer;
        private readonly object _lock = new();

        private float _cpuPct;
        private long  _ramUsedMb;
        private long  _ramTotalMb;
        private float _netSentKbs;
        private float _netRecvKbs;
        private DateTime _lastPoll = DateTime.MinValue;

        public SystemMetricsService()
        {
            try
            {
                _cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _cpu.NextValue(); // First call always returns 0 — discard

                // Pick first active network interface
                var category = new PerformanceCounterCategory("Network Interface");
                var instances = category.GetInstanceNames()
                    .Where(n => !n.Contains("Loopback", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                if (instances.Length > 0)
                {
                    _netSent = new PerformanceCounter("Network Interface", "Bytes Sent/sec",     instances[0]);
                    _netRecv = new PerformanceCounter("Network Interface", "Bytes Received/sec", instances[0]);
                }
            }
            catch { }

            _ramTotalMb = GetTotalRamMb();

            // Poll every 5 seconds
            _timer = new System.Threading.Timer(_ => Poll(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
        }

        private void Poll()
        {
            try
            {
                float cpu      = _cpu?.NextValue() ?? 0;
                long  ramUsed  = GetUsedRamMb();
                float netSent  = (_netSent?.NextValue() ?? 0) / 1024f; // bytes → KB/s
                float netRecv  = (_netRecv?.NextValue() ?? 0) / 1024f;

                lock (_lock)
                {
                    _cpuPct      = cpu;
                    _ramUsedMb   = ramUsed;
                    _netSentKbs  = netSent;
                    _netRecvKbs  = netRecv;
                    _lastPoll    = DateTime.Now;
                }
            }
            catch { }
        }

        private static long GetTotalRamMb()
        {
            try
            {
                using var mc = new System.Management.ManagementObjectSearcher(
                    "SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem");
                foreach (var mo in mc.Get())
                    return (long)(ulong)mo["TotalVisibleMemorySize"] / 1024;
            }
            catch { }
            return 0;
        }

        private static long GetUsedRamMb()
        {
            try
            {
                using var mc = new System.Management.ManagementObjectSearcher(
                    "SELECT FreePhysicalMemory, TotalVisibleMemorySize FROM Win32_OperatingSystem");
                foreach (var mo in mc.Get())
                {
                    long total = (long)(ulong)mo["TotalVisibleMemorySize"] / 1024;
                    long free  = (long)(ulong)mo["FreePhysicalMemory"]     / 1024;
                    return total - free;
                }
            }
            catch { }
            return 0;
        }

        /// <summary>Returns a one-liner system metrics snapshot for LLM context.</summary>
        public string GetMetricsSummary()
        {
            lock (_lock)
            {
                if (_lastPoll == DateTime.MinValue) return "";
                long ramPct = _ramTotalMb > 0 ? _ramUsedMb * 100 / _ramTotalMb : 0;
                return $"=== SYSTEM ===\n  CPU:{_cpuPct:0}%  RAM:{_ramUsedMb}MB/{_ramTotalMb}MB ({ramPct}%)  Net:↑{_netSentKbs:0}KB/s ↓{_netRecvKbs:0}KB/s";
            }
        }

        public void Dispose()
        {
            _timer?.Dispose();
            _cpu?.Dispose();
            _netSent?.Dispose();
            _netRecv?.Dispose();
        }
    }
}
