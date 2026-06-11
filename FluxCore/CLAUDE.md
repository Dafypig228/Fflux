# FluxCore ("Davos") — guide for AI sessions

C# WPF (.NET 10) AI companion on Windows. Gemini 2.5 Flash backend.
This file states the **invariants**. If code contradicts this file, the code wins — then fix this file.
When a session changes an invariant, it MUST update this file in the same change.

## Architecture (the only live pipeline)

```
User → FluxBrain (chat + Gemini native function calling; routes via execute_pc_task / commitment_add / inject_task_context)
     → JarvisCore.ExecuteTaskAsync (step loop: screenshot + UIA elements → LLM → [[COMMAND:arg]] execution)
     → WindowsAutomationAgent (CLICK/TYPE/KEYS/SCROLL) | CodeExecutionAgent (RUN_SHELL/PYTHON/RUN_CSHARP) | ExecutionAgent (OPEN_APP, Playwright)
```

There is exactly ONE command pipeline (JarvisCore). A legacy duplicate in
MainWindow.Permissions.cs was deleted (see `.cleanup-backup\`). Do not add a second one.

## Invariants — breaking these reintroduces fixed bugs

1. **Command registry must stay in sync in 3 places** (a mismatch = silent failure):
   - advertised: `_staticInstruction` `<tools>` in JarvisCore.cs
   - parsed: `ExtractAllCommands` in JarvisCore.Context.cs
   - executed: `ExecuteSingleCommandAsync` switch in JarvisCore.Commands.cs
   Every advertised command MUST have an executor case (RESPOND was once advertised but not executable — every "final answer" was lost).

2. **Coordinate spaces**: the AI thinks in SCREENSHOT SPACE (50% of logical pixels).
   Click handlers multiply incoming coords ×2. ANY coordinate shown to the AI
   (element lists, error messages, success messages) must be ÷2. No DPI math anywhere.

3. **Completion protocol**: `TASK_COMPLETE` / `TASK_FAILED` are TEXT FLAGS (not `[[...]]` commands),
   detected in the ACTION section. They are different outcomes — never merge them.
   `[[RESPOND:text]]` is the model's final answer; a RESPOND-only step auto-completes.
   All terminal returns in ExecuteTaskAsync go through `FinalizeTask()` (unlock, trace,
   reinforce, reflexion, OnResponse). Early returns that skip it must call `UnlockTarget()`.

4. **Messaging**: FluxBrain owns user-facing chat messages (OnMessage). JarvisCore's
   OnResponse is HUD-reset only in MainWindow — do NOT also AddMessage there (duplicates).

5. **Gemini calls**: `thinkingConfig.thinkingBudget = 0` in GeminiService payloads.
   Removing it re-enables hidden thinking and makes every step take seconds longer.

6. **Memory hygiene**: Hippocampus lessons are only learned from CLICK/OPEN_APP failures,
   never from script failures (that poisoned memory with "use CLICK instead of shell" garbage).
   Recall has a 0.2 confidence floor.

7. **Visual validation** runs on SUCCESSFUL screen commands (catching silent failures),
   never on failed ones. Depths: Fast = off, Normal = CLICK/TYPE, Thorough = all screen commands.

8. **Retries**: script commands (RUN_SHELL/PYTHON/RUN_CSHARP) execute ONCE — no blind re-runs
   (side effects + 60s timeout each). Retries are for UI commands only. No hidden UI mutations
   (e.g. scrolling) inside retry logic — the AI decides scrolling explicitly.

9. **Secrets**: Gemini key comes from `%APPDATA%\Davos\settings.json` (`GeminiApiKey`)
   or `GEMINI_API_KEY` env var. NEVER hardcode keys in source — it has leaked twice already.

10. **Grounding**: if the model needs a capability that doesn't exist, the fix is to ADD a real
    capability (service + prompt doc in `<tools>`/`<grounding>`), not prompt exhortations.
    Chrome pages: `Chrome.GetRecentPages()` (extension bridge). Chrome has no COM API.
    Proven screen route for tabs: CTRL+SHIFT+A (Chrome tab-search overlay).

## Known intentionally-dead / aspirational areas

- `Swarm\` is reachable only via `FluxBrain.ExecuteAutonomousResearchAsync` (InnerVoice).
- `SelfCoding\`, `OrchestratorAgent.cs`, `ChatAssistant.cs`, `Architecture.cs` were deleted
  (unreachable); restore from `.cleanup-backup\` only with a real entry point.
- The C++ ImGui overlay (FluxCore.vcxproj) is a separate experiment, not part of the WPF app.

## Verify after changes

```
dotnet build FluxCore\FluxCore.csproj
```

Build must be 0 errors. There are no tests yet — if you add protocol logic, add a test
that walks every advertised command through parse + execute.
