using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Automation;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Text;

namespace FluxCore
{
    public partial class WindowsAutomationAgent
    {
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
                        // Second try: physical click
                        await Task.Delay(50);
                        lastResult = await ClickElementInternalAsync(nameOrId, usePhysicalClick: true);
                        break;
                    case 2:
                        // Third try: scroll to reveal, then click
                        await ScrollAsync("down");
                        await Task.Delay(50);
                        lastResult = await ClickElementInternalAsync(nameOrId, usePhysicalClick: true);
                        break;
                }

                if (lastResult?.Success == true)
                    return lastResult;
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

                    // Scale from 50% screenshot space to full screen (logical) coordinates
                    // Pipeline: Screenshot=50% logical → ×2 → full logical → SetCursorPos (logical)
                    // NO DPI adjustment needed — entire pipeline is in logical pixel space
                    x *= 2;
                    y *= 2;

                    // BOUNDS CHECK: Refuse clicks outside the active window
                    IntPtr activeWin = targetWindow != IntPtr.Zero ? targetWindow : GetForegroundWindow();
                    if (activeWin != IntPtr.Zero)
                    {
                        GetWindowRect(activeWin, out RECT winRect);
                        int winLeft = winRect.Left, winTop = winRect.Top;
                        int winRight = winRect.Right, winBottom = winRect.Bottom;
                        
                        // Skip bounds check if window rect is invalid (dialogs/popups can return 0,0,0,0)
                        bool validRect = (winRight - winLeft) > 10 && (winBottom - winTop) > 10 && winLeft >= -100;
                        
                        if (validRect && (x < winLeft || x > winRight || y < winTop || y > winBottom))
                        {
                            return new AutomationResult(false,
                                $"OUT OF BOUNDS: Coordinates {x},{y} are outside the active window (valid range: x={winLeft}-{winRight}, y={winTop}-{winBottom}). Use coordinates within the window.");
                        }
                    }

                    // Capture window title BEFORE click
                    string titleBefore = GetActiveWindowTitle();

                    // Identify what's at the click point
                    string elementAtPoint = "";
                    try
                    {
                        var elemAtPoint = System.Windows.Automation.AutomationElement.FromPoint(
                            new System.Windows.Point(x, y));
                        if (elemAtPoint != null)
                            elementAtPoint = $" (element: '{elemAtPoint.Current.Name}' [{elemAtPoint.Current.LocalizedControlType}])";
                    }
                    catch { }

                    SetCursorPos(x, y);
                    await Task.Delay(30);
                    mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
                    await Task.Delay(20);
                    mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);

                    // Check if window changed after click
                    await Task.Delay(50);
                    string titleAfter = GetActiveWindowTitle();
                    string titleChange = titleAfter != titleBefore ? $" | ⚠ Window changed: '{titleBefore}' → '{titleAfter}'" : "";

                    return new AutomationResult(true, $"Clicked at {x},{y}{elementAtPoint}{titleChange}");
                }

                // Wait for element to appear
                var target = await WaitForElementAsync(nameOrId, timeoutMs: 800);

                // Collect ALL matches (not just first) to detect ambiguity
                var allElements = FindClickableElements();
                var allMatches = allElements.Where(e =>
                    e.Name.Equals(nameOrId, StringComparison.OrdinalIgnoreCase) ||
                    e.Name.Contains(nameOrId, StringComparison.OrdinalIgnoreCase) ||
                    nameOrId.Contains(e.Name, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                // AMBIGUITY CHECK: Multiple elements with same/similar name → REFUSE
                if (allMatches.Count > 1)
                {
                    var matchList = string.Join("; ", allMatches.Select(e =>
                    {
                        string parentName = "";
                        try
                        {
                            var parent = System.Windows.Automation.TreeWalker.ControlViewWalker.GetParent(e.Element);
                            if (parent != null && !string.IsNullOrWhiteSpace(parent.Current.Name))
                                parentName = $" in:{parent.Current.Name}";
                        }
                        catch { }
                        return $"'{e.Name}' ({e.Type}{parentName}) at {e.X + e.Width / 2},{e.Y + e.Height / 2}";
                    }));
                    return new AutomationResult(false,
                        $"AMBIGUOUS: {allMatches.Count} elements match '{nameOrId}': [{matchList}]. Use [[CLICK:x,y]] with exact coordinates.");
                }

                // Single match from allMatches
                if (target == null && allMatches.Count == 1)
                    target = allMatches[0];

                // Fallback: Check desktop icons
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
                        ? string.Join(", ", clickables.Take(8).Select(e => $"'{e.Name}'({e.Type}) at {e.X + e.Width / 2},{e.Y + e.Height / 2}"))
                        : "none";
                    return new AutomationResult(false, $"Element not found: '{nameOrId}'. Available: {available}");
                }

                // Bring target window to front
                if (_targetWindow != IntPtr.Zero)
                {
                    SetForegroundWindow(_targetWindow);
                    await Task.Delay(30);
                }

                // Capture window title BEFORE click
                string nameTitleBefore = GetActiveWindowTitle();

                // Try InvokePattern first (unless forced physical click)
                if (!usePhysicalClick && target.Element != null &&
                    target.Element.TryGetCurrentPattern(InvokePattern.Pattern, out object? pattern))
                {
                    ((InvokePattern)pattern).Invoke();
                    await Task.Delay(50);
                    string nameTitleAfter = GetActiveWindowTitle();
                    string nameTitleChange = nameTitleAfter != nameTitleBefore ? $" | Window changed: '{nameTitleBefore}' → '{nameTitleAfter}'" : "";
                    return new AutomationResult(true, $"Clicked: '{target.Name}' ({target.Type} at {target.X + target.Width / 2},{target.Y + target.Height / 2}){nameTitleChange}");
                }

                // Physical mouse click
                int centerX = target.X + target.Width / 2;
                int centerY = target.Y + target.Height / 2;

                SetCursorPos(centerX, centerY);
                await Task.Delay(30);
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
                await Task.Delay(20);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);

                await Task.Delay(50);
                string namePhysTitle = GetActiveWindowTitle();
                string namePhysTitleChange = namePhysTitle != nameTitleBefore ? $" | Window changed: '{nameTitleBefore}' → '{namePhysTitle}'" : "";
                return new AutomationResult(true, $"Clicked: '{target.Name}' ({target.Type} at {centerX},{centerY}){namePhysTitleChange}");
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
    }
}
