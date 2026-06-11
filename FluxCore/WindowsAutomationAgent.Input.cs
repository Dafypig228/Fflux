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
        // COMPLETE key map — covers all keys Gemini might request
        private static readonly Dictionary<string, byte> KeyMap = new(StringComparer.OrdinalIgnoreCase)
        {
            // Letters A-Z
            ["A"] = 0x41, ["B"] = 0x42, ["C"] = 0x43, ["D"] = 0x44, ["E"] = 0x45,
            ["F"] = 0x46, ["G"] = 0x47, ["H"] = 0x48, ["I"] = 0x49, ["J"] = 0x4A,
            ["K"] = 0x4B, ["L"] = 0x4C, ["M"] = 0x4D, ["N"] = 0x4E, ["O"] = 0x4F,
            ["P"] = 0x50, ["Q"] = 0x51, ["R"] = 0x52, ["S"] = 0x53, ["T"] = 0x54,
            ["U"] = 0x55, ["V"] = 0x56, ["W"] = 0x57, ["X"] = 0x58, ["Y"] = 0x59, ["Z"] = 0x5A,
            // Digits 0-9
            ["0"] = 0x30, ["1"] = 0x31, ["2"] = 0x32, ["3"] = 0x33, ["4"] = 0x34,
            ["5"] = 0x35, ["6"] = 0x36, ["7"] = 0x37, ["8"] = 0x38, ["9"] = 0x39,
            // Function keys F1-F12
            ["F1"] = 0x70, ["F2"] = 0x71, ["F3"] = 0x72, ["F4"] = 0x73,
            ["F5"] = 0x74, ["F6"] = 0x75, ["F7"] = 0x76, ["F8"] = 0x77,
            ["F9"] = 0x78, ["F10"] = 0x79, ["F11"] = 0x7A, ["F12"] = 0x7B,
            // Arrow keys
            ["LEFT"] = 0x25, ["UP"] = 0x26, ["RIGHT"] = 0x27, ["DOWN"] = 0x28,
            // Navigation
            ["ENTER"] = 0x0D, ["RETURN"] = 0x0D,
            ["BACKSPACE"] = 0x08, ["BACK"] = 0x08,
            ["DELETE"] = 0x2E, ["DEL"] = 0x2E,
            ["TAB"] = 0x09,
            ["ESCAPE"] = 0x1B, ["ESC"] = 0x1B,
            ["HOME"] = 0x24, ["END"] = 0x23,
            ["PAGEUP"] = 0x21, ["PGUP"] = 0x21,
            ["PAGEDOWN"] = 0x22, ["PGDN"] = 0x22,
            ["INSERT"] = 0x2D, ["INS"] = 0x2D,
            ["SPACE"] = 0x20,
            // Misc
            ["PRINTSCREEN"] = 0x2C, ["PRTSC"] = 0x2C,
            ["PAUSE"] = 0x13, ["BREAK"] = 0x13,
            ["NUMLOCK"] = 0x90, ["SCROLLLOCK"] = 0x91, ["CAPSLOCK"] = 0x14,
            // Punctuation (common ones Gemini might use)
            ["PLUS"] = 0xBB, ["MINUS"] = 0xBD, ["EQUALS"] = 0xBB,
        };

        // Modifier key names → VK codes
        private static readonly Dictionary<string, byte> ModifierMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["CTRL"] = 0x11, ["CONTROL"] = 0x11,
            ["ALT"] = 0x12, ["MENU"] = 0x12,
            ["SHIFT"] = 0x10,
            ["WIN"] = 0x5B, ["WINDOWS"] = 0x5B, ["LWIN"] = 0x5B,
        };

        /// <summary>
        /// Sends keyboard shortcuts (CTRL+A, CTRL+V, BACKSPACE, ENTER, ALT+F4, etc.)
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

                // Parse key combo (e.g., "CTRL+A", "BACKSPACE", "CTRL+SHIFT+T")
                var parts = keyCombo.Split('+');
                var modifiers = new List<byte>();
                byte mainKey = 0;

                foreach (var part in parts)
                {
                    string key = part.Trim();

                    // Check if it's a modifier
                    if (ModifierMap.TryGetValue(key, out byte modVk))
                    {
                        modifiers.Add(modVk);
                        continue;
                    }

                    // Check the key map (case-insensitive)
                    if (KeyMap.TryGetValue(key, out byte vk))
                    {
                        mainKey = vk;
                        continue;
                    }

                    // Fallback: single character → use VkKeyScan
                    if (key.Length == 1)
                    {
                        short vkResult = VkKeyScan(key[0]);
                        if (vkResult != -1)
                            mainKey = (byte)(vkResult & 0xFF);
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
                IntPtr targetHwnd = _targetWindow != IntPtr.Zero ? _targetWindow : GetForegroundWindow();

                if (targetHwnd != IntPtr.Zero)
                {
                    SetForegroundWindow(targetHwnd);
                    await Task.Delay(50);

                    // Move cursor to center of target window so scroll goes to the right place
                    if (GetWindowRect(targetHwnd, out RECT rect))
                    {
                        int centerX = (rect.Left + rect.Right) / 2;
                        int centerY = (rect.Top + rect.Bottom) / 2;
                        SetCursorPos(centerX, centerY);
                        await Task.Delay(30);
                    }
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

                // Scale from 50% screenshot space to full screen (logical) coordinates
                // NO DPI adjustment — entire pipeline is in logical pixel space
                x1 *= 2;
                y1 *= 2;
                x2 *= 2;
                y2 *= 2;

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
    }
}
