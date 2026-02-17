using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FluxCore.Swarm.Environment
{
    /// <summary>
    /// Hyper-V VM-based screen environment.
    /// Provides complete isolation - the screen agent operates entirely within the VM.
    /// </summary>
    public class HyperVEnvironment : IScreenEnvironment
    {
        private readonly string _vmName;
        private readonly ScreenEnvironmentConfig _config;
        private readonly string _vmPowerShellScript;

        public ScreenEnvironmentState State { get; private set; } = ScreenEnvironmentState.NotInitialized;
        public string ConnectionInfo => $"HyperV://{_vmName}";

        public HyperVEnvironment(string vmName, ScreenEnvironmentConfig? config = null)
        {
            _vmName = vmName;
            _config = config ?? new ScreenEnvironmentConfig { Name = vmName };

            // PowerShell script template for executing commands in VM
            _vmPowerShellScript = @"
param([string]$Command)
$vm = Get-VM -Name '{0}'
if ($vm.State -ne 'Running') {{ throw 'VM is not running' }}
Invoke-Command -VMName '{0}' -ScriptBlock {{ param($cmd) Invoke-Expression $cmd }} -ArgumentList $Command
";
        }

        public async Task EnsureRunningAsync(CancellationToken ct = default)
        {
            if (State == ScreenEnvironmentState.Running) return;

            State = ScreenEnvironmentState.Starting;

            try
            {
                // Check if Hyper-V is available
                var checkResult = await ExecuteLocalPowerShellAsync("Get-VM -ErrorAction SilentlyContinue | Select-Object -First 1", ct);
                if (!checkResult.Success)
                {
                    throw new InvalidOperationException(
                        "Hyper-V is not available. Please enable Hyper-V:\n" +
                        "Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V -All");
                }

                // Check if VM exists
                var vmCheck = await ExecuteLocalPowerShellAsync($"Get-VM -Name '{_vmName}' -ErrorAction SilentlyContinue", ct);
                if (!vmCheck.Success || string.IsNullOrWhiteSpace(vmCheck.Output))
                {
                    throw new InvalidOperationException(
                        $"VM '{_vmName}' not found. Please create it first:\n" +
                        $"New-VM -Name '{_vmName}' -MemoryStartupBytes 2GB -Generation 2\n" +
                        "Then install Windows and enable PowerShell remoting.");
                }

                // Get VM state
                var stateResult = await ExecuteLocalPowerShellAsync(
                    $"(Get-VM -Name '{_vmName}').State", ct);

                if (stateResult.Output.Trim() != "Running")
                {
                    // Start the VM
                    await ExecuteLocalPowerShellAsync($"Start-VM -Name '{_vmName}'", ct);

                    // Wait for VM to be ready (heartbeat integration service)
                    var deadline = DateTime.UtcNow + _config.StartupTimeout;
                    while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                    {
                        var heartbeat = await ExecuteLocalPowerShellAsync(
                            $"(Get-VMIntegrationService -VMName '{_vmName}' | Where-Object Name -eq 'Heartbeat').PrimaryStatusDescription",
                            ct);

                        if (heartbeat.Output.Trim() == "OK")
                            break;

                        await Task.Delay(2000, ct);
                    }

                    if (ct.IsCancellationRequested)
                    {
                        State = ScreenEnvironmentState.Error;
                        throw new OperationCanceledException();
                    }

                    // Additional delay for Windows to fully boot
                    await Task.Delay(5000, ct);
                }

                // Verify we can execute commands in the VM
                var testResult = await ExecuteInVmAsync("echo 'VM Ready'", ct);
                if (!testResult.Success)
                {
                    throw new InvalidOperationException(
                        "Cannot execute commands in VM. Please ensure:\n" +
                        "1. PowerShell remoting is enabled in the VM\n" +
                        "2. VM Integration Services are installed\n" +
                        "Run in VM: Enable-PSRemoting -Force");
                }

                State = ScreenEnvironmentState.Running;
            }
            catch (Exception)
            {
                State = ScreenEnvironmentState.Error;
                throw;
            }
        }

        public async Task<string> CaptureScreenshotAsync(CancellationToken ct = default)
        {
            EnsureRunning();

            // Capture screenshot inside VM using PowerShell
            var script = @"
Add-Type -AssemblyName System.Windows.Forms
$screen = [System.Windows.Forms.Screen]::PrimaryScreen
$bitmap = New-Object System.Drawing.Bitmap($screen.Bounds.Width, $screen.Bounds.Height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen($screen.Bounds.Location, [System.Drawing.Point]::Empty, $screen.Bounds.Size)
$ms = New-Object System.IO.MemoryStream
$bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
[Convert]::ToBase64String($ms.ToArray())
$graphics.Dispose()
$bitmap.Dispose()
$ms.Dispose()
";

            var result = await ExecuteInVmAsync(script, ct);
            if (!result.Success)
            {
                throw new InvalidOperationException($"Failed to capture screenshot: {result.Output}");
            }

            return result.Output.Trim();
        }

        public async Task ClickAsync(int x, int y, CancellationToken ct = default)
        {
            EnsureRunning();

            var script = $@"
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class MouseOps {{
    [DllImport(""user32.dll"")]
    public static extern bool SetCursorPos(int X, int Y);
    [DllImport(""user32.dll"")]
    public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, int dwExtraInfo);
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
}}
'@
[MouseOps]::SetCursorPos({x}, {y})
Start-Sleep -Milliseconds 50
[MouseOps]::mouse_event([MouseOps]::MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0)
[MouseOps]::mouse_event([MouseOps]::MOUSEEVENTF_LEFTUP, 0, 0, 0, 0)
";

            await ExecuteInVmAsync(script, ct);
        }

        public async Task DoubleClickAsync(int x, int y, CancellationToken ct = default)
        {
            await ClickAsync(x, y, ct);
            await Task.Delay(50, ct);
            await ClickAsync(x, y, ct);
        }

        public async Task RightClickAsync(int x, int y, CancellationToken ct = default)
        {
            EnsureRunning();

            var script = $@"
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class MouseOps {{
    [DllImport(""user32.dll"")]
    public static extern bool SetCursorPos(int X, int Y);
    [DllImport(""user32.dll"")]
    public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, int dwExtraInfo);
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
}}
'@
[MouseOps]::SetCursorPos({x}, {y})
Start-Sleep -Milliseconds 50
[MouseOps]::mouse_event([MouseOps]::MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, 0)
[MouseOps]::mouse_event([MouseOps]::MOUSEEVENTF_RIGHTUP, 0, 0, 0, 0)
";

            await ExecuteInVmAsync(script, ct);
        }

        public async Task TypeTextAsync(string text, CancellationToken ct = default)
        {
            EnsureRunning();

            // Escape special characters for PowerShell
            var escapedText = text.Replace("'", "''").Replace("`", "``");

            var script = $@"
Add-Type -AssemblyName System.Windows.Forms
[System.Windows.Forms.SendKeys]::SendWait('{escapedText}')
";

            await ExecuteInVmAsync(script, ct);
        }

        public async Task SendKeysAsync(string keys, CancellationToken ct = default)
        {
            EnsureRunning();

            // Convert key names to SendKeys format
            var sendKeysFormat = ConvertToSendKeysFormat(keys);

            var script = $@"
Add-Type -AssemblyName System.Windows.Forms
[System.Windows.Forms.SendKeys]::SendWait('{sendKeysFormat}')
";

            await ExecuteInVmAsync(script, ct);
        }

        public async Task ScrollAsync(int x, int y, int deltaY, CancellationToken ct = default)
        {
            EnsureRunning();

            var script = $@"
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class MouseOps {{
    [DllImport(""user32.dll"")]
    public static extern bool SetCursorPos(int X, int Y);
    [DllImport(""user32.dll"")]
    public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, int dwExtraInfo);
    public const uint MOUSEEVENTF_WHEEL = 0x0800;
}}
'@
[MouseOps]::SetCursorPos({x}, {y})
[MouseOps]::mouse_event([MouseOps]::MOUSEEVENTF_WHEEL, 0, 0, {deltaY * 120}, 0)
";

            await ExecuteInVmAsync(script, ct);
        }

        public async Task<string> GetActiveWindowTitleAsync(CancellationToken ct = default)
        {
            EnsureRunning();

            var script = @"
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public class WinAPI {
    [DllImport(""user32.dll"")]
    public static extern IntPtr GetForegroundWindow();
    [DllImport(""user32.dll"", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
}
'@
$hwnd = [WinAPI]::GetForegroundWindow()
$sb = New-Object System.Text.StringBuilder(256)
[void][WinAPI]::GetWindowText($hwnd, $sb, 256)
$sb.ToString()
";

            var result = await ExecuteInVmAsync(script, ct);
            return result.Output.Trim();
        }

        public async Task<bool> LaunchApplicationAsync(string path, string? arguments = null, CancellationToken ct = default)
        {
            EnsureRunning();

            var args = string.IsNullOrEmpty(arguments) ? "" : $"-ArgumentList '{arguments}'";
            var script = $"Start-Process -FilePath '{path}' {args}";

            var result = await ExecuteInVmAsync(script, ct);
            return result.Success;
        }

        public async Task<(bool Success, string Output)> ExecutePowerShellAsync(string command, CancellationToken ct = default)
        {
            EnsureRunning();
            return await ExecuteInVmAsync(command, ct);
        }

        public async Task CopyFileToEnvironmentAsync(string sourcePath, string destPath, CancellationToken ct = default)
        {
            EnsureRunning();

            // Use Copy-VMFile cmdlet
            var result = await ExecuteLocalPowerShellAsync(
                $"Copy-VMFile -Name '{_vmName}' -SourcePath '{sourcePath}' -DestinationPath '{destPath}' -FileSource Host -Force",
                ct);

            if (!result.Success)
            {
                throw new IOException($"Failed to copy file to VM: {result.Output}");
            }
        }

        public async Task CopyFileFromEnvironmentAsync(string sourcePath, string destPath, CancellationToken ct = default)
        {
            EnsureRunning();

            // Read file content in VM and save locally
            var script = $"[Convert]::ToBase64String([System.IO.File]::ReadAllBytes('{sourcePath}'))";
            var result = await ExecuteInVmAsync(script, ct);

            if (!result.Success)
            {
                throw new IOException($"Failed to read file from VM: {result.Output}");
            }

            var bytes = Convert.FromBase64String(result.Output.Trim());
            await File.WriteAllBytesAsync(destPath, bytes, ct);
        }

        public async Task ShutdownAsync(CancellationToken ct = default)
        {
            if (State != ScreenEnvironmentState.Running) return;

            State = ScreenEnvironmentState.Stopping;

            try
            {
                // Graceful shutdown
                await ExecuteLocalPowerShellAsync($"Stop-VM -Name '{_vmName}' -Force", ct);
                State = ScreenEnvironmentState.Stopped;
            }
            catch
            {
                State = ScreenEnvironmentState.Error;
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (State == ScreenEnvironmentState.Running)
            {
                try
                {
                    await ShutdownAsync();
                }
                catch
                {
                    // Ignore shutdown errors during dispose
                }
            }
        }

        private void EnsureRunning()
        {
            if (State != ScreenEnvironmentState.Running)
            {
                throw new InvalidOperationException($"VM is not running. Current state: {State}");
            }
        }

        private async Task<(bool Success, string Output)> ExecuteInVmAsync(string command, CancellationToken ct)
        {
            // Use Invoke-Command with VMName to execute in VM
            var escapedCommand = command.Replace("\"", "`\"").Replace("$", "`$");

            var psCommand = $@"
Invoke-Command -VMName '{_vmName}' -ScriptBlock {{
    {command}
}} 2>&1
";

            return await ExecuteLocalPowerShellAsync(psCommand, ct);
        }

        private async Task<(bool Success, string Output)> ExecuteLocalPowerShellAsync(string command, CancellationToken ct)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    return (false, "Failed to start PowerShell");
                }

                var output = await process.StandardOutput.ReadToEndAsync(ct);
                var error = await process.StandardError.ReadToEndAsync(ct);

                await process.WaitForExitAsync(ct);

                if (process.ExitCode != 0 && !string.IsNullOrEmpty(error))
                {
                    return (false, error);
                }

                return (true, output);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        private static string ConvertToSendKeysFormat(string keys)
        {
            // Convert common key names to SendKeys format
            return keys.ToUpper() switch
            {
                "ENTER" => "{ENTER}",
                "TAB" => "{TAB}",
                "ESC" => "{ESC}",
                "ESCAPE" => "{ESC}",
                "BACKSPACE" => "{BACKSPACE}",
                "DELETE" => "{DELETE}",
                "HOME" => "{HOME}",
                "END" => "{END}",
                "PAGEUP" => "{PGUP}",
                "PAGEDOWN" => "{PGDN}",
                "UP" => "{UP}",
                "DOWN" => "{DOWN}",
                "LEFT" => "{LEFT}",
                "RIGHT" => "{RIGHT}",
                "F1" => "{F1}",
                "F2" => "{F2}",
                "F3" => "{F3}",
                "F4" => "{F4}",
                "F5" => "{F5}",
                "F6" => "{F6}",
                "F7" => "{F7}",
                "F8" => "{F8}",
                "F9" => "{F9}",
                "F10" => "{F10}",
                "F11" => "{F11}",
                "F12" => "{F12}",
                var k when k.StartsWith("CTRL+") => $"^{k[5..].ToLower()}",
                var k when k.StartsWith("ALT+") => $"%{k[4..].ToLower()}",
                var k when k.StartsWith("SHIFT+") => $"+{k[6..].ToLower()}",
                var k when k.StartsWith("WIN+") => $"^{{ESC}}{k[4..].ToLower()}", // Workaround
                _ => keys
            };
        }
    }
}
