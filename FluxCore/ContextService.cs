using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation; // Ссылка: UIAutomationClient

namespace FluxCore
{
    public class ContextService
    {
        // Метаданные окна
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        // Мышь
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }

        public string GetAppMetadata()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                StringBuilder sb = new StringBuilder(256);
                GetWindowText(hwnd, sb, 256);
                GetWindowThreadProcessId(hwnd, out uint pid);
                using var proc = Process.GetProcessById((int)pid);
                return $"[App: {proc.ProcessName}.exe | Window: \"{sb}\"]";
            }
            catch { return "[Unknown App]"; }
        }

        public string GetElementUnderMouse()
        {
            try
            {
                GetCursorPos(out POINT p);
                var element = AutomationElement.FromPoint(new System.Windows.Point(p.X, p.Y));
                if (element == null) return "Ничего";

                var info = element.Current;
                // Собираем всё что есть: имя, тип, помощь
                string details = $"{info.LocalizedControlType} \"{info.Name}\"";
                if (!string.IsNullOrEmpty(info.HelpText)) details += $" ({info.HelpText})";

                return details;
            }
            catch { return "Системный/Защищенный элемент"; }
        }
    }
}