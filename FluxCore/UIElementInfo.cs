using System.Windows.Automation;

namespace FluxCore
{
    public class UIElementInfo
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public string AutomationId { get; set; } = "";
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public AutomationElement? Element { get; set; }
    }
}
