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
        /// Ensures the target window is focused. Uses multiple strategies if needed.
        /// </summary>
        public async Task EnsureFocusAsync(IntPtr targetWindow)
        {
            if (targetWindow == IntPtr.Zero) return;

            IntPtr current = GetForegroundWindow();
            if (current == targetWindow) return;

            SetForegroundWindow(targetWindow);
            await Task.Delay(100);
            if (GetForegroundWindow() == targetWindow) return;

            ShowWindow(targetWindow, SW_RESTORE);
            await Task.Delay(50);
            SetForegroundWindow(targetWindow);
            await Task.Delay(100);
            if (GetForegroundWindow() == targetWindow) return;

            keybd_event(0x12, 0, 0, UIntPtr.Zero);
            SetForegroundWindow(targetWindow);
            keybd_event(0x12, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            await Task.Delay(100);
            if (GetForegroundWindow() == targetWindow) return;

            ShowWindow(targetWindow, SW_MINIMIZE);
            await Task.Delay(50);
            ShowWindow(targetWindow, SW_RESTORE);
            SetForegroundWindow(targetWindow);
            await Task.Delay(150);

            System.Diagnostics.Debug.WriteLine($"[Automation] Focus attempt complete. Current: {GetWindowTitle(GetForegroundWindow())}");
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
