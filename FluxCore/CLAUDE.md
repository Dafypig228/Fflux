# FluxCore ("Davos") — guide for AI sessions

C# WPF (.NET 10, x64) AI companion on Windows. Persona: **Davos** — a computer friend that
talks like a person and operates the PC (chat, automation, scripting, sensors, memory).
Backend: **Gemini 2.5 Flash** via REST (`GeminiService`). Owner: Adil (C++ is his primary
language; this project is C#).

This file states the **invariants and the project's true state**. If code contradicts this
file, the code wins — then fix this file. When a session changes an invariant, it MUST
update this file in the same change. Do NOT trust README.md — it is outdated marketing text.

---

## ⚠ FIRST THING EVERY SESSION: check for overwrites

Adil sometimes copies his master project folder over this one, which **silently reverts
all fixes** (this happened 2026-06-11 — two sessions of fixes were wiped and re-applied).
Before any work, run this marker check:

```
grep -l "thinkingBudget" FluxCore/GeminiService.cs      → must match
grep -l "FinalizeTask"   FluxCore/JarvisCore.cs          → must match
grep -l "case \"RESPOND\"" FluxCore/JarvisCore.Commands.cs → must match
```

If any marker is missing → the project was overwritten again. Recovery: the fixed state
is committed in the **project-local git repo** (checkpoint `b2cfc1c`,
"all 26 bug fixes re-applied"). Run `git status` / `git diff` to see what the copy
changed, and restore fixes from git rather than re-deriving them.

---

## Architecture (the only live pipeline)

```
User input (text/voice)
  → FluxBrain          chat brain + router. ONE Gemini call with native function calling:
                       execute_pc_task → launches JarvisCore
                       commitment_add  → CommitmentStore (deferred actions, fires via RunAgentLoopAsync)
                       inject_task_context → forwards correction into a running task
                       (no tool call)  → conversational reply
  → JarvisCore.ExecuteTaskAsync
                       step loop (max 30): screenshot(50%) + UIA elements + passive context
                       → Gemini → THOUGHT/ACTION/[[COMMAND:arg]]/CONFIDENCE → execute → repeat
  → executors:
       WindowsAutomationAgent  CLICK/TYPE/KEYS/SCROLL/DRAG/WINDOW  (UIA + SendInput)
       CodeExecutionAgent      RUN_SHELL/PYTHON/RUN_CSHARP/START_BACKGROUND/READ_LOG  (60s timeout)
       ExecutionAgent          OPEN_APP, Playwright browser (separate browser, rarely used)
```

**Key files**
| File | Role |
|---|---|
| `FluxBrain.cs` | router, chat persona prompt, task lifecycle, RunningTask tracking |
| `JarvisCore.cs` | `_staticInstruction` (system prompt: identity/tools/grounding/rules), main step loop, `FinalizeTask` |
| `JarvisCore.Commands.cs` | `ExecuteWithRetryAsync` (retry policy), `ExecuteSingleCommandAsync` (command switch) |
| `JarvisCore.Context.cs` | `BuildDynamicContext` (per-step context + passive sensors), `ExtractAllCommands` (parser) |
| `JarvisCore.Response.cs` | `PerformReflexionAsync` (lesson extraction), `GenerateNaturalResponse` |
| `GeminiService.cs` | REST calls, `thinkingBudget=0`, `ChatWithAgentToolsAsync` (function calling) |
| `ValidatorAgent.cs` | `ValidateVisualAsync` — post-action screenshot verification |
| `Hippocampus.cs` | lesson memory (knowledge.json), confidence-scored recall |
| `SensoryCortex.cs` | UIA element scan (÷2 coords), active window, focus timeline, media |
| `ChromeBridgeService.cs` | localhost:27834 receiver for Chrome/VSCode extensions; `GetRecentPages()` |
| `ScriptGlobals.cs` | services injected into RUN_CSHARP: Telegram, DataLake, Http, Settings, KnowledgeGraph, Memory, Gemini, Chrome |
| `MainWindow.*.cs` | UI, service wiring (`InitializeServices`), settings, permission dialogs |
| `DataLakeService` / `KnowledgeGraphService` / `MemoryService` / `CoreMemoryService` | event log / entity graph / vector memory / persona memory |
| `InnerVoice\` | autonomous companion loop (Telegram messages, drives) |
| `Swarm\` | multi-agent system; reachable ONLY via `ExecuteAutonomousResearchAsync` (InnerVoice) |

---

## Invariants — breaking these reintroduces fixed bugs

1. **Command registry sync (3 places)** — a mismatch is a silent failure:
   - advertised: `_staticInstruction` `<tools>` (JarvisCore.cs)
   - parsed: `ExtractAllCommands` (JarvisCore.Context.cs)
   - executed: `ExecuteSingleCommandAsync` switch (JarvisCore.Commands.cs)
   Every advertised command MUST have an executor case. (RESPOND was advertised+parsed but
   not executable → every final answer died as "Unknown command: RESPOND".)

2. **Coordinate spaces**: the AI thinks in SCREENSHOT SPACE = 50% of logical pixels.
   Click handlers multiply incoming coords ×2 (`WindowsAutomationAgent.Click.cs`).
   ANY coordinate shown to the AI (element lists, AMBIGUOUS/not-found errors, "Clicked at"
   messages) must be ÷2. No DPI math anywhere in the pipeline.

3. **Completion protocol**: `TASK_COMPLETE` / `TASK_FAILED` are TEXT FLAGS on their own
   line (never inside `[[...]]`), detected only in the ACTION section. They are DIFFERENT
   outcomes — never OR them together (the old code reported failed tasks as "Task Completed").
   `[[RESPOND:text]]` = the model's final answer; a RESPOND-only step auto-completes;
   `lastRespondText` is preferred over template text. ALL terminal returns in
   `ExecuteTaskAsync` go through `FinalizeTask()` (unlock, task_trace, reinforce,
   fire-and-forget reflexion, OnResponse). Early returns (REJECT/cancel/safety-stop)
   must call `_automation.UnlockTarget()`.

4. **Messaging ownership**: FluxBrain owns user-facing chat (OnMessage → StreamMessage).
   JarvisCore's OnResponse handler in MainWindow is HUD-reset ONLY — adding AddMessage
   there duplicates every task result. OnShowWindow also resets the HUD (covers
   REJECT/cancel paths that never fire OnResponse).

5. **Gemini speed**: `generationConfig.thinkingConfig.thinkingBudget = 0` in BOTH payloads
   in GeminiService (SendPayload + ChatWithAgentToolsAsync). Without it, Gemini 2.5 Flash
   "thinks" on every call — the single largest latency source (seconds per step × 30 steps).

6. **Memory hygiene**: Hippocampus lessons are learned ONLY from CLICK/OPEN_APP failures.
   NEVER store lessons from script failures (it poisoned memory with "use CLICK:x,y instead"
   advice for PowerShell commands). `Recall()` has a 0.2 confidence floor.

7. **Visual validation** (`shouldVerify` in JarvisCore.cs) runs on SUCCESSFUL screen
   commands to catch silent failures — never on failed ones (pointless). Depths:
   Fast = off, Normal (default) = CLICK/TYPE only, Thorough = all screen commands.
   On VISUAL_FAIL: downgrade outcome AND remove the entry from successfulActions.
   `ValidateVisualAsync` sends ONE (after) image, honest prompt, regex-tolerant verdict
   parsing, inconclusive → SUCCESS (false FAILURE poisons the loop).

8. **Retry policy** (`ExecuteWithRetryAsync`): script commands (RUN_SHELL/PYTHON/RUN_CSHARP/
   START_BACKGROUND...) execute ONCE — blind re-runs repeat side effects and waste up to
   60s each. Retries only for UI commands (CLICK handles its own). NO hidden UI mutations
   in retry logic (the old code scrolled the page on click-retry, invalidating all
   coordinates). `GetExpectedApp` returns "" when unknown — never default to "chrome".

9. **Secrets**: Gemini key comes from `%APPDATA%\Davos\settings.json` (`GeminiApiKey`) or
   `GEMINI_API_KEY` env var, resolved by `MainWindow.ResolveGeminiApiKey()`. NEVER hardcode
   keys in source — it has leaked TWICE already (two different keys committed to source).

10. **Grounding beats prompting**: if the model needs a capability that doesn't exist,
    ADD a real one (service + doc in `<tools>`/`<grounding>`), don't add exhortations.
    Chrome pages: `Chrome.GetRecentPages()` (extension bridge; recent browsing, not tabs).
    Proven screen route for live tabs: CTRL+SHIFT+A (Chrome tab-search overlay), read
    from screenshot, ESC. Chrome has NO COM API; Shell.Application ≠ browser tabs.

11. **Loop detection**: identical actions are blocked at 3rd repeat EXCEPT
    `RepeatTolerantCommands` (SCROLL/WAIT/READ_LOG/CHECK_BACKGROUND/LOG) — those
    legitimately repeat (scrolling lists, polling bot logs). KEYS is WARN-ONLY
    (executed, never blocked): pressing ENTER/TAB at different moments is normal;
    hard-blocking it forced absurd workarounds (CAPTCHA trace 2026-06-12).

12. **Parser**: script args in `ExtractAllCommands` are bounded at the next `[[COMMAND:`
    opener — without the bound, the "last ]]\n" fallback swallowed following commands
    into script bodies while they ALSO executed separately.

13. **Output truncation keeps HEAD + TAIL, never cuts the end** (`OutputTrim.Middle`).
    A script prints its conclusion LAST. There are TWO truncation layers: CodeExecutionAgent
    (5000 chars) and JarvisCore step context (1500 chars — this is what the model actually
    sees). Head-only truncation at either layer hides the script's final result and the
    model fabricates it (2026-06-12 memory-task transcript: invented "913.00 MB" + an
    unsupported growth verdict, then TASK_COMPLETE). `lastDataOutput` keeps the TAIL.
    The `<grounding>` section documents the marker; keep them in sync.

17. **Robust foreground activation** (`ForceForegroundWindow` / `ForceFocus` / `EnsureFocusAsync`).
    Bare `SetForegroundWindow` is SILENTLY IGNORED when another process owns the foreground
    (overlays — Overwolf/NVIDIA/PWGood — or the focus-lock timeout). That made Telegram/Steam
    stay behind, so Davos typed blind into the wrong window and declared "opened X" / success it
    never achieved (traces 2026-06-13 20:xx). The proven recipe: SPI_FOREGROUNDLOCKTIMEOUT→0 +
    AllowSetForegroundWindow(ANY) + AttachThreadInput(our thread ↔ BOTH foreground AND target
    threads) + ShowWindow + BringWindowToTop + SetForegroundWindow, ALWAYS detaching in finally
    (a leaked attach hangs us if the target stops responding). `EnsureFocusAsync` (pre click/type)
    now returns whether focus actually took; after OPEN_APP the loop verifies frontmost and warns
    the model if not — so it stops assuming focus. Research: AttachThreadInput is THE missing piece
    (west-wind/oldnewthing). Do NOT regress to bare SetForegroundWindow.

16. **Registering a command touches 4 sites — miss one and it fails differently:**
    `KnownCommandTypes` (parser registry; miss → silently dropped, model loops "no command"),
    the dispatch switch in `ExecuteSingleCommandAsync` (miss → "Unknown command"),
    the `<tools>` prompt (miss → model never uses it), and `ScreenCommands` if it touches the
    screen (miss → wrong access-gating/retry). Unknown `[[TOKEN:...]]` now fails LOUD via
    `DetectUnknownCommand`. (FIND_AND_CLICK shipped missing the registry, 2026-06-13.)

15. **Vision-grounded click** (`VisionGroundedClickAsync`, cmd `[[FIND_AND_CLICK:desc]]`).
    The router's fallback for targets with NO accessibility tree (Telegram desktop, games,
    canvas/WebGL UIs) where the UIA element list is empty and pixel-guessing fails. Asks the
    vision LLM to locate the described element and clicks it. CRITICAL invariant: the model
    returns **NORMALIZED coords (0–1000)**, never pixels — it may re-tile the screenshot to an
    arbitrary size, so pixel coords would silently mis-click ("screenshots are not equal size").
    Mapping reads LIVE geometry: `SensoryCortex.GetScreenLogicalSize()` (full) → ×½ = screenshot
    space → existing CLICK path ×2 = logical. GetScreenBase64 halves the SAME bounds — keep the
    capture scale and this consumer paired. Single-shot (no 3× retry — each miss = one vision call).
    This is ONE layer of the planned method-router (API→UIA→vision→coords); speculative batching
    and UIA `SetValue` typing are NOT built yet. Research basis: UFO²/OmniParser hybrid, RPA
    fallback cascades (CHAT-HISTORY session 5).

14. **Script evidence honesty** (`RunProcessAsync` / `RunPowerShellAsync`):
    - PS 5.1 defaults `Out-File`/`>` to UTF-16 LE → Python reads NUL-riddled garbage.
      The utf8Prefix forces `$PSDefaultParameterValues['Out-File:Encoding']='utf8'` (+
      Set-Content/Add-Content/Export-Csv). Result is UTF-8 WITH BOM → Python side must use
      `encoding='utf-8-sig'` (taught in `<grounding>`). Verified empirically on this machine.
    - Nonzero exit code is ALWAYS a failure (old code needed non-empty stderr too, so a
      silent `exit 1` reported success). Failure messages include STDOUT — pre-crash prints
      are evidence. Exit-0 stderr is surfaced labeled (CLIXML noise filtered).
    - `PYTHONIOENCODING=utf-8` is set for all spawned processes.

---

## Catalog of the 26 fixed bugs (for re-verification after any overwrite)

Speed: thinking-mode off (#5) · strategy pre-call folded into step-1 context · reflexion
fire-and-forget · no 3× script re-runs (#8).
Protocol: RESPOND executor case · TASK_FAILED ≠ TASK_COMPLETE · single FinalizeTask exit ·
unlock on REJECT/cancel/safety-stop · RESPOND-only auto-complete without length gate ·
lastRespondText preferred (#3).
Verification: validation logic un-inverted (#7) · honest single-image validator prompt ·
tolerant verdict regex · ValidationDepth wired from settings.
Memory: no lessons from script failures (#6) · 0.2 confidence recall floor.
Clumsiness: ÷2 coords in all AI-facing messages (#2) · no scroll-on-retry (two places, #8) ·
no chrome-default refocus (#8) · no duplicate chat messages (#4) · HUD reset via OnShowWindow ·
repeat-tolerant loop detection (#11) · parser boundary (#12) · "ok " prefix fix in
GenerateNaturalResponse · dead DescribeActionNaturally branches.
Grounding: Chrome.GetRecentPages + ScriptGlobals.Chrome + `<grounding>` prompt section +
CTRL+SHIFT+A route (#10).
Security: hardcoded key removed → settings/env (#9).
Session 4 (2026-06-12, transcript-driven): head+tail truncation at both layers + tail-keep
lastDataOutput (#13) · PS file writes forced UTF-8 + PYTHONIOENCODING (#14) · nonzero exit =
failure, stdout in failures, exit-0 stderr surfaced (#14) · grounding: encodings + truncation
marker semantics + "never state a result you did not see".

**Task traces for debugging** (`WriteTraceSnapshot`, INCREMENTAL since 2026-06-13): one stable
file per task `%APPDATA%\Davos\traces\trace_yyyyMMdd_HHmmss.md`, rewritten at the TOP of every
step and on crash/cancel — so a HUNG, CRASHED, in-progress, or cancelled task ALL leave a
readable trace, not only completed ones (a hung task can be read LIVE mid-run; the OUTCOME line
shows `IN PROGRESS (on step N)` / `CRASHED:` / `INTERRUPTED` / `COMPLETED`/`FAILED`). The verbatim
model dialogue is post-truncation — exactly what the model saw. When analyzing a run, grep THAT
file — do not paste UI logs into AI sessions (2-3x duplicated, model-side context missing) and
do not trust third-party AI summaries of logs (a Gemini summary fabricated a trace JSON on
2026-06-12). CAVEAT: long tasks compact early history mid-run, so traces show the final steps +
a summary line; for full forensics on 10+ step tasks use the UI log (FluxDebug.txt) as well.

Session 4b (2026-06-12, CAPTCHA + desktop traces): KEYS loop detection is WARN-ONLY
(hard block forced pathological workarounds; mirrors CLICK-near-loop design) ·
`[[CLICK:n]]` resolves element [n] from the current step's numbered list to coordinates
(`ResolveElementIndex`; the model used indices naturally and the harness rejected them) ·
element names in AMBIGUOUS/not-found errors capped at 60 chars (web elements carry whole
paragraphs as names → multi-KB errors) · step budget shown each step ([Step n/30]) +
wrap-up warning in the last 5 steps (model burned 9 steps post-futility with no honest
TASK_FAILED because it didn't know steps were finite).
Known open issues (judgment layer, NOT harness): declares success on assumption without
cheap verification (desktop trace: "I will assume... CONFIDENCE: 1.0"); bulk file moves
bury desktop shortcuts (.lnk/.url) without stating interpretation. → completion audit
(roadmap 5b) is the planned defense.
Dead code deleted (backups in `.cleanup-backup\`): legacy MainWindow.Permissions pipeline
(2nd ExtractAllCommands + WRITE_FILE/RUN_NODE/DOWNLOAD_FILE), ValidatorAgent.ValidateAsync,
FluxBrain Handle{TaskQuery,MultiTask,SelfCoding}Async + OnHideWindow + IntentType trim,
SelfCoding\ (8 files), OrchestratorAgent.cs, ChatAssistant.cs, Architecture.cs, NOTES.md,
root `nul` file.

---

## Workflow rules

- **Git**: the project root has its own repo (`master`). Commit checkpoints after meaningful
  changes. If Adil copies files in from elsewhere, `git diff` reveals what changed.
- **Build** after every change set: `dotnet build FluxCore\FluxCore.csproj` → must be 0 errors
  (~27 pre-existing nullable warnings are normal; the count drifts, errors don't).
- **No tests exist yet.** Highest-value first test: walk every advertised command through
  parse → execute (registry sync, invariant #1).
- The C++ ImGui overlay (`FluxCore.vcxproj`, `imgui\`) is a separate experiment — ignore.
- Runtime data lives in `%APPDATA%\Davos\` (settings.json, DBs) and next to the exe
  (knowledge.json). Delete old `knowledge.json` to purge poisoned lessons from before the fix.

## Roadmap (agreed with Adil, 2026-06-12)

1. **Gemini Live "screen as camera"** — stream ~1fps screenshots into the Live API session
   (GeminiLiveService/GeminiTtsService already hold the WebSocket pattern). Live = eyes+voice
   (commentary, watching videos); JarvisCore = hands. Bridge: Live hands goals to the task loop.
2. **Minecraft via Mineflayer** — Davos writes/runs a Node.js bot (START_BACKGROUND), watches
   READ_LOG, steers it. NOT pixel-clicking (loop latency 2–5s/step can't play real-time).
   Roblox: no API, anti-cheat → honestly not feasible now.
3. **"Connect the dots" synthesis loop** — periodic job (InnerVoice is the natural home) that
   reads the day's DataLake/KnowledgeGraph events and writes conclusions into CoreMemory.
4. **Model routing for wisdom** — Flash for steps, gemini-2.5-pro for planning/reflexion/synthesis
   (ModelRouter exists).
5. **Eval set** — ~10 fixed tasks (clean desktop, list tabs, send TG message, write+run script…)
   run after every change. This is how "did he get smarter?" becomes measurable.
5b. **Completion audit** (designed 2026-06-12, NOT built) — on TASK_COMPLETE, one cheap LLM call:
   "does the evidence in the last steps support the claimed answer?" Inconclusive → accept
   (a false-FAILURE auditor poisons the loop, same lesson as invariant #7). This is the only
   planned defense against confident fabricated answers; truncation fixes reduce but don't
   eliminate them.
6. (Larger refactor, recommended) migrate JarvisCore from `[[COMMAND:arg]]` string protocol to
   Gemini native function calling — deletes the parser and its whole bug class.
