using System;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Windows.Graphics.Imaging;
using Windows.Media.Control;
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
                    // Resize to 50% for faster Gemini processing (original coords still valid — AI sees proportional layout)
                    int halfW = bounds.Width / 2;
                    int halfH = bounds.Height / 2;
                    using (var resized = new Bitmap(halfW, halfH))
                    {
                        using (var g2 = Graphics.FromImage(resized))
                        {
                            g2.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                            g2.DrawImage(bitmap, 0, 0, halfW, halfH);
                        }
                        using (var ms = new MemoryStream())
                        {
                            var encoder = GetEncoder(System.Drawing.Imaging.ImageFormat.Jpeg);
                            var encoderParams = new System.Drawing.Imaging.EncoderParameters(1);
                            encoderParams.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 50L);
                            resized.Save(ms, encoder, encoderParams);
                            return Convert.ToBase64String(ms.ToArray());
                        }
                    }
                }
            }
            catch { return ""; }
        }
        /// <summary>
        /// Logical size of the screen the screenshot is derived from (FULL, not the
        /// half-res capture). Single source of truth for mapping vision-model normalized
        /// coordinates back to clickable pixels — read live so a resolution/DPI/monitor
        /// change can never desync the mapping. GetScreenBase64 halves THIS, so any change
        /// to the capture scale must stay paired with the consumer in JarvisCore.
        /// </summary>
        public (int width, int height) GetScreenLogicalSize()
        {
            var b = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            return (b.Width, b.Height);
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
                sb.AppendLine("=== VISIBLE UI ELEMENTS ===");
                
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return "No active window";
                
                var rootElement = AutomationElement.FromHandle(hwnd);
                if (rootElement == null) return "Cannot access window";

                // Check if this is a browser (Chrome with accessibility exposes web page elements)
                string windowName = rootElement.Current.Name ?? "";
                bool isBrowser = windowName.Contains("Chrome") || windowName.Contains("Edge") || windowName.Contains("Firefox");
                int effectiveMax = isBrowser ? 50 : maxElements; // More elements for web pages
                
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
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Image),
                    // Web page elements (Chrome with --force-renderer-accessibility)
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Custom)
                );
                
                var elements = rootElement.FindAll(TreeScope.Descendants, condition);
                
                // Collect elements with parent context + locale-independent classification.
                // type  = LocalizedControlType (shown to the model — matches the screenshot language).
                // ctype = ControlType enum, used for ALL logic. The old code compared the LOCALIZED
                // string to "edit"/"image"/"button", so on a non-English Windows EVERY input field
                // was dropped (Telegram's "поле" search box → never listed → blind coord-guessing).
                var collected = new List<(string name, string type, ControlType ctype, string parent, int x, int y, bool isInput, bool isInteractive)>();

                foreach (AutomationElement elem in elements)
                {
                    if (collected.Count >= effectiveMax * 3) break;

                    try
                    {
                        var rect = elem.Current.BoundingRectangle;
                        if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) continue;
                        if (rect.X < 0 || rect.Y < 0) continue;
                        // Filter tiny invisible elements (web page artifacts)
                        if (rect.Width < 10 || rect.Height < 10) continue;

                        int centerX = (int)(rect.X + rect.Width / 2);
                        int centerY = (int)(rect.Y + rect.Height / 2);

                        string name = elem.Current.Name;
                        string type = elem.Current.LocalizedControlType;
                        ControlType ctype = elem.Current.ControlType;

                        bool isInput = ctype == ControlType.Edit || ctype == ControlType.ComboBox;
                        bool isInteractive = isInput
                            || ctype == ControlType.Button || ctype == ControlType.Hyperlink
                            || ctype == ControlType.MenuItem || ctype == ControlType.ListItem
                            || ctype == ControlType.TabItem || ctype == ControlType.CheckBox
                            || ctype == ControlType.RadioButton || ctype == ControlType.TreeItem
                            || ctype == ControlType.DataItem || ctype == ControlType.SplitButton;

                        // KEEP an input field even when unnamed (that was the dropped search box).
                        // Drop only unnamed, non-input noise (Text/Group/Custom/Image with no name).
                        if (string.IsNullOrWhiteSpace(name) && !isInput) continue;

                        // Get parent element name for context
                        string parentName = "";
                        try
                        {
                            var parent = TreeWalker.ControlViewWalker.GetParent(elem);
                            if (parent != null && !string.IsNullOrWhiteSpace(parent.Current.Name))
                                parentName = parent.Current.Name;
                            else if (parent != null)
                            {
                                // Try grandparent
                                var gp = TreeWalker.ControlViewWalker.GetParent(parent);
                                if (gp != null && !string.IsNullOrWhiteSpace(gp.Current.Name))
                                    parentName = gp.Current.Name;
                            }
                        }
                        catch { }

                        // Unnamed input → label it so the model can still target it honestly.
                        string displayName = name;
                        if (string.IsNullOrWhiteSpace(displayName))
                        {
                            try { displayName = elem.Current.HelpText; } catch { }
                            if (string.IsNullOrWhiteSpace(displayName))
                            {
                                try { var aid = elem.Current.AutomationId; if (!string.IsNullOrWhiteSpace(aid)) displayName = aid; } catch { }
                            }
                            if (string.IsNullOrWhiteSpace(displayName)) displayName = $"({type} field)";
                        }
                        if (displayName.Length > 40) displayName = displayName.Substring(0, 37) + "...";

                        // Truncate parent name too (WhatsApp previews can be 500+ chars)
                        if (parentName.Length > 40) parentName = parentName.Substring(0, 37) + "...";

                        collected.Add((displayName, type, ctype, parentName, centerX, centerY, isInput, isInteractive));
                    }
                    catch { }
                }
                
                // DEDUP: collapse elements at the same spot (link + icon overlay). Uses the
                // ControlType enum, not localized strings, so it works in any UI language.
                var deduped = new List<(string name, string type, ControlType ctype, string parent, int x, int y, bool isInput, bool isInteractive)>();
                var seenCoords = new HashSet<string>();
                foreach (var el in collected)
                {
                    // Round to 10px grid to catch near-duplicates (5px was too small: 494/5≠496/5)
                    string coordKey = $"{el.x / 10},{el.y / 10}";
                    if (seenCoords.Contains(coordKey))
                    {
                        if (el.ctype == ControlType.Image) continue; // drop icon overlay at a shared coord
                        int existingIdx = deduped.FindIndex(d => $"{d.x / 10},{d.y / 10}" == coordKey);
                        // Prefer the more actionable element when two share a coordinate.
                        if (existingIdx >= 0 && (el.ctype == ControlType.Button || el.ctype == ControlType.Hyperlink || el.isInput))
                            deduped[existingIdx] = el;
                        continue;
                    }
                    seenCoords.Add(coordKey);
                    deduped.Add(el);
                }

                // STABLE READING ORDER: top→bottom in ~16px bands, then left→right. Makes [n] mean
                // what the model sees in the screenshot and stops the index reshuffling between
                // steps (the "wanted Контакты [8], clicked the chat 'Надо'" failure, trace 205531).
                var ordered = deduped.OrderBy(d => d.y / 16).ThenBy(d => d.x).ToList();

                // Apply the cap, but NEVER drop an input field — a missing search box is exactly
                // what blinded him. Any input past the cap is appended rather than cut.
                var shown = ordered.Take(effectiveMax).ToList();
                foreach (var input in ordered.Skip(effectiveMax).Where(d => d.isInput))
                    shown.Add(input);

                int count = 0;
                foreach (var el in shown)
                {
                    string parentInfo = !string.IsNullOrEmpty(el.parent) ? $" in:{el.parent}" : "";
                    // Coords divided by 2 to match 50%-scaled screenshot space (invariant #2)
                    sb.AppendLine($"  [{count + 1}] {el.type} \"{el.name}\"{parentInfo} → {el.x / 2},{el.y / 2}");
                    count++;
                }

                // COVERAGE HONESTY — lets the model reason about ABSENCE instead of inventing an index.
                if (count == 0)
                {
                    sb.AppendLine("  (NONE — this window exposes no accessibility elements; it is pixel-only.");
                    sb.AppendLine("   Use the screenshot with [[FIND_AND_CLICK:description]] or [[CLICK:x,y]]. Do NOT invent a [[CLICK:n]] index.)");
                }
                else
                {
                    if (ordered.Count > count)
                        sb.AppendLine($"  …(+{ordered.Count - count} more off-screen — [[SCROLL:down]] to reveal, or be more specific)");
                    sb.AppendLine("\n⚠ This list is your GROUND TRUTH. [[CLICK:n]] is valid ONLY for an [n] above. If the control");
                    sb.AppendLine("  you want is NOT listed, it is not in the accessibility tree — [[SCROLL:down]] to reveal it,");
                    sb.AppendLine("  use [[FIND_AND_CLICK:desc]], or conclude it is not present. NEVER guess an index.");
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

        // ===== 4. APP FOCUS TIMELINE =====
        private record FocusEvent(string App, DateTime Start, DateTime End);
        private readonly List<FocusEvent> _focusEvents = new();
        private string _currentFocusApp = "";
        private DateTime _currentFocusStart = DateTime.Now;
        private System.Threading.Timer? _focusTimer;

        public void StartFocusTracking()
        {
            _currentFocusApp   = GetActiveWindow();
            _currentFocusStart = DateTime.Now;
            _focusTimer = new System.Threading.Timer(_ => PollFocus(), null,
                TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }

        private void PollFocus()
        {
            try
            {
                string current = GetActiveWindow();
                if (current == _currentFocusApp) return;

                var completed = new FocusEvent(_currentFocusApp, _currentFocusStart, DateTime.Now);
                lock (_focusEvents)
                {
                    _focusEvents.Add(completed);
                    // Keep 4 hours of history max
                    var cutoff = DateTime.Now.AddHours(-4);
                    _focusEvents.RemoveAll(e => e.End < cutoff);
                }

                _currentFocusApp   = current;
                _currentFocusStart = DateTime.Now;
            }
            catch { }
        }

        /// <summary>Returns a compact timeline of recent app usage.</summary>
        public string GetFocusTimeline(int minutes = 60)
        {
            var since = DateTime.Now.AddMinutes(-minutes);
            List<FocusEvent> snapshot;
            lock (_focusEvents)
                snapshot = _focusEvents.Where(e => e.End >= since).ToList();

            if (snapshot.Count == 0 && string.IsNullOrEmpty(_currentFocusApp)) return "";

            var sb = new StringBuilder();
            sb.AppendLine($"=== APP FOCUS (last {minutes}min) ===");
            foreach (var e in snapshot)
            {
                int secs = (int)(e.End - e.Start).TotalSeconds;
                if (secs < 5) continue; // Skip accidental focus flashes
                sb.AppendLine($"  {e.Start:HH:mm}-{e.End:HH:mm} ({secs}s) — {e.App}");
            }
            // Append currently active app
            if (!string.IsNullOrEmpty(_currentFocusApp))
            {
                int secs = (int)(DateTime.Now - _currentFocusStart).TotalSeconds;
                sb.AppendLine($"  {_currentFocusStart:HH:mm}-now  ({secs}s) — {_currentFocusApp} [active]");
            }
            return sb.ToString();
        }

        public MediaInfo? GetMediaInfo()
        {
            try
            {
                // Task.Run escapes the UI SynchronizationContext — prevents deadlock
                return Task.Run(async () =>
                {
                    var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                    var session = manager.GetCurrentSession();
                    if (session == null) return null;

                    var props = await session.TryGetMediaPropertiesAsync();
                    if (string.IsNullOrEmpty(props.Title)) return null;

                    var playback = session.GetPlaybackInfo();
                    var timeline = session.GetTimelineProperties();
                    bool isPlaying = playback?.PlaybackStatus ==
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                    return new MediaInfo(
                        Title:     props.Title,
                        Artist:    props.Artist ?? "",
                        Album:     props.AlbumTitle ?? "",
                        IsPlaying: isPlaying,
                        Position:  timeline?.Position ?? TimeSpan.Zero,
                        Duration:  timeline?.EndTime  ?? TimeSpan.Zero,
                        AppName:   session.SourceAppUserModelId ?? ""
                    );
                }).GetAwaiter().GetResult();
            }
            catch { /* No media active or WinRT unavailable */ }
            return null;
        }
    }
}

public record MediaInfo(
    string Title,
    string Artist,
    string Album,
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan Duration,
    string AppName
);