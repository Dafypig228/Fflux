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
            // ALWAYS refresh to find a proper target window (not Flux)
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
                !c.title.Contains("Program Manager") &&
                !c.title.Contains("NVIDIA") &&
                !c.title.Contains("Default IME") &&
                !c.title.Contains("Microsoft Text Input") &&
                c.title != "Settings").ToList();

            // 3. Pick the top-most valid window (EnumWindows returns in Z-order)
            // This ensures we target Chrome if it's open, or Notepad if it's open.
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
        
        public async Task<AutomationResult> ClickElementAsync(string nameOrId)
        {
            try
            {
                // Check if coordinate: "100,200" or "x:100, y:200"
                var coordMatch = System.Text.RegularExpressions.Regex.Match(nameOrId, @"(\d+)[, ]+(\d+)");
                if (coordMatch.Success)
                {
                    int x = int.Parse(coordMatch.Groups[1].Value);
                    int y = int.Parse(coordMatch.Groups[2].Value);
                    
                    SetCursorPos(x, y);
                    await Task.Delay(50);
                    mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
                    await Task.Delay(50);
                    mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
                    
                    return new AutomationResult(true, $"Clicked at {x},{y}");
                }

                // Try to find target window by name if specified
                if (nameOrId.Contains("Close", StringComparison.OrdinalIgnoreCase) ||
                    nameOrId.Contains("File", StringComparison.OrdinalIgnoreCase))
                {
                    SetTargetWindowByTitle("Notepad");
                }

                var clickables = FindClickableElements();
                var target = clickables.FirstOrDefault(e => 
                    e.Name.Contains(nameOrId, StringComparison.OrdinalIgnoreCase) ||
                    e.AutomationId.Contains(nameOrId, StringComparison.OrdinalIgnoreCase));

                // FALLBACK: Search desktop icons if not found in current window
                if (target == null)
                {
                    var desktopElements = FindDesktopIcons();
                    target = desktopElements.FirstOrDefault(e => 
                        e.Name.Contains(nameOrId, StringComparison.OrdinalIgnoreCase));
                    
                    if (target != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Automation] Found on desktop: {target.Name}");
                    }
                }

                if (target == null)
                {
                    string available = clickables.Count > 0 
                        ? string.Join(", ", clickables.Take(8).Select(e => $"'{e.Name}'"))
                        : "none found";
                    return new AutomationResult(false, $"Element not found: '{nameOrId}'. Target: {_targetWindowTitle}. Available: {available}");
                }

                // Bring target window to front first
                if (_targetWindow != IntPtr.Zero)
                {
                    SetForegroundWindow(_targetWindow);
                    await Task.Delay(100);
                }

                // Try InvokePattern first
                if (target.Element != null && target.Element.TryGetCurrentPattern(InvokePattern.Pattern, out object? pattern))
                {
                    ((InvokePattern)pattern).Invoke();
                    return new AutomationResult(true, $"Clicked: {target.Name} ({target.Type})");
                }

                // Fallback: physical mouse click
                int centerX = target.X + target.Width / 2;
                int centerY = target.Y + target.Height / 2;
                SetCursorPos(centerX, centerY);
                await Task.Delay(50);
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
                
                return new AutomationResult(true, $"Clicked at ({centerX}, {centerY}): {target.Name}");
            }
            catch (Exception ex)
            {
                return new AutomationResult(false, $"Click failed: {ex.Message}");
            }
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
        /// Types text into the target window. Just focuses and sends keyboard input.
        /// </summary>
        public async Task<AutomationResult> TypeTextAsync(string fieldHint, string text)
        {
            try
            {
                // USE CURRENT FOREGROUND WINDOW - don't search for a different one!
                IntPtr foregroundWindow = GetForegroundWindow();
                
                if (foregroundWindow == IntPtr.Zero)
                    return new AutomationResult(false, "No foreground window found.");
                
                // Get window title for logging
                StringBuilder sb = new StringBuilder(256);
                GetWindowText(foregroundWindow, sb, 256);
                string windowTitle = sb.ToString();
                
                // Small delay to ensure window is ready
                await Task.Delay(50);
                
                // For browsers, don't search for text areas - just paste into current focus
                // The AI should have already clicked where it wants to type
                
                // Use clipboard paste - most reliable method
                string? originalClip = null;
                try
                {
                    if (System.Windows.Clipboard.ContainsText())
                        originalClip = System.Windows.Clipboard.GetText();
                }
                catch { }
                
                // Set our text to clipboard (must be on STA thread)
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    System.Windows.Clipboard.SetText(text);
                });
                
                await Task.Delay(30);
                
                // Press Ctrl+V
                keybd_event(0x11, 0, 0, UIntPtr.Zero); // VK_CONTROL down
                keybd_event(0x56, 0, 0, UIntPtr.Zero); // V down
                keybd_event(0x56, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // V up
                keybd_event(0x11, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // VK_CONTROL up
                
                await Task.Delay(50);
                
                // Restore original clipboard (async, no wait)
                if (originalClip != null)
                {
                    _ = Task.Run(() => System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        try { System.Windows.Clipboard.SetText(originalClip); } catch { }
                    }));
                }
                
                return new AutomationResult(true, $"Typed in {windowTitle}: {text}");
            }
            catch (Exception ex)
            {
                return new AutomationResult(false, $"Type failed: {ex.Message}");
            }
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
