# Davos (FluxCore)

A persistent AI that lives on your Windows desktop. It passively observes everything — Telegram messages, clipboard, browser tabs, file changes, terminal, system errors — and can autonomously execute complex PC tasks, run code, browse the web, and modify its own source code.

---

## What It Does

Davos runs as a transparent overlay on your desktop. You talk to it (voice or text), and it figures out what you want:

- **Conversation** — answers questions, discusses topics, recalls context from previous sessions
- **PC tasks** — clicks, types, scrolls, opens apps, navigates UIs, takes screenshots, runs OCR
- **Code execution** — runs Python, PowerShell, Node.js, CMD with live output
- **Parallel tasks** — decomposes complex goals into subtasks and runs them across multiple agents simultaneously
- **Self-coding** — plans, reviews, implements, builds, and commits changes to its own source

Between your messages, it is passively collecting context so it already knows what you are working on before you ask.

---

## Architecture

```
User (voice / text)
        │
        ▼
   FluxBrain  ──── intent classifier + request queue
    │   │   │   │
    │   │   │   └── SELF_CODING  → SelfCodingOrchestrator
    │   │   └────── MULTI_TASK   → SwarmOrchestrator (parallel agents)
    │   └────────── PC_TASK      → JarvisCore (plan→execute→verify→reflect)
    └────────────── CHAT         → conversation loop with passive context
                         ▲
            Passive sensors injected every turn:
            Telegram · clipboard · files · terminal
            browser · notifications · event log · git
```

**Routing is fully dynamic** — no keywords, pure LLM classification with confidence gating (if confidence < 0.8, run 3-way consensus vote before acting).

---

## Passive Sensors

Davos never opens an app to read data it is already collecting in the background.

| Sensor | Mechanism | What it captures |
|--------|-----------|-----------------|
| **Telegram** | WTelegramClient MTProto API | Real-time DMs and group messages, stored in semantic memory |
| **Clipboard** | `WM_CLIPBOARDUPDATE` Win32 hook | Text, image, file entries with source app; ring buffer of 20 |
| **File system** | `FileSystemWatcher` | Creates, modifies, deletes, renames on Desktop / Documents / Downloads / repos |
| **Browser** | Local HTTP server on port 27834 + Chrome extension | Active tab URL, page DOM text, canvas screenshots |
| **VS Code** | Chrome bridge extension | Current file, cursor line, diagnostics |
| **Terminal** | CodeExecutionAgent output capture | Last 20 shell commands and their output |
| **Windows notifications** | `wpndatabase.db` polling (10 s) | Toast notifications from all apps |
| **Event log** | `EntryWritten` event (zero poll cost) | System + Application errors and warnings |
| **Git** | Repository watcher | Recent commits, branch status |
| **System metrics** | Periodic sampling | CPU, RAM, disk |

All sensors write to:
- **DataLake** — append-only SQLite log, never deleted (`datalake.db`)
- **MemoryService** — semantic vector search (`davos_memory.db`)

---

## Memory Architecture

Five layers, always active:

### 1. CoreMemory (`core_memory.json`)
MemGPT-style persistent blocks updated after every conversation turn.

| Block | Cap | Contents |
|-------|-----|----------|
| **Core** | 700 chars | Persona, who the user is, key relationships, preferences |
| **Working** | 450 chars | Today's focus, recent highlights, active topics |

Updated by a background LLM call (temperature 0.35) — never blocks the conversation.

### 2. DataLake (`datalake.db`)
Append-only event store. Every sensor write, every task lifecycle event (STARTED / DONE / FAILED / CANCELLED), every Telegram message. Survives restarts. Enables detecting interrupted tasks.

```sql
events(id INTEGER, source TEXT, ts DATETIME, content TEXT, meta JSON)
```

### 3. KnowledgeGraph (`knowledge_graph.db`)
Entity extraction from all DataLake events, run by LLM every 30 minutes.

- **Nodes** — People, Projects, Topics (type + label + properties)
- **Edges** — Relationships between entities

### 4. SemanticMemory (`davos_memory.db`)
SQLite with embedding vectors. Enables similarity search across all collected content: Telegram messages, clipboard entries, task results, web pages.

### 5. Hippocampus (`knowledge.json`)
Reflexion-based lesson memory. Each failed task generates a lesson (trigger keyword → what to do instead). Confidence-scored (0.5–1.0), tracked across successes and failures. Recalled into every task execution context.

---

## Task Execution (JarvisCore)

PC tasks run in a **plan → execute → verify → reflect** loop, up to 30 steps and 3 retries per task.

**Every step gets full passive context injected** (capped by character budget):

```
clipboard (1200)  · file events (800)   · git (400)
notifications (600) · chrome (2000)      · vscode (800)
telegram (1200)   · terminal (600)       · event log (350)
task history (600) · system metrics (150) · focus timeline (500)
```

**Command syntax** extracted from LLM response:

```
[[CLICK:x,y]]           [[TYPE:text]]          [[KEYS:ctrl+c]]
[[SCROLL:x,y,dir,n]]    [[RUN_SHELL:cmd]]      [[OPEN_APP:name]]
[[RESPOND:text]]        [[WAIT:ms]]            [[LOG:text]]
```

**Validation modes**: Fast (on failure only), Normal (screen commands), Thorough (all commands).

**Safety gates:**
- `RUN_SHELL` / `POWERSHELL` require user confirmation
- Deletions use Recycle Bin
- Never acts on Davos's own window
- Blocks kill commands targeting own process

**On failure:** `ReflectionAgent` analyzes the step, generates an alternative command, writes a lesson to Hippocampus, and retries.

---

## Execution Agents

| Agent | What it does |
|-------|-------------|
| **WindowsAutomationAgent** | Win32 P/Invoke mouse + keyboard. `SetCursorPos` + `mouse_event` for clicks. `keybd_event` for typing. UIAutomation for element detection. |
| **CodeExecutionAgent** | Sandboxed script execution in `%TEMP%\FluxSandbox`. Python, PowerShell, CMD. 60 s timeout. Output capped at 5000 chars. |
| **ExecutionAgent** | File reads, web browsing (Microsoft Playwright headless Chromium), app launching. |
| **SensoryCortex** | Screenshots via PrintScreen. OCR via `Windows.Media.Ocr`. UI element enumeration via UIAutomation. Focus timeline (180 s). |
| **ValidatorAgent** | Verifies command success: file existence, stdout parsing, download size, app launch. Returns `(isSuccess, message, shouldRetry)`. |
| **ReflectionAgent** | Analyzes failures, generates recovery strategy JSON, teaches lessons to Hippocampus. |

---

## Swarm System (Parallel Multi-Agent)

For complex goals that can be parallelized:

```
SwarmOrchestrator
    ├── TaskDecomposer   → LLM breaks goal into DAG of subtasks
    ├── DependencyGraph  → orders tasks, identifies parallelism
    ├── DynamicAgentPool → auto-scales CodeAgent + ScreenAgent instances
    ├── MessageBus       → pub/sub between agents
    ├── AgentRegistry    → health tracking
    └── FileLockManager  → prevents concurrent write conflicts
```

**Available commands for swarm tasks:** `WRITE_FILE`, `READ_FILE`, `EDIT_FILE`, `MOVE_FILE`, `DELETE_FILE`, `PYTHON`, `POWERSHELL`, `COMPILE`, `TEST`

**ScreenAgent** can run in an isolated Hyper-V VM or fall back to local screen.

---

## Self-Coding

Davos can modify its own source code:

```
SelfCodingOrchestrator
    1. PlannerAgent       → implementation plan from goal
    2. DeliberatorAgent   → 3-role review (Skeptic / Minimalist / Advocate)
    3. CodeWriterAgent    → generate code changes
    4. BuildVerifier      → dotnet build (auto-fix loop)
    5. RuntimeVerifier    → smoke tests
    6. GitManager         → branch → commit → merge
```

User approval gates at key decision points.

---

## LLM Routing

```
ModelRouter
    ├── GeminiService     → Gemini 2.5 Flash (primary)
    │     temperature: 0.1 (classifier) · 0.2 (task) · 0.7 (chat) · 0.3 (reflection)
    └── LocalLLMService   → OpenAI-compatible local API (fallback)
```

Static system instruction cached by Gemini across all steps in a task (reduces token cost significantly).

---

## UI

WPF overlay window (340 px wide, always on top, transparent, no taskbar entry).

| Element | Description |
|---------|-------------|
| **Chat tab** | Message history, text input, send button |
| **Memory tab** | Live view of semantic memory entries |
| **Tasks tab** | Running and completed task list with status |
| **HUD** | Step-by-step task progress with validation badges |
| **Settings** | Opacity, blur, wake word, API keys, STT languages, TTS, Telegram |
| **Logs overlay** | Full log output with timestamps |
| **Permission overlay** | Destructive action confirmation dialogs |

**Hotkeys:**

| Key | Action |
|-----|--------|
| `Alt+F` | Toggle window visibility (global hotkey) |
| Right Alt (hold) | Push-to-talk |
| Enter | Send text input |

---

## Telegram Setup

1. Get `api_id` and `api_hash` from [my.telegram.org](https://my.telegram.org)
2. Open Settings in Davos → enable Telegram → paste credentials
3. First run: a WPF dialog asks for phone → verification code → optional 2FA
4. Session saved to `%APPDATA%\Davos\telegram.session`
5. Status indicator shows `● Connected (N msgs)` when active

**Session recovery:** If the session file becomes corrupt, Davos auto-detects the error on startup, releases the file handle via GC, deletes all `telegram.*` files, and re-authenticates automatically. The `↺ Reset Session` button in Settings forces this manually.

Only DMs (`PeerUser`) and small groups (`PeerChat`) are captured — broadcast channels are filtered out.

---

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Runtime | .NET 10 (x64), WPF |
| LLM | Gemini 2.5 Flash · Local OpenAI-compatible |
| Database | SQLite via `Microsoft.Data.Sqlite` |
| Screen automation | Win32 P/Invoke · `System.Windows.Automation` |
| Browser | Microsoft Playwright (headless Chromium) |
| OCR | `Windows.Media.Ocr` |
| Audio | NAudio (16 kHz) · Gemini STT · Gemini Live TTS · ElevenLabs Scribe v2 |
| Telegram | WTelegramClient 3.5.8 (MTProto) |
| Notifications | `wpndatabase.db` (SQLite) |

---

## Prerequisites

- Windows 10 / 11 (x64)
- .NET 10 SDK
- Gemini API key
- Python 3.x (optional — for script execution)
- Node.js (optional — for JS script execution)
- Google Chrome + Davos extension (optional — for browser context)

---

## Build & Run

```bash
git clone https://github.com/Dafypig228/Fflux.git
cd Fflux
dotnet restore
dotnet build
dotnet run --project FluxCore
```

Set your Gemini API key as an environment variable:

```powershell
$env:GEMINI_API_KEY = "your-key-here"
```

Or paste it directly into the source (`MainWindow.xaml.cs` → `API_KEY` constant).

---

## Configuration

All settings persist to `%APPDATA%\Davos\settings.json`:

```json
{
  "WindowOpacity": 0.95,
  "BlurRadius": 10,
  "WakeWord": "Davos",
  "RequireWakeWord": true,
  "AutoMinimizeOnComplete": true,
  "TtsEnabled": false,
  "TtsVoice": "Charon",
  "ElevenLabsApiKey": "",
  "SttRussian": true,
  "SttEnglish": true,
  "SttKazakh": false,
  "TelegramEnabled": false,
  "TelegramApiId": 0,
  "TelegramApiHash": ""
}
```

---

## Data Files

All data is stored in `%APPDATA%\Davos\`:

| File | Contents |
|------|----------|
| `settings.json` | User configuration |
| `core_memory.json` | Persistent personality and user model |
| `datalake.db` | Append-only event log (all sensors + task lifecycle) |
| `davos_memory.db` | Semantic memory with embeddings |
| `knowledge_graph.db` | Extracted entities and relationships |
| `telegram.session` | WTelegramClient MTProto session |

And `knowledge.json` next to the executable — Hippocampus lessons.

---

## Project Structure

```
FluxCore/
├── FluxBrain.cs                    # Intent router + request queue
├── JarvisCore.cs                   # Task execution orchestrator
├── JarvisCore.Context.cs           # Dynamic context builder (passive sensor injection)
├── JarvisCore.Commands.cs          # Command extraction + dispatch
├── JarvisCore.Response.cs          # Response formatting
│
├── Memory/
│   ├── CoreMemoryService.cs        # MemGPT-style persistent blocks
│   ├── Hippocampus.cs              # Reflexion-based lesson memory
│   ├── MemoryService.cs            # Semantic memory with embeddings
│   ├── DataLakeService.cs          # Append-only event log
│   └── KnowledgeGraphService.cs    # LLM entity extraction
│
├── Sensors/
│   ├── TelegramService.cs          # MTProto real-time messages
│   ├── ClipboardService.cs         # Win32 clipboard monitoring
│   ├── FileWatcherService.cs       # FileSystemWatcher events
│   ├── ChromeBridgeService.cs      # Browser DOM + screenshot bridge
│   ├── NotificationService.cs      # Windows toast notifications
│   ├── EventLogService.cs          # System event log monitoring
│   └── GitWatcherService.cs        # Git repository tracking
│
├── Agents/
│   ├── WindowsAutomationAgent.cs   # Win32 mouse/keyboard
│   ├── WindowsAutomationAgent.Click.cs
│   ├── WindowsAutomationAgent.Input.cs
│   ├── WindowsAutomationAgent.Type.cs
│   ├── CodeExecutionAgent.cs       # Script sandbox
│   ├── CodeExecutionAgent.Scripts.cs
│   ├── CodeExecutionAgent.Files.cs
│   ├── ExecutionAgent.cs           # File/web/app operations
│   ├── SensoryCortex.cs            # Screenshot, OCR, UI detection
│   ├── ValidatorAgent.cs           # Command verification
│   └── ReflectionAgent.cs          # Failure analysis + recovery
│
├── LLM/
│   ├── ILLMService.cs              # LLM interface
│   ├── GeminiService.cs            # Gemini 2.5 Flash client
│   ├── LocalLLMService.cs          # Local model client (OpenAI API)
│   └── ModelRouter.cs              # Primary + fallback routing
│
├── Swarm/
│   ├── SwarmOrchestrator.cs        # Multi-agent coordinator
│   ├── TaskDecomposer.cs           # Goal → DAG of subtasks
│   ├── DependencyGraph.cs          # Task ordering + parallelism
│   ├── DynamicAgentPool.cs         # Auto-scaling agent pool
│   ├── Agents/
│   │   ├── CodeAgent.cs            # Background code execution
│   │   └── ScreenAgent.cs          # Visual testing (Hyper-V or local)
│   ├── Infrastructure/
│   │   ├── MessageBus.cs           # Pub/sub + task queue
│   │   ├── AgentRegistry.cs        # Health tracking
│   │   └── FileLockManager.cs      # Concurrent write protection
│   └── Environment/
│       ├── IScreenEnvironment.cs   # Isolated screen interface
│       └── HyperVEnvironment.cs    # Hyper-V VM implementation
│
├── SelfCoding/
│   ├── SelfCodingOrchestrator.cs   # Plan-deliberate-code-build-test-git
│   ├── PlannerAgent.cs             # Implementation planning
│   ├── DeliberatorAgent.cs         # 3-role code review
│   ├── CodeWriterAgent.cs          # Code generation + auto-fix
│   ├── BuildVerifier.cs            # dotnet build wrapper
│   ├── RuntimeVerifier.cs          # Smoke test runner
│   ├── GitManager.cs               # Branch/commit/merge
│   └── SelfCodingTypes.cs          # Shared types
│
└── UI/
    ├── MainWindow.xaml              # WPF layout
    ├── MainWindow.xaml.cs           # Bootstrap + init
    ├── MainWindow.Chat.cs           # Chat message rendering
    ├── MainWindow.Input.cs          # Voice + text input handling
    ├── MainWindow.Settings.cs       # Settings panel + Telegram init
    ├── MainWindow.Hud.cs            # Task progress HUD
    ├── MainWindow.Permissions.cs    # Destructive action dialogs
    ├── TelegramAuthDialog.xaml      # WPF phone/code/2FA input
    └── AudioService.cs             # NAudio recording + transcription
```
