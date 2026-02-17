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
    public class WindowsAutomationAgent
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
                !c.title.Contains("FLUX", StringComparison.OrdinalIgnoreCase) &&
                !c.title.Contains("Flux ai", StringComparison.OrdinalIgnoreCase) &&
                !c.title.Contains("Fluxoria", StringComparison.OrdinalIgnoreCase) &&
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
                        // Filter out empty unnamed elements to reduce noise, unless it's a Button/Input
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

        // EnsureFocusAsync is defined below as a public method with multiple strategies

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
                    // Verify element is in valid position (not off-screen)
                    if (target.X > 0 && target.Y > 0 && target.Width > 0 && target.Height > 0)
                        return target;
                }

                await Task.Delay(100);
            }
            return null;
        }

        /// <summary>
        /// Performs a reliable click with retry and multiple strategies.
        /// </summary>
        public async Task<AutomationResult> ClickWithRetryAsync(string nameOrId, int maxRetries = 3)
        {
            AutomationResult? lastResult = null;

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                // Strategy changes based on attempt number
                switch (attempt)
                {
                    case 0:
                        // First try: normal click
                        lastResult = await ClickElementInternalAsync(nameOrId, usePhysicalClick: false);
                        break;
                    case 1:
                        // Second try: physical click with longer delays
                        await Task.Delay(200);
                        lastResult = await ClickElementInternalAsync(nameOrId, usePhysicalClick: true);
                        break;
                    case 2:
                        // Third try: scroll to reveal element, then click
                        await ScrollAsync("down");
                        await Task.Delay(150);
                        lastResult = await ClickElementInternalAsync(nameOrId, usePhysicalClick: true);
                        break;
                }

                if (lastResult?.Success == true)
                    return lastResult;

                // Backoff delay between retries
                await Task.Delay(100 * (attempt + 1));
            }

            return lastResult ?? new AutomationResult(false, $"Click failed after {maxRetries} attempts");
        }

        /// <summary>
        /// Internal click implementation with strategy options.
        /// </summary>
        private async Task<AutomationResult> ClickElementInternalAsync(string nameOrId, bool usePhysicalClick)
        {
            try
            {
                // Ensure target window is focused first
                IntPtr targetWindow = _useLockedTarget && _lockedTargetWindow != IntPtr.Zero
                    ? _lockedTargetWindow
                    : _targetWindow;

                if (targetWindow != IntPtr.Zero)
                {
                    await EnsureFocusAsync(targetWindow);
                }

                // Check if coordinate: "100,200"
                var coordMatch = System.Text.RegularExpressions.Regex.Match(nameOrId, @"(\d+)[, ]+(\d+)");
                if (coordMatch.Success)
                {
                    int x = int.Parse(coordMatch.Groups[1].Value);
                    int y = int.Parse(coordMatch.Groups[2].Value);

                    SetCursorPos(x, y);
                    await Task.Delay(60);
                    mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
                    await Task.Delay(40);
                    mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);

                    return new AutomationResult(true, $"Clicked at {x},{y}");
                }

                // Wait for element to appear
                var target = await WaitForElementAsync(nameOrId, timeoutMs: 1500);

                // Fallback: check desktop icons
                if (target == null)
                {
                    var desktopElements = FindDesktopIcons();
                    target = desktopElements.FirstOrDefault(e =>
                        e.Name.Contains(nameOrId, StringComparison.OrdinalIgnoreCase));
                }

                if (target == null)
                {
                    var clickables = FindClickableElements();
                    string available = clickables.Count > 0
                        ? string.Join(", ", clickables.Take(5).Select(e => $"'{e.Name}'"))
                        : "none";
                    return new AutomationResult(false, $"Element not found: '{nameOrId}'. Available: {available}");
                }

                // Bring target window to front
                if (_targetWindow != IntPtr.Zero)
                {
                    SetForegroundWindow(_targetWindow);
                    await Task.Delay(50);
                }

                // Try InvokePattern first (unless forced physical click)
                if (!usePhysicalClick && target.Element != null &&
                    target.Element.TryGetCurrentPattern(InvokePattern.Pattern, out object? pattern))
                {
                    ((InvokePattern)pattern).Invoke();
                    return new AutomationResult(true, $"Clicked: {target.Name}");
                }

                // Physical mouse click
                int centerX = target.X + target.Width / 2;
                int centerY = target.Y + target.Height / 2;

                SetCursorPos(centerX, centerY);
                await Task.Delay(50);
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
                await Task.Delay(40);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);

                return new AutomationResult(true, $"Clicked: {target.Name} at ({centerX},{centerY})");
            }
            catch (Exception ex)
            {
                return new AutomationResult(false, $"Click failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Main click entry point - uses retry logic for reliability.
        /// </summary>
        public async Task<AutomationResult> ClickElementAsync(string nameOrId)
        {
            // Use the retry version for better reliability
            return await ClickWithRetryAsync(nameOrId, maxRetries: 3);
        }
        
        /// <summary>
        /// Finds desktop icons by accessing Program Manager -> SHELLDLL -> Desktop ListView
        /// </summary>
        private List<UIElementInfo> FindDesktopIcons()
        {
            var results = new List<UIElementInfo>();
            try
            {
                // Desktop icons live under "Program Manager" window
                IntPtr progMan = IntPtr.Zero;
                EnumWindows((hWnd, lParam) =>
                {
                    string title = GetWindowTitle(hWnd);
                    if (title == "Program Manager")
                    {
                        progMan = hWnd;
                        return false; // Stop enumeration
                    }
                    return true;
                }, IntPtr.Zero);
                
                if (progMan == IntPtr.Zero) return results;
                
                var desktopRoot = AutomationElement.FromHandle(progMan);
                if (desktopRoot == null) return results;
                
                // Find all ListItems (desktop icons are ListItems)
                var elements = desktopRoot.FindAll(TreeScope.Descendants, 
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
                
                foreach (AutomationElement el in elements)
                {
                    if (!string.IsNullOrWhiteSpace(el.Current.Name))
                    {
                        AddElementInfo(results, el, "DesktopIcon");
                    }
                }
                
                System.Diagnostics.Debug.WriteLine($"[Automation] Found {results.Count} desktop icons");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Automation] Desktop icon search error: {ex.Message}");
            }
            return results;
        }

        /// <summary>
        /// Types text into the target window. Uses different methods for console vs GUI apps.
        /// </summary>
        public async Task<AutomationResult> TypeTextAsync(string fieldHint, string text)
        {
            try
            {
                // Ensure target window is focused first
                IntPtr targetWindow = _useLockedTarget && _lockedTargetWindow != IntPtr.Zero
                    ? _lockedTargetWindow
                    : GetForegroundWindow();

                if (targetWindow == IntPtr.Zero)
                    return new AutomationResult(false, "No target window found.");

                // Focus the window
                await EnsureFocusAsync(targetWindow);

                // Get window info for logging
                StringBuilder sb = new StringBuilder(256);
                GetWindowText(targetWindow, sb, 256);
                string windowTitle = sb.ToString();

                // Detect if this is a console window - use character-by-character for consoles
                if (IsConsoleWindow())
                {
                    System.Diagnostics.Debug.WriteLine($"[Automation] Console detected, using char-by-char typing");
                    return await TypeToConsoleAsync(text, windowTitle);
                }

                // For GUI apps, use clipboard paste (faster and more reliable)
                return await TypeViaClipboardAsync(text, windowTitle);
            }
            catch (Exception ex)
            {
                return new AutomationResult(false, $"Type failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Types text to console windows character by character.
        /// Console windows don't handle Ctrl+V the same way GUI apps do.
        /// </summary>
        private async Task<AutomationResult> TypeToConsoleAsync(string text, string windowTitle)
        {
            try
            {
                foreach (char c in text)
                {
                    // Handle special characters
                    if (c == '\n' || c == '\r')
                    {
                        keybd_event(0x0D, 0, 0, UIntPtr.Zero); // ENTER down
                        keybd_event(0x0D, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // ENTER up
                        await Task.Delay(50);
                        continue;
                    }

                    if (c == '\t')
                    {
                        keybd_event(0x09, 0, 0, UIntPtr.Zero); // TAB down
                        keybd_event(0x09, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // TAB up
                        await Task.Delay(10);
                        continue;
                    }

                    // Get virtual key code for the character
                    short vkResult = VkKeyScan(c);
                    if (vkResult == -1)
                    {
                        // Character not mappable, skip it
                        System.Diagnostics.Debug.WriteLine($"[Automation] Cannot type character: {c}");
                        continue;
                    }

                    byte vk = (byte)(vkResult & 0xFF);
                    bool needShift = (vkResult & 0x100) != 0;
                    bool needCtrl = (vkResult & 0x200) != 0;
                    bool needAlt = (vkResult & 0x400) != 0;

                    // Press modifiers
                    if (needShift) keybd_event(0x10, 0, 0, UIntPtr.Zero);
                    if (needCtrl) keybd_event(0x11, 0, 0, UIntPtr.Zero);
                    if (needAlt) keybd_event(0x12, 0, 0, UIntPtr.Zero);

                    // Press and release the key
                    keybd_event(vk, 0, 0, UIntPtr.Zero);
                    keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

                    // Release modifiers
                    if (needAlt) keybd_event(0x12, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    if (needCtrl) keybd_event(0x11, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    if (needShift) keybd_event(0x10, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

                    await Task.Delay(15); // Small delay between characters
                }

                return new AutomationResult(true, $"Typed to console {windowTitle}: {text}");
            }
            catch (Exception ex)
            {
                return new AutomationResult(false, $"Console type failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Types text via clipboard paste (for GUI apps) with fallback to char-by-char.
        /// </summary>
        private async Task<AutomationResult> TypeViaClipboardAsync(string text, string windowTitle)
        {
            // Try clipboard method first (fastest)
            bool clipboardSuccess = await TryClipboardPasteAsync(text);

            if (clipboardSuccess)
            {
                return new AutomationResult(true, $"Typed in {windowTitle}: {text}");
            }

            // Fallback: character by character (slower but more reliable)
            System.Diagnostics.Debug.WriteLine("[Automation] Clipboard failed, falling back to char-by-char");
            return await TypeCharByCharAsync(text, windowTitle);
        }

        /// <summary>
        /// Attempts to set clipboard and paste. Returns false if clipboard is locked.
        /// </summary>
        private async Task<bool> TryClipboardPasteAsync(string text)
        {
            int maxRetries = 3;

            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    bool clipSet = false;

                    // Try to set clipboard (may fail if locked by another app)
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        try
                        {
                            System.Windows.Clipboard.SetText(text);
                            // Verify it was set correctly
                            clipSet = System.Windows.Clipboard.GetText() == text;
                        }
                        catch
                        {
                            clipSet = false;
                        }
                    });

                    if (!clipSet)
                    {
                        await Task.Delay(50 * (i + 1)); // Backoff
                        continue;
                    }

                    await Task.Delay(30);

                    // Press Ctrl+V
                    keybd_event(0x11, 0, 0, UIntPtr.Zero); // VK_CONTROL down
                    keybd_event(0x56, 0, 0, UIntPtr.Zero); // V down
                    await Task.Delay(30);
                    keybd_event(0x56, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // V up
                    keybd_event(0x11, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // VK_CONTROL up

                    await Task.Delay(30);
                    return true;
                }
                catch
                {
                    await Task.Delay(50 * (i + 1));
                }
            }

            return false;
        }

        /// <summary>
        /// Types text character by character using SendInput (fallback method).
        /// </summary>
        private async Task<AutomationResult> TypeCharByCharAsync(string text, string windowTitle)
        {
            try
            {
                foreach (char c in text)
                {
                    if (c == '\n' || c == '\r')
                    {
                        keybd_event(0x0D, 0, 0, UIntPtr.Zero);
                        keybd_event(0x0D, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                        await Task.Delay(30);
                        continue;
                    }

                    if (c == '\t')
                    {
                        keybd_event(0x09, 0, 0, UIntPtr.Zero);
                        keybd_event(0x09, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                        await Task.Delay(10);
                        continue;
                    }

                    short vkResult = VkKeyScan(c);
                    if (vkResult == -1)
                    {
                        // Skip unmappable characters but log them
                        System.Diagnostics.Debug.WriteLine($"[Automation] Skipping unmappable char: {c} (0x{((int)c):X4})");
                        continue;
                    }

                    byte vk = (byte)(vkResult & 0xFF);
                    bool needShift = (vkResult & 0x100) != 0;
                    bool needCtrl = (vkResult & 0x200) != 0;
                    bool needAlt = (vkResult & 0x400) != 0;

                    if (needShift) keybd_event(0x10, 0, 0, UIntPtr.Zero);
                    if (needCtrl) keybd_event(0x11, 0, 0, UIntPtr.Zero);
                    if (needAlt) keybd_event(0x12, 0, 0, UIntPtr.Zero);

                    keybd_event(vk, 0, 0, UIntPtr.Zero);
                    keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

                    if (needAlt) keybd_event(0x12, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    if (needCtrl) keybd_event(0x11, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    if (needShift) keybd_event(0x10, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

                    await Task.Delay(10); // Faster than console typing
                }

                return new AutomationResult(true, $"Typed (char-by-char) in {windowTitle}: {text}");
            }
            catch (Exception ex)
            {
                return new AutomationResult(false, $"Char-by-char type failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Ensures the target window is focused. Uses multiple strategies if needed.
        /// </summary>
        public async Task EnsureFocusAsync(IntPtr targetWindow)
        {
            if (targetWindow == IntPtr.Zero) return;

            IntPtr current = GetForegroundWindow();
            if (current == targetWindow) return; // Already focused

            // Strategy 1: Simple SetForegroundWindow
            SetForegroundWindow(targetWindow);
            await Task.Delay(100);

            if (GetForegroundWindow() == targetWindow) return;

            // Strategy 2: Restore if minimized, then focus
            ShowWindow(targetWindow, SW_RESTORE);
            await Task.Delay(50);
            SetForegroundWindow(targetWindow);
            await Task.Delay(100);

            if (GetForegroundWindow() == targetWindow) return;

            // Strategy 3: Alt key trick - Windows allows SetForegroundWindow after Alt press
            keybd_event(0x12, 0, 0, UIntPtr.Zero); // Alt down
            SetForegroundWindow(targetWindow);
            keybd_event(0x12, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // Alt up
            await Task.Delay(100);

            if (GetForegroundWindow() == targetWindow) return;

            // Strategy 4: Minimize and restore to force attention
            ShowWindow(targetWindow, SW_MINIMIZE);
            await Task.Delay(50);
            ShowWindow(targetWindow, SW_RESTORE);
            SetForegroundWindow(targetWindow);
            await Task.Delay(150);

            System.Diagnostics.Debug.WriteLine($"[Automation] Focus attempt complete. Current: {GetWindowTitle(GetForegroundWindow())}");
        }

        /// <summary>
        /// Sends keyboard shortcuts (CTRL+A, CTRL+V, BACKSPACE, ENTER, etc.)
        /// </summary>
        public async Task<AutomationResult> SendKeysAsync(string keyCombo)
        {
            try
            {
                // Find and focus target window
                GetTargetRoot();
                if (_targetWindow != IntPtr.Zero)
                {
                    SetForegroundWindow(_targetWindow);
                    await Task.Delay(150);
                }
                
                // Parse key combo (e.g., "CTRL+A", "BACKSPACE", "ENTER")
                var parts = keyCombo.ToUpper().Split('+');
                var modifiers = new List<byte>();
                byte mainKey = 0;
                
                foreach (var part in parts)
                {
                    switch (part.Trim())
                    {
                        case "CTRL": modifiers.Add(0x11); break; // VK_CONTROL
                        case "ALT": modifiers.Add(0x12); break; // VK_MENU
                        case "SHIFT": modifiers.Add(0x10); break; // VK_SHIFT
                        case "WIN": case "WINDOWS": case "LWIN": modifiers.Add(0x5B); break; // VK_LWIN (Windows key)
                        case "ENTER": mainKey = 0x0D; break; // VK_RETURN
                        case "BACKSPACE": mainKey = 0x08; break; // VK_BACK
                        case "DELETE": mainKey = 0x2E; break; // VK_DELETE
                        case "TAB": mainKey = 0x09; break; // VK_TAB
                        case "ESCAPE": case "ESC": mainKey = 0x1B; break; // VK_ESCAPE
                        case "HOME": mainKey = 0x24; break;
                        case "END": mainKey = 0x23; break;
                        case "A": mainKey = 0x41; break;
                        case "C": mainKey = 0x43; break;
                        case "D": mainKey = 0x44; break; // For WIN+D
                        case "V": mainKey = 0x56; break;
                        case "X": mainKey = 0x58; break;
                        case "Z": mainKey = 0x5A; break;
                        case "S": mainKey = 0x53; break;
                        default:
                            // Try to get key for single character
                            if (part.Length == 1)
                                mainKey = (byte)VkKeyScan(part[0]);
                            break;
                    }
                }
                
                // Press modifiers
                foreach (var mod in modifiers)
                    keybd_event(mod, 0, 0, UIntPtr.Zero);
                
                // Press main key
                if (mainKey != 0)
                {
                    keybd_event(mainKey, 0, 0, UIntPtr.Zero);
                    keybd_event(mainKey, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                }
                
                // Release modifiers (reverse order)
                modifiers.Reverse();
                foreach (var mod in modifiers)
                    keybd_event(mod, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                
                await Task.Delay(50);
                
                return new AutomationResult(true, $"Sent keys: {keyCombo}");
            }
            catch (Exception ex)
            {
                return new AutomationResult(false, $"SendKeys failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Scrolls up or down.
        /// </summary>
        public async Task<AutomationResult> ScrollAsync(string direction)
        {
            try
            {
                GetTargetRoot();
                if (_targetWindow != IntPtr.Zero)
                {
                    SetForegroundWindow(_targetWindow);
                    await Task.Delay(100);
                }
                
                // Scroll amount (120 = one notch, positive = up, negative = down)
                int delta = direction.ToLower().Contains("up") ? 120 * 3 : -120 * 3;
                
                mouse_event(MOUSEEVENTF_WHEEL, 0, 0, (uint)delta, 0);
                
                return new AutomationResult(true, $"Scrolled {direction}");
            }
            catch (Exception ex)
            {
                return new AutomationResult(false, $"Scroll failed: {ex.Message}");
            }
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
                        System.Windows.Application.Current.MainWindow.WindowState = System.Windows.WindowState.Minimized; // Fallback
                });
            }
        }

        /// <summary>
        /// Drags from Start(x,y) to End(x,y).
        /// </summary>
        public async Task<AutomationResult> DragDropAsync(string coords)
        {
            try
            {
                // Format: "x1,y1,x2,y2"
                var parts = coords.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4) return new AutomationResult(false, "Invalid coords. Use: x1,y1,x2,y2");

                int x1 = int.Parse(parts[0]);
                int y1 = int.Parse(parts[1]);
                int x2 = int.Parse(parts[2]);
                int y2 = int.Parse(parts[3]);

                // 1. Move to Start
                SetCursorPos(x1, y1);
                await Task.Delay(100);

                // 2. Press Left Down
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
                await Task.Delay(100);

                // 3. Drag smoothly
                int steps = 10;
                for (int i = 1; i <= steps; i++)
                {
                    int cx = x1 + (x2 - x1) * i / steps;
                    int cy = y1 + (y2 - y1) * i / steps;
                    SetCursorPos(cx, cy);
                    await Task.Delay(20);
                }

                // 4. Release
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
                await Task.Delay(100);

                return new AutomationResult(true, $"Dragged from {x1},{y1} to {x2},{y2}");
            }
            catch (Exception ex)
            {
                return new AutomationResult(false, $"Drag failed: {ex.Message}");
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

    public class UIElementInfo
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public string AutomationId { get; set; } = "";
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public AutomationElement? Element { get; set; }
    }

    public class AutomationResult
    {
        public bool Success { get; }
        public string Message { get; }

        public AutomationResult(bool success, string message)
        {
            Success = success;
            Message = message;
        }
    }
}
