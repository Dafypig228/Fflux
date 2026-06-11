using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation; // Обязательно добавь ссылку на UIAutomationClient и UIAutomationTypes

namespace FluxCore
{
    public class ContextService
    {
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }

        // --- СЛОЙ 1: Имя окна ---
        public string GetLayer1_Metadata(out bool hasChanged)
        {
            hasChanged = false;
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                StringBuilder sb = new StringBuilder(256);
                GetWindowText(hwnd, sb, 256);
                return $"ACTIVE WINDOW: [{sb}] (HWND: {hwnd})";
            }
            catch { return "UNKNOWN WINDOW"; }
        }

        // --- СЛОЙ 3: ПОЛНОЕ ДЕРЕВО ИНТЕРФЕЙСА (DEEP SCAN) ---
        public string GetLayer3_UIHierarchy()
        {
            try
            {
                GetCursorPos(out POINT p);
                var element = AutomationElement.FromPoint(new System.Windows.Point(p.X, p.Y));

                if (element == null) return "NO UI ELEMENT";

                StringBuilder dump = new StringBuilder();
                dump.AppendLine($"--- FOCUSED ELEMENT ---");
                dump.AppendLine($"Type: {element.Current.LocalizedControlType}");
                dump.AppendLine($"Name: \"{element.Current.Name}\"");
                dump.AppendLine($"ID: {element.Current.AutomationId}");
                dump.AppendLine($"Value: {GetTextPattern(element)}");

                // СКАНИРУЕМ РОДИТЕЛЕЙ (ПУТЬ НАВЕРХ)
                dump.AppendLine($"\n--- HIERARCHY PATH ---");
                var walker = TreeWalker.ControlViewWalker;
                var parent = walker.GetParent(element);
                int depth = 0;

                while (parent != null && depth < 5) // Ограничим глубину, чтобы не зависло
                {
                    dump.Insert(0, $"{parent.Current.LocalizedControlType} [\"{parent.Current.Name}\"] > \n");
                    parent = walker.GetParent(parent);
                    depth++;
                }

                // СКАНИРУЕМ СОСЕДЕЙ (ЧТО РЯДОМ)
                dump.AppendLine($"\n--- SURROUNDING ELEMENTS (SIBLINGS) ---");
                var parentNode = walker.GetParent(element);
                if (parentNode != null)
                {
                    var child = walker.GetFirstChild(parentNode);
                    int count = 0;
                    while (child != null && count < 20) // Максимум 20 соседей
                    {
                        string marker = (child == element) ? " <--- [CURSOR]" : "";
                        dump.AppendLine($" - {child.Current.LocalizedControlType}: \"{child.Current.Name}\"{marker}");
                        child = walker.GetNextSibling(child);
                        count++;
                    }
                }

                return dump.ToString();
            }
            catch (Exception ex)
            {
                return $"UI SCAN ERROR: {ex.Message}";
            }
        }

        private string GetTextPattern(AutomationElement el)
        {
            if (el.TryGetCurrentPattern(ValuePattern.Pattern, out object p)) return ((ValuePattern)p).Current.Value;
            if (el.TryGetCurrentPattern(TextPattern.Pattern, out object t)) return "Text Content Available";
            return "No Value";
        }
    }
}