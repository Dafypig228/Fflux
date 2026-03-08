# FluxCore Prompt System — Fix Instructions

## Project Context
This is a C# WPF AI assistant called "Davos" (FluxCore). The prompt system spans multiple files. Key files:
- `JarvisCore.cs` — Main task execution system prompt (`_staticInstruction`, lines 148-279)
- `JarvisCore.Context.cs` — Per-step dynamic context builder (`BuildDynamicContext`)
- `FluxBrain.cs` — Intent classifier prompt (line 223) + Chat prompt (line 342)
- `ReflectionAgent.cs` — Failure analysis prompt
- `OrchestratorAgent.cs` — **Deprecated, to be deleted**

## Fixes To Apply

### 1. Restructure `_staticInstruction` with XML sections (JarvisCore.cs)
Wrap the system prompt in clear XML-tagged sections: `<identity>`, `<tools>`, `<rules priority="critical">`, `<rules>`, `<examples>`, `<forbidden>`, `<paths>`. This helps Gemini index sections and reduces instruction-following failures. Keep the same content, just add structure.

### 2. Deduplicate rules across prompts
The rule "use CLICK:x,y coordinates, not names" appears in BOTH `_staticInstruction` AND `BuildDynamicContext` (line 51). Remove it from `BuildDynamicContext` — the static instruction already covers it. Apply this principle to any rule that appears in both static and dynamic context.

### 3. Fix chat NEED_REROUTE anti-pattern (FluxBrain.cs)
In `HandleChatAsync` (line 342-369), the chat prompt says "NEVER output commands" but then asks the LLM to output `NEED_REROUTE`. Fix by:
- Remove the NEED_REROUTE instruction from the chat system prompt entirely
- Remove the `NEED_REROUTE` detection block in `HandleChatAsync` (lines 390-395)
- The classifier in `FluxBrain.ClassifyIntentAsync` already handles routing correctly — trust it

### 4. Delete OrchestratorAgent.cs
This file is fully superseded by `FluxBrain`. It uses hardcoded keyword arrays in `ShouldUsePlanning()` — the exact anti-pattern `FluxBrain` correctly avoids. Delete the file and remove any references to it.

### 5. Add recovery alternatives in BuildDynamicContext (JarvisCore.Context.cs)
After the failures section (around line 41), add a conditional block when `failures.Count >= 3`:
```csharp
if (failures.Count >= 3)
{
    sb.AppendLine("\n💡 ALTERNATIVE STRATEGIES (pick one you haven't tried):");
    sb.AppendLine("  1. Use [[RUN_SHELL:...]] instead of UI clicks");
    sb.AppendLine("  2. Use [[KEYS:TAB]] + [[KEYS:ENTER]] for keyboard navigation");
    sb.AppendLine("  3. [[SCROLL:down]] to reveal hidden elements");
    sb.AppendLine("  4. [[OPEN_APP:...]] to refocus the correct window");
}
```

### 6. Remove screenshot from ReflectionAgent (ReflectionAgent.cs)
In `AnalyzeFailureAsync`, remove the screenshot parameter from the LLM call and remove "Look at the screenshot" from the prompt. The JSON schema (alternative/retry/abort) doesn't use visual data anyway, and `ValidatorAgent` already handles visual verification separately.

## Rules for These Changes
- Do NOT change any command execution logic, only prompt text and dead code removal
- Preserve all existing command types and their parsing in `ExtractAllCommands`
- Keep temperature values as they are (0.2 task, 0.7 chat, 0.1 classifier, 0.3 reflexion)
- Test that the project still builds after changes (`dotnet build`)
