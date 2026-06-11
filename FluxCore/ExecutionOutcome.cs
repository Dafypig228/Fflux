namespace FluxCore
{
    public class ExecutionOutcome
    {
        public bool Success { get; }
        public string Message { get; }

        public ExecutionOutcome(bool success, string message)
        {
            Success = success;
            Message = message;
        }
    }
}
