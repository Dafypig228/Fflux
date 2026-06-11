using System;
using System.IO;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Microsoft.Playwright;

namespace FluxCore
{
    /// <summary>
    /// Execution Agent: Handles autonomous file reading, web browsing, and app launching.
    /// All actions require user permission via dialog.
    /// </summary>
    public class ExecutionAgent : IAsyncDisposable
    {
        private IPlaywright? _playwright;
        private IBrowser? _browser;
        private IPage? _currentPage;
        
        public bool IsInitialized => _browser != null;

        // =========================================
        // INITIALIZATION
        // =========================================
        public async Task InitializeAsync()
        {
            if (_playwright != null) return;
            
            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = false, // Visible browser for user trust
                Args = new[] { "--start-maximized" }
            });
        }

        // =========================================
        // FILE OPERATIONS
        // =========================================
        
        /// <summary>
        /// Reads a file and returns its content.
        /// </summary>
        public async Task<ExecutionResult> ReadFileAsync(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                    return new ExecutionResult(false, $"File not found: {filePath}");
                
                var info = new FileInfo(filePath);
                
                // Security: Limit file size to 1MB
                if (info.Length > 1_000_000)
                    return new ExecutionResult(false, "File too large (>1MB). Skipping for safety.");
                
                // Security: Only allow text-based files
                string[] allowedExtensions = { ".txt", ".cs", ".json", ".xml", ".md", ".py", ".js", ".html", ".css", ".yaml", ".yml", ".log", ".config" };
                if (!Array.Exists(allowedExtensions, ext => filePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                    return new ExecutionResult(false, $"File type not allowed. Allowed: {string.Join(", ", allowedExtensions)}");
                
                string content = await File.ReadAllTextAsync(filePath);
                
                // Truncate if too long
                if (content.Length > 5000)
                    content = content.Substring(0, 5000) + "\n\n[... TRUNCATED ...]";
                
                return new ExecutionResult(true, content);
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"Error reading file: {ex.Message}");
            }
        }

        // =========================================
        // WEB OPERATIONS (PLAYWRIGHT)
        // =========================================
        
        /// <summary>
        /// Opens a URL in the browser and returns the page text content.
        /// </summary>
        public async Task<ExecutionResult> OpenUrlAsync(string url)
        {
            try
            {
                // Try Playwright first
                try
                {
                    if (_browser == null)
                        await InitializeAsync();
                    
                    if (_browser != null)
                    {
                        _currentPage = await _browser.NewPageAsync();
                        await _currentPage.GotoAsync(url, new PageGotoOptions { Timeout = 30000 });
                        await _currentPage.WaitForLoadStateAsync(LoadState.NetworkIdle);
                        
                        string textContent = await _currentPage.InnerTextAsync("body");
                        if (textContent.Length > 8000)
                            textContent = textContent.Substring(0, 8000) + "\n\n[... TRUNCATED ...]";
                        
                        return new ExecutionResult(true, $"[URL: {url}]\n\n{textContent}");
                    }
                }
                catch (Exception playwrightEx)
                {
                    // Playwright failed, fall back to system browser
                    System.Diagnostics.Debug.WriteLine($"Playwright failed: {playwrightEx.Message}");
                }

                // Fallback: Open in system default browser
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
                return new ExecutionResult(true, $"Opened in default browser: {url}\n(Playwright unavailable - using system browser)");
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"Error opening URL: {ex.Message}");
            }
        }

        /// <summary>
        /// Takes a screenshot of the current browser page.
        /// </summary>
        public async Task<string?> GetBrowserScreenshotBase64Async()
        {
            if (_currentPage == null) return null;
            
            try
            {
                byte[] screenshot = await _currentPage.ScreenshotAsync(new PageScreenshotOptions { Type = ScreenshotType.Jpeg, Quality = 70 });
                return Convert.ToBase64String(screenshot);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Clicks an element on the current page.
        /// </summary>
        public async Task<ExecutionResult> ClickElementAsync(string selector)
        {
            if (_currentPage == null)
                return new ExecutionResult(false, "No browser page open.");
            
            try
            {
                await _currentPage.ClickAsync(selector);
                await _currentPage.WaitForLoadStateAsync(LoadState.NetworkIdle);
                return new ExecutionResult(true, $"Clicked: {selector}");
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"Click failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Types text into an input field.
        /// </summary>
        public async Task<ExecutionResult> TypeTextAsync(string selector, string text)
        {
            if (_currentPage == null)
                return new ExecutionResult(false, "No browser page open.");
            
            try
            {
                await _currentPage.FillAsync(selector, text);
                return new ExecutionResult(true, $"Typed into {selector}: {text}");
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"Type failed: {ex.Message}");
            }
        }

        /// <summary>
        /// RELIABLE: Clicks element by visible text (no coordinates!)
        /// </summary>
        public async Task<ExecutionResult> ClickByTextAsync(string text)
        {
            if (_currentPage == null)
                return new ExecutionResult(false, "No browser page open.");
            
            try
            {
                // Try various text-based selectors
                var selectors = new[]
                {
                    $"text={text}",
                    $"*:has-text(\"{text}\")",
                    $"[aria-label*=\"{text}\"]",
                    $"button:has-text(\"{text}\")",
                    $"a:has-text(\"{text}\")"
                };
                
                foreach (var selector in selectors)
                {
                    try
                    {
                        var element = _currentPage.Locator(selector).First;
                        if (await element.IsVisibleAsync())
                        {
                            await element.ClickAsync(new LocatorClickOptions { Timeout = 3000 });
                            return new ExecutionResult(true, $"Clicked element with text: {text}");
                        }
                    }
                    catch { continue; }
                }
                
                return new ExecutionResult(false, $"Could not find element with text: {text}");
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"Click by text failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Types into the currently focused element.
        /// </summary>
        public async Task<ExecutionResult> TypeIntoFocusedAsync(string text)
        {
            if (_currentPage == null)
                return new ExecutionResult(false, "No browser page open.");
            
            try
            {
                await _currentPage.Keyboard.TypeAsync(text);
                return new ExecutionResult(true, $"Typed: {text}");
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"Type failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets info about the current page for AI understanding.
        /// </summary>
        public async Task<ExecutionResult> GetPageInfoAsync()
        {
            if (_currentPage == null)
                return new ExecutionResult(false, "No browser page open.");
            
            try
            {
                string url = _currentPage.Url;
                string title = await _currentPage.TitleAsync();
                
                // Get visible interactive elements
                var buttons = await _currentPage.Locator("button:visible").AllTextContentsAsync();
                var inputs = await _currentPage.Locator("input:visible").CountAsync();
                var links = await _currentPage.Locator("a:visible").AllTextContentsAsync();
                
                var info = $"Page: {title}\nURL: {url}\n";
                info += $"Buttons: {string.Join(", ", buttons.Take(10))}\n";
                info += $"Inputs: {inputs} visible\n";
                info += $"Links: {string.Join(", ", links.Take(10))}";
                
                return new ExecutionResult(true, info);
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"Get page info failed: {ex.Message}");
            }
        }

        // =========================================
        // APP OPERATIONS
        // =========================================
        
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        private const int SW_RESTORE = 9;

        /// <summary>
        /// Opens an application - searches Start Menu and common locations.
        /// </summary>
        public async Task<ExecutionResult> OpenApp(string target)
        {
            try
            {
                // 0. CHECK IF ALREADY RUNNING (Focus it!)
                // 0. CHECK IF ALREADY RUNNING (Focus it!)
                string procName = target.Replace(".exe", "").ToLower();
                
                // Handle common aliases
                if (procName.Contains("google chrome") || procName.Contains("chrome")) procName = "chrome";
                if (procName.Contains("edge")) procName = "msedge";
                if (procName.Contains("firefox")) procName = "firefox";
                if (procName.Contains("telegram")) procName = "telegram";
                if (procName.Contains("whatsapp")) procName = "whatsapp";

                var processes = Process.GetProcesses();
                foreach (var p in processes)
                {
                    if (p.ProcessName.ToLower() == procName || 
                       (procName.Length > 4 && p.ProcessName.ToLower().Contains(procName)))
                    {
                        if (p.MainWindowHandle == IntPtr.Zero) continue;

                        IntPtr handle = p.MainWindowHandle;
                        ShowWindow(handle, SW_RESTORE);
                        
                        bool success = false;
                        for (int i = 0; i < 3; i++)
                        {
                            SetForegroundWindow(handle);
                            await Task.Delay(50);
                            
                            IntPtr active = GetForegroundWindow();
                            if (active == handle) 
                            { 
                                success = true; 
                                break; 
                            }
                            
                            if (i == 1)
                            {
                                ShowWindow(handle, 6);
                                await Task.Delay(30);
                                ShowWindow(handle, 9);
                            }
                        }
                        
                        if (success || GetForegroundWindow() == handle)
                        {
                            // For Chrome: try to enable accessibility on existing instance
                            if (procName == "chrome" || p.ProcessName.ToLower() == "chrome")
                            {
                                try
                                {
                                    // Request the accessibility tree — this forces Chrome to enable it
                                    var chromeElement = AutomationElement.FromHandle(handle);
                                    if (chromeElement != null)
                                    {
                                        // Attempt to walk the tree — triggers Chrome's renderer accessibility
                                        var doc = chromeElement.FindFirst(TreeScope.Descendants,
                                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
                                    }
                                }
                                catch { }
                            }
                            return new ExecutionResult(true, $"Focused existing: {p.ProcessName}");
                        }
                    }
                }

                // CHROME SPECIAL: Launch with accessibility enabled so UIA can see web page elements
                if (procName == "chrome")
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "chrome",
                            Arguments = "--force-renderer-accessibility",
                            UseShellExecute = true
                        });
                        await Task.Delay(500);
                        return new ExecutionResult(true, "Opened: Chrome (with accessibility)");
                    }
                    catch
                    {
                        // Try full path
                        string[] chromePaths = {
                            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe")
                        };
                        foreach (var path in chromePaths)
                        {
                            if (File.Exists(path))
                            {
                                Process.Start(new ProcessStartInfo
                                {
                                    FileName = path,
                                    Arguments = "--force-renderer-accessibility",
                                    UseShellExecute = true
                                });
                                await Task.Delay(500);
                                return new ExecutionResult(true, "Opened: Chrome (with accessibility)");
                            }
                        }
                    }
                }

                // First try direct launch
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = target,
                        UseShellExecute = true
                    });
                    return new ExecutionResult(true, $"Opened: {target}");
                }
                catch { }

                // Search in Start Menu
                var startMenuPaths = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs")
                };

                string searchName = target.Replace(".exe", "").Replace(".lnk", "");
                
                foreach (var startPath in startMenuPaths)
                {
                    if (!Directory.Exists(startPath)) continue;
                    
                    try 
                    {
                        var files = Directory.GetFiles(startPath, "*.lnk", SearchOption.AllDirectories);
                        var match = files.FirstOrDefault(f => 
                            Path.GetFileNameWithoutExtension(f).Contains(searchName, StringComparison.OrdinalIgnoreCase));
                        
                        if (match != null)
                        {
                            try {
                                Process.Start(new ProcessStartInfo
                                {
                                    FileName = match,
                                    UseShellExecute = true
                                });
                                return new ExecutionResult(true, $"Opened: {Path.GetFileNameWithoutExtension(match)}");
                            } catch {} // Ignore start failure
                        }
                    }
                    catch (UnauthorizedAccessException) { continue; } // Ignore access denied
                    catch (Exception) { continue; } 
                }

                // All search methods failed — return FAILURE (don't open Explorer!)
                return new ExecutionResult(false, 
                    $"Could not find application: '{target}'. Searched running processes, Start Menu, and common paths. " +
                    $"Try using [[RUN_SHELL:Start-Process '{target}']] or the exact executable path.");
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"Failed to open: {ex.Message}");
            }
        }

        // =========================================
        // CLEANUP
        // =========================================
        public async ValueTask DisposeAsync()
        {
            if (_currentPage != null) await _currentPage.CloseAsync();
            if (_browser != null) await _browser.CloseAsync();
            _playwright?.Dispose();
        }
    }

    /// <summary>
    /// Result of an execution action.
    /// </summary>
    public class ExecutionResult
    {
        public bool Success { get; }
        public string Message { get; }

        public ExecutionResult(bool success, string message)
        {
            Success = success;
            Message = message;
        }
    }
}
