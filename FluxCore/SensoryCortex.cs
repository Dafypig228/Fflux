using System;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Linq;
using System.Collections.Generic;

namespace FluxCore
{
    public class SensoryCortex
    {
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        private OcrEngine? _ocrEngine;

        public SensoryCortex()
        {
            try { _ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages(); }
            catch { _ocrEngine = null; }
        }

        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private const uint GW_HWNDNEXT = 2;

        public string GetActiveWindow()
        {
            IntPtr hwnd = GetForegroundWindow();
            int myPid = System.Diagnostics.Process.GetCurrentProcess().Id;
            
            // Начинаем поиск
            int maxDepth = 20; // Увеличим глубину
            while (maxDepth > 0 && hwnd != IntPtr.Zero)
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                
                // Если это МЫ - пропускаем сразу
                if (pid == myPid)
                {
                hwnd = GetWindow(hwnd, GW_HWNDNEXT);
                    continue;
                }

                StringBuilder sb = new StringBuilder(256);
                GetWindowText(hwnd, sb, 256);
                string title = sb.ToString();

                // Check size to filter ghost windows (Chrome background processes often have 0x0 size)
                GetWindowRect(hwnd, out RECT rect);
                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;

                // Фильтр системных окон и оверлеев
                if (IsWindowVisible(hwnd) && width > 10 && height > 10 &&
                    !string.IsNullOrWhiteSpace(title) && 
                    title != "Program Manager" && 
                    title != "Default IME" &&
                    !title.Contains("NVIDIA GeForce Overlay", StringComparison.OrdinalIgnoreCase))
                {
                    return title;
                }

                hwnd = GetWindow(hwnd, GW_HWNDNEXT);
                maxDepth--;
            }
            
            return "Desktop";
        }

        // --- TRUE VISION (СКРИНШОТ ДЛЯ GEMINI) ---
        public string GetScreenBase64()
        {
            try
            {
                var bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
                using (var bitmap = new Bitmap(bounds.Width, bounds.Height))
                {
                    using (var g = Graphics.FromImage(bitmap))
                    {
                        g.CopyFromScreen(System.Drawing.Point.Empty, System.Drawing.Point.Empty, bounds.Size);
                    }
                    using (var ms = new MemoryStream())
                    {
                        var encoder = GetEncoder(System.Drawing.Imaging.ImageFormat.Jpeg);
                        var encoderParams = new System.Drawing.Imaging.EncoderParameters(1);
                        encoderParams.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 60L);
                        bitmap.Save(ms, encoder, encoderParams);
                        return Convert.ToBase64String(ms.ToArray());
                    }
                }
            }
            catch { return ""; }
        }
        private System.Drawing.Imaging.ImageCodecInfo GetEncoder(System.Drawing.Imaging.ImageFormat format)
        {
            return System.Drawing.Imaging.ImageCodecInfo.GetImageDecoders().FirstOrDefault(c => c.FormatID == format.Guid)
                    ?? System.Drawing.Imaging.ImageCodecInfo.GetImageDecoders()[0];
        }

        // ===== 1. UI ИЕРАРХИЯ (UIA) =====
        public string GetLayer3_UIHierarchy()
        {
            try
            {
                StringBuilder result = new StringBuilder();
                GetCursorPos(out POINT p);

                var element = AutomationElement.FromPoint(new System.Windows.Point(p.X, p.Y));

                if (element != null)
                {
                    result.AppendLine("=== 🎯 CURSOR POINTING AT (PRIORITY #1) ===");
                    result.AppendLine(GetElementDetails(element));

                    result.AppendLine("\n=== 📦 CONTAINER / PARENT (PRIORITY #2) ===");
                    try
                    {
                        var parent = TreeWalker.ControlViewWalker.GetParent(element);
                        if (parent != null)
                        {
                            result.AppendLine(GetElementSummary(parent));
                            var grandParent = TreeWalker.ControlViewWalker.GetParent(parent);
                            if (grandParent != null) result.AppendLine($" (Inside: {GetElementSummary(grandParent)})");
                        }
                    }
                    catch { }
                }
                return result.ToString();
            }
            catch (Exception ex) { return $"UI Error: {ex.Message}"; }
        }

        private string GetElementDetails(AutomationElement element)
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                sb.AppendLine($"Type: {element.Current.LocalizedControlType}");
                sb.AppendLine($"Name: {element.Current.Name}");
                sb.AppendLine($"Enabled: {element.Current.IsEnabled}");

                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object valPattern))
                    sb.AppendLine($"Value: {((ValuePattern)valPattern).Current.Value}");

                if (element.TryGetCurrentPattern(TextPattern.Pattern, out object txtPattern))
                    sb.AppendLine($"Text: {((TextPattern)txtPattern).DocumentRange.GetText(100)}");
            }
            catch { }
            return sb.ToString();
        }

        private string GetElementSummary(AutomationElement element)
        {
            try { return $"{element.Current.LocalizedControlType}: '{element.Current.Name}'"; } catch { return "Unknown"; }
        }

        /// <summary>
        /// Scans the active window for clickable elements and returns their positions.
        /// This gives the AI "vision" to know WHERE to click.
        /// </summary>
        public string GetClickableElements(int maxElements = 30)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("=== CLICKABLE ELEMENTS (Name → X,Y coordinates) ===");
                
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return "No active window";
                
                var rootElement = AutomationElement.FromHandle(hwnd);
                if (rootElement == null) return "Cannot access window";
                
                // Find all clickable elements: buttons, links, edit boxes, etc.
                var condition = new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Image)
                );
                
                var elements = rootElement.FindAll(TreeScope.Descendants, condition);
                int count = 0;
                
                foreach (AutomationElement elem in elements)
                {
                    if (count >= maxElements) break;
                    
                    try
                    {
                        var rect = elem.Current.BoundingRectangle;
                        
                        // Skip elements with no size or off-screen
                        if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) continue;
                        if (rect.X < 0 || rect.Y < 0) continue;
                        
                        // Get center coordinates
                        int centerX = (int)(rect.X + rect.Width / 2);
                        int centerY = (int)(rect.Y + rect.Height / 2);
                        
                        string name = elem.Current.Name;
                        string type = elem.Current.LocalizedControlType;
                        
                        // Skip empty names for certain types
                        if (string.IsNullOrWhiteSpace(name) && type != "edit") continue;
                        
                        // Format: "Button 'Send' → 1234,567"
                        string displayName = string.IsNullOrWhiteSpace(name) ? $"({type})" : name;
                        if (displayName.Length > 40) displayName = displayName.Substring(0, 37) + "...";
                        
                        sb.AppendLine($"  [{type}] \"{displayName}\" → {centerX},{centerY}");
                        count++;
                    }
                    catch { /* Skip problematic elements */ }
                }
                
                if (count == 0)
                {
                    sb.AppendLine("  (No clickable elements found - may be a web page, use Tab/Enter instead)");
                }
                else
                {
                    sb.AppendLine($"\nTo click an element, use: [[CLICK:x,y]]");
                    sb.AppendLine($"Example: [[CLICK:1234,567]]");
                }
                
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"Element scan error: {ex.Message}";
            }
        }

        // ===== 2. МОЩНЫЙ OCR (С РАСШИРЕННЫМ КОНТЕКСТОМ) =====
        public async Task<string> GetVisualContext()
        {
            if (_ocrEngine == null) return "[OCR Not Init]";
            StringBuilder result = new StringBuilder();

            try
            {
                // 1. Активное окно (Оставляем, полезно для понимания приложения)
                IntPtr hwnd = GetForegroundWindow();
                if (GetWindowRect(hwnd, out RECT r))
                {
                    result.AppendLine("=== ACTIVE WINDOW TEXT ===");
                    Rectangle rect = new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                    // Ограничиваем скан окна (иногда оно слишком большое и тормозит)
                    if (rect.Width > 0 && rect.Height > 0)
                        result.AppendLine(await ScanRegion(rect));
                }

                // 2. Зона курсора (ИЗМЕНЕНО НА WIDE MODE)
                GetCursorPos(out POINT c);
                result.AppendLine("\n=== CURSOR LINE CONTEXT ===");

                // БЫЛО: Square 300x300 (обрезало длинные строки)
                // Rectangle rectSquare = new Rectangle(c.X - 150, c.Y - 150, 300, 300);

                // СТАЛО: Wide Strip 1200x80 (видит всю строку целиком)
                int w = 1200;
                int h = 80;
                // Центрируем по X, центрируем по Y
                Rectangle rectWide = new Rectangle(c.X - (w / 2), c.Y - (h / 2), w, h);

                result.AppendLine(await ScanRegion(rectWide));

                return result.ToString();
            }
            catch { return "[Vision Error]"; }
        }

        private async Task<string> ScanRegion(Rectangle bounds)
        {
            try
            {
                var screen = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);

                // Обрезаем границы, чтобы не вылезти за экран
                int x = Math.Max(0, bounds.X);
                int y = Math.Max(0, bounds.Y);
                int w = Math.Min(bounds.Width, screen.Width - x);
                int h = Math.Min(bounds.Height, screen.Height - y);

                if (w < 10 || h < 10) return "";

                using (var bmp = new Bitmap(w, h))
                {
                    using (var g = Graphics.FromImage(bmp))
                        g.CopyFromScreen(x, y, 0, 0, new Size(w, h));

                    using (var ms = new MemoryStream())
                    {
                        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
                        ms.Position = 0;
                        var decoder = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
                        using (var sb = await decoder.GetSoftwareBitmapAsync())
                        {
                            var res = await _ocrEngine.RecognizeAsync(sb);
                            return res.Text;
                        }
                    }
                }
            }
            catch { return ""; }
        }

        // ===== 3. ПРОЦЕССЫ И СИСТЕМА =====
        public string GetRunningProcesses()
        {
            try
            {
                var procs = System.Diagnostics.Process.GetProcesses()
                    .Where(p => !string.IsNullOrEmpty(p.MainWindowTitle))
                    .Take(30);
                return "PROCESSES: " + string.Join(", ", procs.Select(p => p.ProcessName));
            }
            catch { return ""; }
        }

        public string GetSystemInfo() => $"Time: {DateTime.Now:HH:mm:ss}, User: {Environment.UserName}";
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
    }
}