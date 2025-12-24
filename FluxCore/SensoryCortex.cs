using System;
using System.Drawing; // Нужен для Bitmap
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices; // Для WinAPI
using System.Text;
using System.Windows; // Для SystemParameters (WPF)
using System.Windows.Automation; // Для UI Automation

namespace FluxCore
{
    public class SensoryCortex
    {
        // --- WINAPI ИМПОРТЫ (Чтобы не зависеть от WinForms) ---
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }
        // -----------------------------------------------------

        private string _lastScreenHash = "";

        // --- 1. ВИЗУАЛЬНЫЙ ЗАХВАТ (Глаза) ---
        public string CaptureScreenBase64(out bool hasVisualChange)
        {
            try
            {
                // Используем WPF SystemParameters вместо WinForms Screen
                int width = (int)SystemParameters.PrimaryScreenWidth;
                int height = (int)SystemParameters.PrimaryScreenHeight;

                using var bmp = new Bitmap(width, height);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(0, 0, 0, 0, bmp.Size);
                }

                // Быстрый расчет хеша (Delta-анализ)
                string currentHash = GetImageHash(bmp);
                hasVisualChange = currentHash != _lastScreenHash;
                _lastScreenHash = currentHash;

                if (!hasVisualChange) return ""; // Экран спит

                // Конвертация в JPEG
                using var ms = new MemoryStream();
                var encoderParams = new EncoderParameters(1);
                encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 75L);
                var codec = GetEncoderInfo("image/jpeg");
                bmp.Save(ms, codec, encoderParams);
                return Convert.ToBase64String(ms.ToArray());
            }
            catch
            {
                hasVisualChange = false;
                return "";
            }
        }

        // --- 2. РЕНТГЕН (UI Automation) ---
        public string ScanActiveWindowDeep()
        {
            try
            {
                var element = AutomationElement.FocusedElement;
                if (element == null) return "[No Focus]";

                // Поднимаемся до уровня окна
                var walker = TreeWalker.ControlViewWalker;
                var window = element;
                while (window != null && window.Current.ControlType != ControlType.Window)
                {
                    window = walker.GetParent(window);
                }
                if (window == null) window = element;

                StringBuilder sb = new StringBuilder();
                sb.AppendLine($"[WINDOW: {window.Current.Name}]");

                // Рекурсивное сканирование
                ScanRecursive(window, sb, 0);

                // Добавляем точный фокус мыши через WinAPI
                GetCursorPos(out POINT p);
                var mouseElem = AutomationElement.FromPoint(new System.Windows.Point(p.X, p.Y));

                if (mouseElem != null)
                {
                    try
                    {
                        sb.AppendLine($"\n>>> MOUSE FOCUS: {mouseElem.Current.LocalizedControlType} \"{mouseElem.Current.Name}\" <<<");
                    }
                    catch { sb.AppendLine("\n>>> MOUSE FOCUS: [Protected Element] <<<"); }
                }

                return sb.ToString();
            }
            catch { return "[UI Scan Error]"; }
        }

        private void ScanRecursive(AutomationElement root, StringBuilder sb, int depth)
        {
            if (depth > 2) return;
            try
            {
                // 🔥 ИСПРАВЛЕНИЕ: Пишем System.Windows.Automation.Condition явно
                var children = root.FindAll(TreeScope.Children, System.Windows.Automation.Condition.TrueCondition);

                foreach (AutomationElement child in children)
                {
                    var info = child.Current;
                    if (!string.IsNullOrWhiteSpace(info.Name))
                    {
                        string indent = new string('-', depth * 2);
                        sb.AppendLine($"{indent} {info.LocalizedControlType}: {info.Name}");
                    }
                    ScanRecursive(child, sb, depth + 1);
                }
            }
            catch { }
        }

        private string GetImageHash(Bitmap bmp)
        {
            // Простейший хеш по 3 точкам
            int w = bmp.Width, h = bmp.Height;
            // Проверка на 0 размер, чтобы не упало
            if (w == 0 || h == 0) return "";

            var sb = new StringBuilder();
            sb.Append(bmp.GetPixel(w / 2, h / 2).ToString());
            sb.Append(bmp.GetPixel(10, 10).ToString());
            sb.Append(bmp.GetPixel(w - 10, h - 10).ToString());
            return sb.ToString();
        }

        private static ImageCodecInfo GetEncoderInfo(string mimeType)
        {
            var encoders = ImageCodecInfo.GetImageEncoders();
            foreach (var e in encoders) if (e.MimeType == mimeType) return e;
            return encoders[0];
        }
    }
}