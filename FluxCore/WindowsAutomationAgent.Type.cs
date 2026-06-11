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
                    string? savedClip = null;

                    // Save user's clipboard, set our text, verify
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        try
                        {
                            // SAVE user's clipboard before overwriting
                            if (System.Windows.Clipboard.ContainsText())
                                savedClip = System.Windows.Clipboard.GetText();

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

                    await Task.Delay(50); // Wait for paste to complete

                    // RESTORE user's clipboard
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        try
                        {
                            if (savedClip != null)
                                System.Windows.Clipboard.SetText(savedClip);
                            else
                                System.Windows.Clipboard.Clear();
                        }
                        catch { /* Best effort restore */ }
                    });

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
    }
}
