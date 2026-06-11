using System;
using System.Runtime.InteropServices;
using System.Windows.Automation; // Нужно добавить ссылку на UIAutomationClient и UIAutomationTypes

namespace FluxCore
{
    public class MouseService
    {
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out System.Drawing.Point lpPoint);

        public string GetElementUnderMouse()
        {
            try
            {
                if (GetCursorPos(out var point))
                {
                    // Ищем элемент UI прямо под курсором
                    AutomationElement element = AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y));
                    if (element == null) return "Пустое место";

                    var info = element.Current;
                    // Собираем инфу: Тип элемента (кнопка/текст) + его название
                    return $"Тип: {info.LocalizedControlType}, Имя: \"{info.Name}\", Описание: \"{info.HelpText}\"";
                }
            }
            catch { return "Системный элемент (защищен)"; }
            return "Неизвестно";
        }
    }
}