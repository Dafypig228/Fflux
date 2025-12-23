namespace FluxCore
{
    public class ChatMessage
    {
        public string Text { get; set; } = "";
        public bool IsUser { get; set; } // true = справа (синий), false = слева (серый)
    }
}