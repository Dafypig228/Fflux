# FluxCore Source File Notes

> **Rewrite this when source files change.** Also fix prompt bugs:
> 1. **Restructure `_staticInstruction` with XML sections** (JarvisCore.cs) — Wrap the system prompt in clear XML-tagged sections: `<identity>`, `<tools>`, `<rules priority="critical">`, `<rules>`, `<examples>`, `<forbidden>`, `<paths>`. This helps Gemini index sections and reduces instruction-following failures. Keep the same content, just add structure.
> 2. **Deduplicate rules across prompts** — The rule "use CLICK:x,y coordinates, not names" appears in BOTH `_staticInstruction` AND `BuildDynamicContext` (line 51). Remove it from `BuildDynamicContext` — the static instruction already covers it. Apply this principle to any rule that appears in both static and dynamic context.
> 3. **Fix chat NEED_REROUTE anti-pattern** (FluxBrain.cs) — In `HandleChatAsync` (line 342-369), the chat prompt says "NEVER output commands" but then asks the LLM to output `NEED_REROUTE`. Fix by: Remove the NEED_REROUTE instruction from the chat system prompt entirely. Remove the `NEED_REROUTE` detection block in `HandleChatAsync` (lines 390-395).
> 5. **Add recovery alternatives in BuildDynamicContext** (JarvisCore.Context.cs) — After the failures section (around line 41), add a conditional block when `failures.Count >= 3`:
> ```csharp
> if (failures.Count >= 3) {
>     sb.AppendLine("\n--- ALTERNATIVE STRATEGIES (pick one you haven't tried):");
>     // shell commands, keyboard nav, scroll, refocus
> }
> ```
> 6. **Remove screenshot from ReflectionAgent** (ReflectionAgent.cs) — In `AnalyzeFailureAsync`, remove the screenshot parameter from the LLM call and remove "Look at the screenshot" from the prompt. The JSON schema (alternative/retry/abort) doesn't use visual data anyway, and ValidatorAgent already handles visual verification separately.
> 7. **Clean up chat display** (MainWindow.Chat.cs) — The chat shows internal status like step numbers and "Thinking..." that clutter the conversation. Clean up the chat to only show: user messages, AI responses, and brief task completion summaries. Move debug/status info to the log panel only.

---

## File Inventory (50 source files)

### Core Intelligence

| File | Summary |
|------|---------|
| **FluxBrain.cs** | Central intelligence router. Classifies user intent (Chat / PC_TASK / MULTI_TASK / TASK_QUERY / SelfCoding) via fast Gemini call, then routes to the appropriate handler. Uses consensus voting for low-confidence classifications and a bounded Channel queue that never drops requests. Supports parallel chat while tasks run. |
| **JarvisCore.cs** | Main task execution engine with Plan-Execute-Verify-Reflect loop. Caches `_staticInstruction` as the system prompt, runs up to 30 steps per task with 3 retries. Parses `[[COMMAND:args]]` syntax from LLM responses. Dispatches to automation, code execution, and shell agents. |
| **JarvisCore.Context.cs** | Partial class that builds per-step dynamic context. Assembles current goal, step number, Hippocampus lessons, completed/failed actions, clickable UI elements, and recovery hints into the user prompt sent alongside the static instruction. |
| **JarvisCore.Commands.cs** | Partial class with retry logic. `ExecuteWithRetryAsync` wraps each command with fallback strategies (scroll on click fail, clipboard paste on type fail, alternative app names on OPEN_APP fail). Includes foreground window safety check. |
| **JarvisCore.Response.cs** | Partial class for post-task reflexion. Extracts TRIGGER/LESSON pairs from LLM analysis and stores them in Hippocampus. Caps at 3 lessons per task. |

### Memory & Learning

| File | Summary |
|------|---------|
| **Hippocampus.cs** | Long-term reflexion memory stored as `knowledge.json`. Each `ReflexionItem` has a trigger keyword, lesson text, use count, confidence score (0-1), and success/fail tracking. `Recall(context)` finds relevant lessons; `LearnStructuredAsync` adds new ones with fuzzy dedup. `ReinforceLessons` adjusts confidence after task completion. |
| **MemoryService.cs** | Embeddings-based semantic memory with SQLite backend (`davos_memory.db`). Stores text with Gemini-generated embeddings. Supports cosine-similarity search, daily summaries, session summaries, and a `GetSmartContext` blend of recent + relevant + session memories. |

### Validation & Analysis

| File | Summary |
|------|---------|
| **ValidatorAgent.cs** | Critic agent that verifies command execution results. Type-specific checks: file existence for WRITE_FILE, error keywords for scripts. Returns `ValidationResult` with success/retry/message. |
| **ReflectionAgent.cs** | Failure analysis agent. Takes a failed action + context, asks LLM to produce a JSON `RecoveryPlan` with strategy: alternative command, retry with wait, or abort. |

### LLM Integration

| File | Summary |
|------|---------|
| **GeminiService.cs** | Gemini 2.5 Flash API client (`ILLMService`). Handles chat with history, vision (BASE64 JPEG inline), system instructions, and temperature control. |
| **LLM/ILLMService.cs** | Interface: `ChatWithHistory`, `GenerateText`, `AskSimple`, `GetEmbeddingAsync`. Properties: `ModelId`, `SupportsVision`. |
| **LLM/LocalLLMService.cs** | OpenAI-compatible local model client (llama.cpp, Ollama, LM Studio). Text-only (no vision). 120-second timeout for slow inference. |
| **LLM/ModelRouter.cs** | Smart routing: vision tasks go to cloud Gemini, text tasks try local model first with Gemini fallback on error. Embeddings always use cloud. |

### UI (MainWindow partials)

| File | Summary |
|------|---------|
| **MainWindow.xaml.cs** | Main window and application bootstrapper. Creates and wires all services (cortex, audio, gemini, executor, automation, codeRunner, brain, jarvis). Registers Alt+F global hotkey. Manages chat history as `ObservableCollection<ChatMessage>`. |
| **MainWindow.Chat.cs** | Chat UI handlers: send button, tab navigation (Chat/Memory/Tasks), microphone toggle, log/debug buttons, typewriter-effect message streaming with adaptive speed. |
| **MainWindow.Input.cs** | Keyboard input: Right Alt push-to-talk (PTT) via polling timer, Enter-to-send. Also contains legacy `ExecuteCommands` parser and `ProcessRequest` which delegates to FluxBrain.SubmitAsync. |
| **MainWindow.Permissions.cs** | Permission/confirmation dialogs. `RequestPermissionAsync` for destructive commands, `RequestScreenAccessAsync` for smart mode, `RequestConfirmationAsync` for FluxBrain intent gate. Also contains `ExecuteWithPermissionAsync` — the legacy command execution pipeline with safety checks. |
| **MainWindow.Settings.cs** | Settings UI: opacity/blur sliders, auto-minimize toggle, window position save/load, config persistence, fade in/out animations, hotkey hook. |
| **MainWindow.Hud.cs** | Neuro-HUD overlay showing agent state (THINKING/ACTING/VERIFYING/REFLECTING) with color-coded indicators, validation badges, and background window-focus monitoring that saves context to MemoryService. |

### Windows Automation

| File | Summary |
|------|---------|
| **WindowsAutomationAgent.cs** | Low-level Windows UI automation via Win32 P/Invoke. Declares `SetForegroundWindow`, `SetCursorPos`, `mouse_event`, `keybd_event`, `GetWindowText`, `EnumWindows`, DPI scaling APIs. Manages target window locking. |
| **WindowsAutomationAgent.Click.cs** | Click operations: `ClickAsync(x,y)`, `ClickWithRetryAsync` (3 retries), `DoubleClickAsync`, `RightClickAsync`. |
| **WindowsAutomationAgent.Type.cs** | Keyboard input: `TypeAsync(text)` character-by-character, `SendKeysAsync(keys)` for combos like CTRL+C, ALT+F4. |
| **WindowsAutomationAgent.Input.cs** | Additional input handling (partial class). |

### Screen & Vision

| File | Summary |
|------|---------|
| **SensoryCortex.cs** | Screen sensing: `GetActiveWindow()` with system window filtering, `GetScreenBase64()` as JPEG at 50% resolution, `GetVisualContext()` with OCR, `GetLayer3_UIHierarchy()` for clickable element detection. |
| **ScreenService.cs** | OCR with delta detection. Skips OCR when center pixel hasn't changed (performance optimization). Returns `(OCRText, VisualChanged)` tuple. |
| **ContextService.cs** | Window metadata (`GetLayer1_Metadata`) and deep UI hierarchy scan at cursor position (`GetLayer3_UIHierarchy`) — element type, name, automation ID, parent path, siblings. |
| **MouseService.cs** | Single method: `GetElementUnderMouse()` returns AutomationElement info (type, name, help text). |
| **GlassCortex.cs** | Captures background behind window for blur effect. Temporarily hides window, captures screen, restores. |
| **BlurCompositor.cs** | Enables Win11 Acrylic Blur Behind effect via `SetWindowCompositionAttribute` Win32 API. |

### Code Execution

| File | Summary |
|------|---------|
| **CodeExecutionAgent.cs** | Sandbox for scripts. `SanitizePath` handles OneDrive migration, wrong usernames, relative paths. `GetCommonPathsInfo` provides known directory paths for AI. Temp dir: `FluxSandbox`. |
| **CodeExecutionAgent.Scripts.cs** | Script runners: `RunPythonAsync`, `RunNodeAsync`, `RunPowerShellAsync`, `RunCmdAsync`. Auto-finds Python in PATH and common locations. UTF-8 for Cyrillic. Self-protection blocks kill commands on own process. 60-second timeout, 5000-char output limit. |
| **CodeExecutionAgent.Files.cs** | File operations: list, move, copy, delete (recycle bin), rename, read, write, search, clipboard, download. Smart file finding across Desktop/Documents/Downloads. Special Public Desktop handling. |
| **ExecutionAgent.cs** | High-level file/web/app operations. Uses Playwright for headless Chromium (URL navigation, screenshots, element interaction). `OpenApp` searches PATH, Start Menu, and running processes. Chrome launched with `--force-renderer-accessibility`. |

### Audio

| File | Summary |
|------|---------|
| **AudioService.cs** | Microphone recording with NAudio. 16kHz sample rate, 800ms silence detection threshold. Sends WAV to Gemini for transcription. Filters error responses. Events: `OnFinalText`, `OnPartialText`, `OnError`. |

### Swarm Multi-Agent System

| File | Summary |
|------|---------|
| **Swarm/SwarmOrchestrator.cs** | Main swarm coordinator. Decomposes goals via TaskDecomposer, builds dependency graph, spawns DynamicAgentPool, executes tasks in parallel respecting dependencies. Optional ScreenAgent for visual verification. Returns `SwarmExecutionResult` with stats. |
| **Swarm/TaskDecomposer.cs** | Uses Gemini to break high-level goals into `AgentTask[]` with command types, dependencies, and file access lists. Prefers background commands over screen interaction. |
| **Swarm/DependencyGraph.cs** | DAG-based task ordering. Tracks task status (Pending/Enqueued/Running/Completed/Failed). `GetReadyTasks()` returns tasks whose dependencies are all completed. Calculates max parallelism via topological sort. |
| **Swarm/DynamicAgentPool.cs** | Auto-scaling pool of CodeAgents. Config: min 2, max 50 agents, 3 tasks per agent, 5-minute idle timeout. Tracks specialization distribution. |
| **Swarm/ConflictResolver.cs** | Priority-based file lock conflict resolution. Strategies: Wait, Preempt, Merge, Skip, Abort. Releases stale locks from dead/errored agents. Communicates via MessageBus. |
| **Swarm/Agents/BaseAgent.cs** | Abstract base for swarm agents. States: Initializing/Idle/Working/Error/Stopped. 10-second heartbeat. Auto-registers with AgentRegistry. Abstract `RunLoopAsync`. |
| **Swarm/Agents/CodeAgent.cs** | Background code agent. Specializations: CSharpDeveloper, PythonDeveloper, FileManager, TestRunner, CodeReviewer, etc. No screen access — uses CodeExecutionAgent for scripts and file ops. |
| **Swarm/Agents/ScreenAgent.cs** | Visual testing agent using isolated IScreenEnvironment (Hyper-V VM or local fallback). Only ONE at a time. Capabilities: click, type, screenshot, launch-app, etc. |
| **Swarm/UI/ScreenAgentWindow.xaml.cs** | WPF window displaying ScreenAgent's live view — screenshot updates, status bar, action log. |

### Swarm Infrastructure

| File | Summary |
|------|---------|
| **Swarm/Infrastructure/MessageBus.cs** | In-memory pub/sub + task queue via Channels. Message types: TaskCompleted, TaskFailed, AssignTask, Progress, LockConflict. Supports targeted and broadcast delivery. `AgentTask` is the core work unit with dependencies, capabilities, and payload. |
| **Swarm/Infrastructure/AgentRegistry.cs** | Registry of all swarm agents with state tracking, heartbeat monitoring, metrics (success rate, avg duration), and health reports. Auto-detects dead agents via heartbeat timeout (1 minute). |
| **Swarm/Infrastructure/FileLockManager.cs** | In-memory file lock manager with expiration (5-minute default). Semaphore-based thread safety. Periodic cleanup of expired locks. Supports acquire, release, extend, wait-for-lock. |
| **Swarm/Environment/IScreenEnvironment.cs** | Interface for isolated screen environments. Methods: screenshot, click, type, send keys, scroll, launch app, PowerShell, file copy. Includes `LocalScreenEnvironment` stub that uses the real desktop (for testing). |
| **Swarm/Environment/HyperVEnvironment.cs** | Hyper-V VM implementation of IScreenEnvironment. Manages VM lifecycle (start, heartbeat check, shutdown). Executes PowerShell inside VM via `Invoke-Command`. Uses Win32 mouse/keyboard API inside VM for clicks/typing. |

### Self-Coding Pipeline

| File | Summary |
|------|---------|
| **SelfCoding/SelfCodingOrchestrator.cs** | Full pipeline: Plan (Gemini Pro) -> Deliberate (3 parallel reviewers on Gemini Flash) -> User Approval -> Git Branch -> Implement step-by-step -> Build Verify -> Auto-fix (3 attempts) -> User Review Diff -> Merge or Abandon. |
| **SelfCoding/PlannerAgent.cs** | Generates `ImplementationPlan` from user request + project context. Reads all .cs files, extracts method signatures from relevant files, includes full content of tiny files. |
| **SelfCoding/DeliberatorAgent.cs** | Three review roles: Skeptic (edge cases, race conditions), Minimalist (simplicity, reuse), User's Advocate (intent match, UX). Each scores 0-1 and flags blocking issues. |
| **SelfCoding/CodeWriterAgent.cs** | Implements plan steps. For large files (>300 lines) uses REPLACE METHOD blocks; for small files returns complete file. Has `FixBuildErrorsAsync` for auto-repair. |
| **SelfCoding/BuildVerifier.cs** | Runs `dotnet build --no-restore`, parses error/warning output with regex. Returns `BuildResult` with structured errors. |
| **SelfCoding/RuntimeVerifier.cs** | Backs up binaries, builds, then runs smoke test (`dotnet run -- --smoke-test` with 10s timeout). Non-critical if smoke test fails but build succeeds. |
| **SelfCoding/GitManager.cs** | Git operations: create `davos/` branch, commit, get diff, merge to base, abandon branch. Branch names sanitized to 40 chars. |
| **SelfCoding/SelfCodingTypes.cs** | Data types: `SelfCodingResult`, `VerificationResult`, `BuildResult`, `BuildError`, `ImplementationPlan`, `PlanStep`, `DeliberationResult`. |

### Configuration & Data Models

| File | Summary |
|------|---------|
| **AppSettings.cs** | Persistent settings in `%APPDATA%\Davos\settings.json`. Properties: opacity, blur, window position, wake word, auto-minimize, validation depth, local model config. Auto-migrates from old FluxCore directory. |
| **AppConfig.cs** | Runtime constants: background color (16,16,18), alpha 230, app name "Davos". |
| **ChatMessage.cs** | Observable chat message with `Text` (INotifyPropertyChanged) and `IsUser` bool. |
| **AutomationResult.cs** | Simple `{Success, Message}` record. |
| **ExecutionOutcome.cs** | Simple `{Success, Message}` record. |
| **UIElementInfo.cs** | UI element metadata: Name, Type, AutomationId, bounding box (X,Y,W,H), AutomationElement reference. |
| **FluxLogger.cs** | Appends timestamped messages to `Desktop\FluxDebug.txt`. |
| **Architecture.cs** | Legacy factory classes: `NeuralLink` (wraps GeminiService), `OmniLoop` (pairs SensoryCortex + NeuralLink). |
| **OrchestratorAgent.cs** | **DEPRECATED** — empty file, superseded by FluxBrain. Should be deleted. |
| **ChatAssistant.cs** | **EMPTY** — no content. |
| **ContextAnalyzer.cs** | **EMPTY** — no content. |

---

## Architecture Flow

```
User Input (voice/text via MainWindow)
  |
  v
FluxBrain.SubmitAsync() --- non-blocking Channel queue
  |
  v
ProcessLoop (background) --> ClassifyIntentAsync (fast Gemini)
  |
  +--> Chat: HandleChatAsync() --- history-based conversation
  |
  +--> PC_TASK: HandlePcTaskAsync()
  |       |
  |       v
  |     JarvisCore.ExecuteTaskAsync()
  |       |
  |       v
  |     Loop (max 30 steps):
  |       1. BuildDynamicContext (goals + failures + UI elements + memories)
  |       2. LLM generates [[COMMAND:args]]
  |       3. ExecuteWithRetryAsync (fallback strategies)
  |       4. ValidatorAgent checks result
  |       5. ReflectionAgent on failure -> RecoveryPlan
  |       6. Hippocampus learns lessons
  |
  +--> MULTI_TASK: HandleMultiTaskAsync()
  |       |
  |       v
  |     SwarmOrchestrator.ExecuteGoalAsync()
  |       TaskDecomposer -> DependencyGraph -> DynamicAgentPool
  |       CodeAgents execute in parallel (respecting deps)
  |
  +--> SelfCoding:
          Plan -> Deliberate(3x) -> Code -> Build -> Test -> Git
```

## Key Dependencies

| Package | Used By |
|---------|---------|
| NAudio | AudioService (microphone recording) |
| Microsoft.Data.Sqlite | MemoryService (embeddings DB) |
| Microsoft.Playwright | ExecutionAgent (headless Chromium) |
| Windows.Media.Ocr | SensoryCortex, ScreenService |
| System.Windows.Automation | WindowsAutomationAgent, ContextService, SensoryCortex |

## Important Constants

| Constant | Value | Location |
|----------|-------|----------|
| MAX_RETRIES | 3 | JarvisCore.cs |
| MAX_STEPS | 30 | JarvisCore.cs |
| SILENCE_THRESHOLD_MS | 800 | AudioService.cs |
| TIMEOUT_MS | 60000 | CodeExecutionAgent.cs |
| MAX_OUTPUT_LENGTH | 5000 | CodeExecutionAgent.cs |
| Hotkey | Alt+F | MainWindow.xaml.cs |
| Settings path | %APPDATA%\Davos\ | AppSettings.cs |
| Memory DB | davos_memory.db | MemoryService.cs |
| Knowledge file | knowledge.json | Hippocampus.cs |
