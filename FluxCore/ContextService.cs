using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation; // Ссылка: UIAutomationClient

namespace FluxCore
{
    public class ContextService
    {
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        // --- ДОБАВИЛ СЮДА, ЧТОБЫ НЕ ЗАВИСЕТЬ ОТ WINFORMS ---
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
        // --------------------------------------------------

        private IntPtr _lastHwnd = IntPtr.Zero;
        private string _lastTitle = "";

        // СЛОЙ 1: Метаданные
        public string GetLayer1_Metadata(out bool hasChanged)
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                StringBuilder sb = new StringBuilder(256);
                GetWindowText(hwnd, sb, 256);
                string title = sb.ToString();

                GetWindowThreadProcessId(hwnd, out uint pid);
                using var proc = Process.GetProcessById((int)pid);
                string processName = proc.ProcessName;

                hasChanged = (hwnd != _lastHwnd || title != _lastTitle);

                _lastHwnd = hwnd;
                _lastTitle = title;

                return $"[PROCESS: {processName}.exe] [TITLE: \"{title}\"]";
            }
            catch
            {
                hasChanged = true;
                return "[System Process]";
            }
        }

        // СЛОЙ 3: UI Иерархия (ИСПРАВЛЕНО)
        public string GetLayer3_UIHierarchy()
        {
            try
            {
                // Используем нативный метод вместо System.Windows.Forms
                GetCursorPos(out POINT p);
                var winPoint = new System.Windows.Point(p.X, p.Y);

                var element = AutomationElement.FromPoint(winPoint);
                if (element == null) return "Focus: None";

                StringBuilder hierarchy = new StringBuilder();

                AutomationElement current = element;
                int depth = 0;
                while (current != null && depth < 3)
                {
                    try
                    {
                        string name = current.Current.Name;
                        string type = current.Current.LocalizedControlType;

                        if (!string.IsNullOrWhiteSpace(name))
                            hierarchy.Insert(0, $"{type} \"{name}\" > ");
                        else
                            hierarchy.Insert(0, $"{type} > ");

                        current = TreeWalker.ControlViewWalker.GetParent(current);
                    }
                    catch { break; } // Защита от элементов, к которым нет доступа
                    depth++;
                }

                return $"[UI PATH: {hierarchy.ToString().Trim(' ', '>')}]";
            }
            catch { return "[UI: Protected Context]"; }
        }
    }
}