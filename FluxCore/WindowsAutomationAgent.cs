using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Automation;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Text;

namespace FluxCore
{
    /// <summary>
    /// Windows Automation Agent: Interacts with desktop UI elements using UIAutomation.
    /// </summary>
    public partial class WindowsAutomationAgent
    {
        // =========================================
        // WINDOWS API
        // =========================================
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, int dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern short VkKeyScan(char ch);

        [DllImport("user32.dll")]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        // ── Robust foreground activation (defeats focus-stealing prevention) ──
        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int dwProcessId);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref uint pvParam, uint fWinIni);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, UIntPtr pvParam, uint fWinIni);
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")]
        private static extern bool IsHungAppWindow(IntPtr hwnd);

        private const uint SPI_GETFOREGROUNDLOCKTIMEOUT = 0x2000;
        private const uint SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001;
        private const uint SPIF_SENDWININICHANGE = 0x0002;
        private const int  ASFW_ANY = -1;

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        // Window commands
        private const int SW_MINIMIZE = 6;
        private const int SW_MAXIMIZE = 3;
        private const int SW_RESTORE = 9;
        private const uint WM_CLOSE = 0x0010;

        // Track the target window (not Flux)
        private IntPtr _targetWindow = IntPtr.Zero;
        private string _targetWindowTitle = "";

        // LOCKED target - doesn't change during a task
        private IntPtr _lockedTargetWindow = IntPtr.Zero;
        private bool _useLockedTarget = false;

        // DPI scaling factor
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        private const uint MONITOR_DEFAULTTONEAREST = 2;

        // =========================================
        // TARGET WINDOW MANAGEMENT
        // =========================================

        /// <summary>
        /// Captures the current foreground window as the target (call this before Flux takes focus).
        /// </summary>
        public void CaptureTargetWindow()
        {
            _targetWindow = GetForegroundWindow();
            _targetWindowTitle = GetWindowTitle(_targetWindow);
            System.Diagnostics.Debug.WriteLine($"[Automation] Captured target: {_targetWindowTitle}");
        }

        /// <summary>
        /// LOCKS the target window for the duration of a task.
        /// Call this at task start to prevent window switching issues.
        /// </summary>
        public void SetLockedTarget(IntPtr hwnd)
        {
            _lockedTargetWindow = hwnd;
            _useLockedTarget = hwnd != IntPtr.Zero;
            if (_useLockedTarget)
            {
                _targetWindow = hwnd;
                _targetWindowTitle = GetWindowTitle(hwnd);
                System.Diagnostics.Debug.WriteLine($"[Automation] LOCKED target: {_targetWindowTitle}");
            }
        }

        /// <summary>
        /// Unlocks the target window (call at task end).
        /// </summary>
        public void UnlockTarget()
        {
            _lockedTargetWindow = IntPtr.Zero;
            _useLockedTarget = false;
            System.Diagnostics.Debug.WriteLine($"[Automation] Target UNLOCKED");
        }

        /// <summary>
        /// Gets the process name of the active window.
        /// </summary>
        public string GetActiveProcessName()
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return "";

            GetWindowThreadProcessId(hwnd, out uint pid);
            try
            {
                return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
            }
            catch { return ""; }
        }

        /// <summary>
        /// Checks if the current target is a console window.
        /// </summary>
        private bool IsConsoleWindow()
        {
            string procName = GetActiveProcessName().ToLower();
            return procName.Contains("cmd") ||
                   procName.Contains("powershell") ||
                   procName.Contains("windowsterminal") ||
                   procName.Contains("conhost") ||
                   procName.Contains("pwsh");
        }

        /// <summary>
        /// Gets DPI scale factor for the current monitor.
        /// </summary>
        private float GetDpiScale()
        {
            try
            {
                IntPtr monitor = MonitorFromWindow(GetForegroundWindow(), MONITOR_DEFAULTTONEAREST);
                GetDpiForMonitor(monitor, 0, out uint dpiX, out uint _);
                return dpiX / 96f; // 96 is standard DPI
            }
            catch
            {
                return 1.0f; // Fallback to no scaling
            }
        }

        /// <summary>
        /// Finds a window by partial title and sets it as target.
        /// </summary>
        public bool SetTargetWindowByTitle(string partialTitle)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                string title = GetWindowTitle(hWnd);
                if (title.Contains(partialTitle, StringComparison.OrdinalIgnoreCase))
                {
                    // Skip Flux itself
                    if (!title.Contains("FLUX", StringComparison.OrdinalIgnoreCase))
                    {
                        found = hWnd;
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);

            if (found != IntPtr.Zero)
            {
                _targetWindow = found;
                _targetWindowTitle = GetWindowTitle(found);
                return true;
            }
            return false;
        }

        private string GetWindowTitle(IntPtr hWnd)
        {
            var sb = new StringBuilder(256);
            GetWindowText(hWnd, sb, 256);
            return sb.ToString();
        }

        private string GetActiveWindowTitle()
        {
            return GetWindowTitle(GetForegroundWindow());
        }

        private AutomationElement? GetTargetRoot()
        {
            // If we have a locked target, USE IT - don't search for another window
            if (_useLockedTarget && _lockedTargetWindow != IntPtr.Zero)
            {
                _targetWindow = _lockedTargetWindow;
                _targetWindowTitle = GetWindowTitle(_lockedTargetWindow);
                System.Diagnostics.Debug.WriteLine($"[Automation] Using LOCKED target: {_targetWindowTitle}");

                try
                {
                    return AutomationElement.FromHandle(_lockedTargetWindow);
                }
                catch
                {
                    return null;
                }
            }

            // Only refresh if not locked
            _targetWindow = IntPtr.Zero;

            var candidates = new List<(IntPtr hWnd, string title)>();

            EnumWindows((hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                string title = GetWindowTitle(hWnd);
                if (!string.IsNullOrEmpty(title) && title.Length > 1)
                {
                    candidates.Add((hWnd, title));
                }
                return true;
            }, IntPtr.Zero);

            // Filter out known overlays and our own window
            var validWindows = candidates.Where(c =>
                !c.title.Contains("Davos", StringComparison.OrdinalIgnoreCase) &&
                !c.title.Contains("Program Manager") &&
                !c.title.Contains("NVIDIA") &&
                !c.title.Contains("Default IME") &&
                !c.title.Contains("Microsoft Text Input") &&
                c.title != "Settings").ToList();

            // Pick the top-most valid window (EnumWindows returns in Z-order)
            if (validWindows.Count > 0)
            {
                var top = validWindows[0];
                _targetWindow = top.hWnd;
                _targetWindowTitle = top.title;
            }

            System.Diagnostics.Debug.WriteLine($"[Automation] Target: {_targetWindowTitle} (from {validWindows.Count} candidates)");

            if (_targetWindow == IntPtr.Zero) return null;

            try
            {
                return AutomationElement.FromHandle(_targetWindow);
            }
            catch
            {
                return null;
            }
        }

        // =========================================
        // FIND UI ELEMENTS
        // =========================================

        public List<UIElementInfo> FindClickableElements()
        {
            var results = new List<UIElementInfo>();
            try
            {
                var rootElement = GetTargetRoot();
                if (rootElement == null) return results;

                var controlTypes = new[] {
                    ControlType.Button, ControlType.Hyperlink, ControlType.CheckBox, ControlType.MenuItem,
                    ControlType.ListItem, ControlType.TreeItem, ControlType.TabItem,
                    ControlType.Pane, ControlType.Custom, ControlType.Text
                };
                foreach (var ct in controlTypes)
                {
                    var elements = rootElement.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ct));
                    foreach (AutomationElement el in elements)
                    {
                        if (!string.IsNullOrWhiteSpace(el.Current.Name))
                            AddElementInfo(results, el, ct.ProgrammaticName.Replace("ControlType.", ""));
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WindowsAutomation] Find error: {ex.Message}");
            }
            return results;
        }

        public List<UIElementInfo> FindTextFields()
        {
            var results = new List<UIElementInfo>();
            try
            {
                var rootElement = GetTargetRoot();
                if (rootElement == null) return results;

                var controlTypes = new[] { ControlType.Edit, ControlType.Document };
                foreach (var ct in controlTypes)
                {
                    var elements = rootElement.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ct));
                    foreach (AutomationElement el in elements)
                    {
                        AddElementInfo(results, el, ct.ProgrammaticName.Replace("ControlType.", ""));
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WindowsAutomation] Find text error: {ex.Message}");
            }
            return results;
        }

        private void AddElementInfo(List<UIElementInfo> list, AutomationElement element, string type)
        {
            try
            {
                var rect = element.Current.BoundingRectangle;
                if (rect.IsEmpty || rect.Width < 5 || rect.Height < 5) return;

                list.Add(new UIElementInfo
                {
                    Name = element.Current.Name ?? "",
                    Type = type,
                    AutomationId = element.Current.AutomationId ?? "",
                    X = (int)rect.X,
                    Y = (int)rect.Y,
                    Width = (int)rect.Width,
                    Height = (int)rect.Height,
                    Element = element
                });
            }
            catch { }
        }

        // =========================================
        // ACTIONS
        // =========================================

        /// <summary>
        /// Waits for an element to be visible and enabled.
        /// </summary>
        private async Task<UIElementInfo?> WaitForElementAsync(string nameOrId, int timeoutMs = 2000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                var clickables = FindClickableElements();
                var target = clickables.FirstOrDefault(e =>
                    e.Name.Contains(nameOrId, StringComparison.OrdinalIgnoreCase) ||
                    e.AutomationId.Contains(nameOrId, StringComparison.OrdinalIgnoreCase));

                if (target != null)
                {
                    if (target.X > 0 && target.Y > 0 && target.Width > 0 && target.Height > 0)
                        return target;
                }

                await Task.Delay(100);
            }
            return null;
        }

        /// <summary>
        /// Ensures the target window is actually frontmost before we click/type into it.
        /// Returns true if the target IS foreground at the end — callers can stop guessing.
        /// </summary>
        public async Task<bool> EnsureFocusAsync(IntPtr targetWindow)
        {
            if (targetWindow == IntPtr.Zero) return false;
            if (GetForegroundWindow() == targetWindow) return true;

            // Robust force-foreground, a few tries with short settles (overlays like Overwolf/
            // NVIDIA fight back for a moment after launch).
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (ForceForegroundWindow(targetWindow)) return true;
                await Task.Delay(80);
            }

            // Last resort: minimize/restore bounce, which kicks even stubborn windows, then force again.
            ShowWindow(targetWindow, SW_MINIMIZE);
            await Task.Delay(40);
            ShowWindow(targetWindow, SW_RESTORE);
            bool ok = ForceForegroundWindow(targetWindow);
            await Task.Delay(80);

            System.Diagnostics.Debug.WriteLine($"[Automation] Focus result={ok || GetForegroundWindow() == targetWindow}. Current: {GetWindowTitle(GetForegroundWindow())}");
            return GetForegroundWindow() == targetWindow;
        }

        /// <summary>Public entry: force any window to the foreground. Used by OPEN_APP and the
        /// task loop so "I opened X" is only reported when X is actually in front.</summary>
        public bool ForceFocus(IntPtr hWnd) => ForceForegroundWindow(hWnd);

        // ══════════════════════════════════════════════════════════════════════════════
        // CHEAP STATE LAYER — ground truth from Win32, NO screenshot, NO LLM (microseconds).
        // The agent should KNOW the real window state, not assume it. Used to gate actions
        // (wait until ready) instead of firing them on a fixed-time guess.
        // ══════════════════════════════════════════════════════════════════════════════

        /// <summary>Is this window alive and responding (not hung)? Cheap.</summary>
        public bool IsResponsive(IntPtr hWnd) => hWnd != IntPtr.Zero && !IsHungAppWindow(hWnd);

        /// <summary>Ground truth the model/harness can read for free: what's focused right now,
        /// and is it responding. Replaces "I assume X is focused".</summary>
        public (IntPtr hwnd, string title, bool responsive) GetForegroundState()
        {
            IntPtr fg = GetForegroundWindow();
            return (fg, GetWindowTitle(fg), fg != IntPtr.Zero && !IsHungAppWindow(fg));
        }

        /// <summary>
        /// Event-driven readiness wait: polls cheap Win32 state until <paramref name="target"/>
        /// is ACTUALLY the foreground window AND responsive, re-forcing focus each tick (overlays
        /// fight back for a beat after launch). Returns the instant it's ready — fast for light
        /// apps, patient for heavy ones — or false on timeout. This is the replacement for fixed
        /// sleeps and "assume it opened": never waits longer than reality needs, never less.
        /// </summary>
        public async Task<bool> WaitUntilForegroundReadyAsync(IntPtr target, int timeoutMs = 3000, int pollMs = 60)
        {
            if (target == IntPtr.Zero) return false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (GetForegroundWindow() == target && !IsHungAppWindow(target))
                    return true;
                ForceForegroundWindow(target);
                await Task.Delay(pollMs);
            }
            return GetForegroundWindow() == target && !IsHungAppWindow(target);
        }

        /// <summary>
        /// Forces an arbitrary window to the foreground, defeating Windows' focus-stealing
        /// prevention. SetForegroundWindow ALONE is silently ignored when another process owns
        /// the foreground (overlays, or the lock timeout) — that's why Telegram/Steam stayed
        /// behind Steam/Overwolf/PWGood and Davos typed blind. Proven recipe (verified across
        /// machines): drop SPI_FOREGROUNDLOCKTIMEOUT to 0, AllowSetForegroundWindow(ANY), then
        /// AttachThreadInput our thread to BOTH the current-foreground thread AND the target
        /// thread so Windows treats us as "related", then ShowWindow + BringWindowToTop +
        /// SetForegroundWindow. ALWAYS detaches in finally — a leaked AttachThreadInput hangs us
        /// if the target ever stops responding. Returns true only if the target really is foreground.
        /// </summary>
        private bool ForceForegroundWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return false;
            if (GetForegroundWindow() == hWnd) return true;

            uint appThread = GetCurrentThreadId();
            uint foreThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            uint targetThread = GetWindowThreadProcessId(hWnd, out _);

            uint oldTimeout = 0;
            try { SystemParametersInfo(SPI_GETFOREGROUNDLOCKTIMEOUT, 0, ref oldTimeout, 0); } catch { }
            try { SystemParametersInfo(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, UIntPtr.Zero, SPIF_SENDWININICHANGE); } catch { }
            try { AllowSetForegroundWindow(ASFW_ANY); } catch { }

            bool attachedFore = false, attachedTarget = false;
            try
            {
                if (foreThread != 0 && foreThread != appThread)
                    attachedFore = AttachThreadInput(appThread, foreThread, true);
                if (targetThread != 0 && targetThread != appThread && targetThread != foreThread)
                    attachedTarget = AttachThreadInput(appThread, targetThread, true);

                ShowWindow(hWnd, SW_RESTORE);
                BringWindowToTop(hWnd);
                SetForegroundWindow(hWnd);
            }
            finally
            {
                if (attachedFore) AttachThreadInput(appThread, foreThread, false);
                if (attachedTarget) AttachThreadInput(appThread, targetThread, false);
                try { SystemParametersInfo(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, (UIntPtr)oldTimeout, SPIF_SENDWININICHANGE); } catch { }
            }

            return GetForegroundWindow() == hWnd;
        }

        /// <summary>
        /// Controls windows: minimize, maximize, restore, close.
        /// </summary>
        public async Task<AutomationResult> WindowControlAsync(string action)
        {
            try
            {
                GetTargetRoot();
                if (_targetWindow == IntPtr.Zero)
                    return new AutomationResult(false, "No target window found");

                string actionLower = action.ToLower();

                if (actionLower.Contains("min"))
                {
                    ShowWindow(_targetWindow, SW_MINIMIZE);
                    return new AutomationResult(true, $"Minimized: {_targetWindowTitle}");
                }
                else if (actionLower.Contains("max"))
                {
                    ShowWindow(_targetWindow, SW_MAXIMIZE);
                    return new AutomationResult(true, $"Maximized: {_targetWindowTitle}");
                }
                else if (actionLower.Contains("restore"))
                {
                    ShowWindow(_targetWindow, SW_RESTORE);
                    return new AutomationResult(true, $"Restored: {_targetWindowTitle}");
                }
                else if (actionLower.Contains("close"))
                {
                    PostMessage(_targetWindow, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    return new AutomationResult(true, $"Closed: {_targetWindowTitle}");
                }

                return new AutomationResult(false, $"Unknown action: {action}. Use minimize/maximize/restore/close.");
            }
            catch (Exception ex)
            {
                return new AutomationResult(false, $"Window control failed: {ex.Message}");
            }
        }

        public void MinimizeFlux()
        {
            if (System.Windows.Application.Current != null)
            {
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (System.Windows.Application.Current.MainWindow is MainWindow mw)
                        mw.ForceHide();
                    else if (System.Windows.Application.Current.MainWindow != null)
                        System.Windows.Application.Current.MainWindow.WindowState = System.Windows.WindowState.Minimized;
                });
            }
        }

        public string GetUIContext()
        {
            var clickables = FindClickableElements();
            var textFields = FindTextFields();

            var summary = new StringBuilder();
            summary.AppendLine($"[TARGET: {_targetWindowTitle}]");
            summary.AppendLine($"Buttons/Links: {clickables.Count}");

            foreach (var el in clickables.Take(10))
            {
                if (!string.IsNullOrEmpty(el.Name))
                    summary.AppendLine($"  - [{el.Type}] \"{el.Name}\"");
            }

            summary.AppendLine($"Text Fields: {textFields.Count}");
            foreach (var el in textFields.Take(5))
            {
                summary.AppendLine($"  - [{el.Type}] \"{el.Name}\" (id: {el.AutomationId})");
            }

            return summary.ToString();
        }
    }
}
