namespace FluxCore
{
    public class AutomationResult
    {
        public bool Success { get; }
        public string Message { get; }

        public AutomationResult(bool success, string message)
        {
            Success = success;
            Message = message;
        }
    }
}
