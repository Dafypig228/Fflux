using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

namespace FluxCore
{
    public class ClipboardEntry
    {
        public string Content { get; set; } = "";
        public string Type    { get; set; } = "text"; // text | image | files
        public string App     { get; set; } = "";
        public DateTime When  { get; set; }
    }

    public class ClipboardService : IDisposable
    {
        [DllImport("user32.dll")] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        private const int WM_CLIPBOARDUPDATE = 0x031D;
        private const int MAX_ENTRIES = 20;

        private IntPtr _hwnd = IntPtr.Zero;
        private readonly List<ClipboardEntry> _history = new();
        private readonly SensoryCortex? _cortex;

        /// <summary>Optional — set after construction to persist events to the data lake.</summary>
        public DataLakeService? DataLake { get; set; }

        public ClipboardService(SensoryCortex? cortex = null)
        {
            _cortex = cortex;
        }

        /// <summary>
        /// Call once from MainWindow after window is loaded.
        /// Registers for WM_CLIPBOARDUPDATE on the main HWND.
        /// </summary>
        public void Attach(IntPtr hwnd)
        {
            _hwnd = hwnd;
            AddClipboardFormatListener(hwnd);
        }

        /// <summary>
        /// Add this as a hook to the main window's HwndSource.
        /// </summary>
        public IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_CLIPBOARDUPDATE)
                CaptureClipboard();
            return IntPtr.Zero;
        }

        private void CaptureClipboard()
        {
            try
            {
                var entry = new ClipboardEntry
                {
                    When = DateTime.Now,
                    App  = _cortex?.GetActiveWindow() ?? ""
                };

                var data = System.Windows.Clipboard.GetDataObject();
                if (data == null) return;

                if (data.GetDataPresent(System.Windows.DataFormats.Text))
                {
                    var text = System.Windows.Clipboard.GetText();
                    if (string.IsNullOrWhiteSpace(text)) return;
                    entry.Type    = "text";
                    entry.Content = text.Length > 500 ? text[..500] + "…" : text;
                }
                else if (data.GetDataPresent(System.Windows.DataFormats.FileDrop))
                {
                    var files = (string[])data.GetData(System.Windows.DataFormats.FileDrop);
                    entry.Type    = "files";
                    entry.Content = string.Join(", ", files ?? Array.Empty<string>());
                }
                else if (data.GetDataPresent(System.Windows.DataFormats.Bitmap))
                {
                    entry.Type    = "image";
                    entry.Content = "[Image]";
                }
                else return;

                lock (_history)
                {
                    // Skip identical consecutive copies
                    if (_history.Count > 0 && _history[^1].Content == entry.Content) return;
                    _history.Add(entry);
                    if (_history.Count > MAX_ENTRIES) _history.RemoveAt(0);
                }

                // Persist to data lake (outside lock to avoid nesting)
                DataLake?.Write("clipboard", entry.Content,
                    new { app = entry.App, type = entry.Type });
            }
            catch { }
        }

        public string GetRecentClipboard(int count = 5)
        {
            lock (_history)
            {
                if (_history.Count == 0) return "";
                var sb = new StringBuilder();
                sb.AppendLine("=== CLIPBOARD HISTORY ===");
                foreach (var e in _history.TakeLast(count))
                {
                    sb.AppendLine($"  [{e.When:HH:mm}] [{e.Type}] in:{e.App}");
                    sb.AppendLine($"    {e.Content}");
                }
                return sb.ToString();
            }
        }

        public void Dispose()
        {
            if (_hwnd != IntPtr.Zero)
                RemoveClipboardFormatListener(_hwnd);
        }
    }
}
