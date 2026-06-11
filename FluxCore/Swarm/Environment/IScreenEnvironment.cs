using System;
using System.Threading;
using System.Threading.Tasks;

namespace FluxCore.Swarm.Environment
{
    /// <summary>
    /// State of the screen environment (VM or RDP session).
    /// </summary>
    public enum ScreenEnvironmentState
    {
        NotInitialized,
        Starting,
        Running,
        Stopping,
        Stopped,
        Error
    }

    /// <summary>
    /// Configuration for the screen environment.
    /// </summary>
    public class ScreenEnvironmentConfig
    {
        public string Name { get; init; } = "FluxScreen";
        public int ScreenWidth { get; init; } = 1920;
        public int ScreenHeight { get; init; } = 1080;
        public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromMinutes(2);
        public bool AutoStart { get; init; } = true;
    }

    /// <summary>
    /// Interface for isolated screen environments (Hyper-V VM or RDP session).
    /// The screen agent uses this to capture screenshots and interact with the UI
    /// in a completely separate environment from the user's main desktop.
    /// </summary>
    public interface IScreenEnvironment : IAsyncDisposable
    {
        /// <summary>
        /// Current state of the environment.
        /// </summary>
        ScreenEnvironmentState State { get; }

        /// <summary>
        /// Connection info for debugging/display purposes.
        /// </summary>
        string ConnectionInfo { get; }

        /// <summary>
        /// Ensure the environment is running and ready for use.
        /// </summary>
        Task EnsureRunningAsync(CancellationToken ct = default);

        /// <summary>
        /// Capture a screenshot from the environment.
        /// </summary>
        /// <returns>Base64-encoded PNG screenshot</returns>
        Task<string> CaptureScreenshotAsync(CancellationToken ct = default);

        /// <summary>
        /// Click at the specified coordinates.
        /// </summary>
        Task ClickAsync(int x, int y, CancellationToken ct = default);

        /// <summary>
        /// Double-click at the specified coordinates.
        /// </summary>
        Task DoubleClickAsync(int x, int y, CancellationToken ct = default);

        /// <summary>
        /// Right-click at the specified coordinates.
        /// </summary>
        Task RightClickAsync(int x, int y, CancellationToken ct = default);

        /// <summary>
        /// Type text at the current focus.
        /// </summary>
        Task TypeTextAsync(string text, CancellationToken ct = default);

        /// <summary>
        /// Send keyboard keys (e.g., "ENTER", "CTRL+C").
        /// </summary>
        Task SendKeysAsync(string keys, CancellationToken ct = default);

        /// <summary>
        /// Scroll at the specified coordinates.
        /// </summary>
        Task ScrollAsync(int x, int y, int deltaY, CancellationToken ct = default);

        /// <summary>
        /// Get the title of the active window.
        /// </summary>
        Task<string> GetActiveWindowTitleAsync(CancellationToken ct = default);

        /// <summary>
        /// Launch an application inside the environment.
        /// </summary>
        Task<bool> LaunchApplicationAsync(string path, string? arguments = null, CancellationToken ct = default);

        /// <summary>
        /// Execute a PowerShell command inside the environment.
        /// </summary>
        Task<(bool Success, string Output)> ExecutePowerShellAsync(string command, CancellationToken ct = default);

        /// <summary>
        /// Copy a file into the environment.
        /// </summary>
        Task CopyFileToEnvironmentAsync(string sourcePath, string destPath, CancellationToken ct = default);

        /// <summary>
        /// Copy a file from the environment.
        /// </summary>
        Task CopyFileFromEnvironmentAsync(string sourcePath, string destPath, CancellationToken ct = default);

        /// <summary>
        /// Shutdown the environment.
        /// </summary>
        Task ShutdownAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Stub implementation for when no VM is available.
    /// Uses the local screen (with warnings) - mainly for testing.
    /// </summary>
    public class LocalScreenEnvironment : IScreenEnvironment
    {
        private readonly WindowsAutomationAgent _automation;
        private readonly SensoryCortex _sensory;

        public ScreenEnvironmentState State { get; private set; } = ScreenEnvironmentState.NotInitialized;
        public string ConnectionInfo => "Local (WARNING: Uses your screen!)";

        public LocalScreenEnvironment()
        {
            _automation = new WindowsAutomationAgent();
            _sensory = new SensoryCortex();
        }

        public Task EnsureRunningAsync(CancellationToken ct = default)
        {
            State = ScreenEnvironmentState.Running;
            return Task.CompletedTask;
        }

        public Task<string> CaptureScreenshotAsync(CancellationToken ct = default)
        {
            return Task.FromResult(_sensory.GetScreenBase64());
        }

        public async Task ClickAsync(int x, int y, CancellationToken ct = default)
        {
            await _automation.ClickElementAsync($"{x},{y}");
        }

        public async Task DoubleClickAsync(int x, int y, CancellationToken ct = default)
        {
            await _automation.ClickElementAsync($"{x},{y}");
            await Task.Delay(50, ct);
            await _automation.ClickElementAsync($"{x},{y}");
        }

        public async Task RightClickAsync(int x, int y, CancellationToken ct = default)
        {
            // WindowsAutomationAgent would need to support right-click
            // For now, just do a regular click
            await _automation.ClickElementAsync($"{x},{y}");
        }

        public async Task TypeTextAsync(string text, CancellationToken ct = default)
        {
            await _automation.TypeTextAsync("", text);
        }

        public async Task SendKeysAsync(string keys, CancellationToken ct = default)
        {
            await _automation.SendKeysAsync(keys);
        }

        public async Task ScrollAsync(int x, int y, int deltaY, CancellationToken ct = default)
        {
            await _automation.ScrollAsync(deltaY > 0 ? "down" : "up");
        }

        public Task<string> GetActiveWindowTitleAsync(CancellationToken ct = default)
        {
            return Task.FromResult(_sensory.GetActiveWindow());
        }

        public async Task<bool> LaunchApplicationAsync(string path, string? arguments = null, CancellationToken ct = default)
        {
            try
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    Arguments = arguments ?? "",
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(startInfo);
                await Task.Delay(1000, ct); // Wait for app to start
                return true;
            }
            catch
            {
                return false;
            }
        }

        public async Task<(bool Success, string Output)> ExecutePowerShellAsync(string command, CancellationToken ct = default)
        {
            var codeRunner = new CodeExecutionAgent();
            var result = await codeRunner.RunPowerShellAsync(command);
            return (result.Success, result.Message);
        }

        public Task CopyFileToEnvironmentAsync(string sourcePath, string destPath, CancellationToken ct = default)
        {
            System.IO.File.Copy(sourcePath, destPath, overwrite: true);
            return Task.CompletedTask;
        }

        public Task CopyFileFromEnvironmentAsync(string sourcePath, string destPath, CancellationToken ct = default)
        {
            System.IO.File.Copy(sourcePath, destPath, overwrite: true);
            return Task.CompletedTask;
        }

        public Task ShutdownAsync(CancellationToken ct = default)
        {
            State = ScreenEnvironmentState.Stopped;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            State = ScreenEnvironmentState.Stopped;
            return ValueTask.CompletedTask;
        }
    }
}
