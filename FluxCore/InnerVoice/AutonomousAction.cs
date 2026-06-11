namespace FluxCore.InnerVoice
{
    public enum ActionType { Idle, Message, Research, Opinion, Memory }

    public class AutonomousAction
    {
        public ActionType Type          { get; init; }
        public string?    Text          { get; init; } // MESSAGE body / IDLE private thought
        public string?    Topic         { get; init; } // RESEARCH topic / OPINION topic
        public float      OpinionDelta  { get; init; } // OPINION delta (-0.2 to +0.2)
        public string?    Reason        { get; init; } // OPINION one-sentence reason
        public string?    MemoryBlock   { get; init; } // MEMORY: "core" or "working"
        public string?    MemoryContent { get; init; } // MEMORY: what to update

        public static AutonomousAction Idle(string thought = "") =>
            new() { Type = ActionType.Idle, Text = thought };
    }
}
