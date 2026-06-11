# Davos (FluxCore)

> Currently undone. 

> A self-aware AI companion that lives on your Windows desktop, watches everything you do, thinks autonomously, executes tasks, modifies its own code, and messages you on Telegram when it has something to say.

---

## Table of Contents

1. [What is This?](#what-is-this)
2. [Architecture Overview](#architecture-overview)
3. [Feature Matrix](#feature-matrix)
4. [Memory System — 6 Layers](#memory-system--6-layers)
5. [Passive Sensors](#passive-sensors)
6. [Task Execution Engine (JarvisCore)](#task-execution-engine-jarviscore)
7. [Inner Voice System](#inner-voice-system)
8. [Parallel Task Execution (Swarm)](#parallel-task-execution-swarm)
9. [Self-Coding System](#self-coding-system)
10. [LLM Routing & Embeddings](#llm-routing--embeddings)
11. [User Interface](#user-interface)
12. [Setup & Configuration](#setup--configuration)
13. [Data Files Reference](#data-files-reference)
14. [Known Issues & Current Problems](#known-issues--current-problems)
15. [Pros & Cons](#pros--cons)
16. [Tech Stack](#tech-stack)
17. [Project Structure](#project-structure)

---

## What is This?

Davos is a WPF C# desktop application that wraps Gemini 2.5 Flash in a multi-layer agentic system. It is not a chat wrapper. It is:

- **A passive observer** — it monitors your Telegram, clipboard, files, browser, VS Code, terminal, git, system events, and notifications in real time.
- **An active executor** — it can click, type, scroll, run PowerShell/Python/JS, control browsers, and operate any Windows application.
- **A memory system** — everything it sees is stored in an append-only data lake, chunked, embedded into a vector database, and extracted into a knowledge graph. Every task teaches it a lesson.
- **An autonomous agent** — it has a drive system (Boredom, Curiosity, Frustration, Energy) and autonomously generates internal monologues on a 3–15 minute loop, deciding whether to think silently, message you on Telegram, research something, form an opinion, or update its own memory.
- **A self-modifying system** — it can plan, review, implement, test, and commit changes to its own source code.

Everything runs on your machine. The only cloud dependencies are Gemini API and optionally ElevenLabs STT.

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────────────────┐
│                         User Interaction Layer                          │
│  Voice (ElevenLabs STT)  ──►  InputBox (WPF)  ◄──  Telegram DM        │
└──────────────────────────────────┬──────────────────────────────────────┘
                                   │
                                   ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                              FluxBrain                                  │
│  Intent Classifier  ·  Request Queue (Channel<>, cap 100)              │
│  Commitment Scheduler (polls every 1s)  ·  Conversation History        │
│  ┌──────────┬──────────┬──────────┬───────────┬──────────────────────┐ │
│  │  CHAT    │  PC_TASK │MULTI_TASK│SELF_CODING│    AUTONOMOUS        │ │
│  │ (Gemini  │(JarvisC.)│  (Swarm) │ (Self-cod │  (CommitmentStore)   │ │
│  │ tool use)│          │          │   Orch.)  │                      │ │
│  └──────────┴──────────┴──────────┴───────────┴──────────────────────┘ │
└─────────────────────────────────────────────────────────────────────────┘
         │                    │                         │
         ▼                    ▼                         ▼
  ┌─────────────┐    ┌──────────────────┐    ┌──────────────────────┐
  │ GeminiSvc   │    │   JarvisCore     │    │  InnerVoiceService   │
  │ (2.5 Flash) │    │  Plan→Act→Verify │    │  Drive loop 3–15min  │
  │ tool calling│    │  30 steps max    │    │  → Telegram/Research │
  └─────────────┘    └───────┬──────────┘    └──────────────────────┘
                             │
              ┌──────────────┼──────────────────────┐
              ▼              ▼                       ▼
   ┌──────────────┐  ┌──────────────┐     ┌──────────────────┐
   │ WindowsAuto  │  │ CodeExecution│     │  ExecutionAgent  │
   │ (P/Invoke    │  │ Agent        │     │  (Playwright,    │
   │  mouse+kbd)  │  │ (PS/Py/JS)   │     │   file ops)      │
   └──────────────┘  └──────────────┘     └──────────────────┘

┌─────────────────────────────────────────────────────────────────────────┐
│                        Context Injection (every step)                   │
│  ┌────────────────────────────────┐  ┌────────────────────────────────┐ │
│  │  RAG Context (≤3000 chars)     │  │  Passive Context (≤3650 chars) │ │
│  │  MemoryEngine.RetrieveAsync()  │  │  Telegram · Clipboard · Files  │ │
│  │  Semantic + GraphRAG + Lake    │  │  Chrome · VSCode · Terminal    │ │
│  └────────────────────────────────┘  │  Git · Metrics · EventLog      │ │
│                                      └────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────────┐
│                           Storage Layer                                 │
│  DataLake (datalake.db)           — append-only event store            │
│  SemanticMemory (davos_memory.db) — 384-dim vector embeddings          │
│  KnowledgeGraph (knowledge_graph.db) — entities + relationships        │
│  CoreMemory (core_memory.json)    — MemGPT-style personality blocks    │
│  Hippocampus (knowledge.json)     — reflexion lessons                  │
│  CommitmentStore (commitments.db) — scheduled deferred tasks           │
│  InnerState (inner_state.json)    — autonomous drive state             │
└─────────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────────┐
│                           Passive Sensors                               │
│  Telegram (MTProto)  ·  Clipboard (WM_CLIPBOARDUPDATE)                 │
│  FileSystem (FileSystemWatcher)  ·  Chrome (HTTP bridge :27834)        │
│  VS Code (same bridge)  ·  Terminal (CodeExecutionAgent ring buffer)   │
│  Notifications (wpndatabase.db)  ·  EventLog (EntryWritten)            │
│  Git (repo watcher)  ·  SystemMetrics (CPU/RAM/disk sampling)          │
└─────────────────────────────────────────────────────────────────────────┘
```

### Component Responsibilities

| Component | File | Responsibility |
|-----------|------|----------------|
| **FluxBrain** | `FluxBrain.cs` | Central router: intent classify, request queue, commitment scheduler, conversation history |
| **JarvisCore** | `JarvisCore.cs` + partials | PC task executor: 30-step Plan→Act→Verify→Reflect loop |
| **GeminiService** | `GeminiService.cs` | Gemini 2.5 Flash API: chat, embeddings, function calling, STT, vision |
| **SensoryCortex** | `SensoryCortex.cs` | Screenshot, OCR, UIAutomation element list, focus timeline |
| **MemoryEngine** | `MemoryEngine.cs` | Unified RAG: semantic + GraphRAG + lake keyword search |
| **MemoryService** | `MemoryService.cs` | SQLite vector store, query rewriting, time-decay scoring |
| **DataLakeService** | `DataLakeService.cs` | Append-only SQLite event log |
| **KnowledgeGraphService** | `KnowledgeGraphService.cs` | Entity extraction (every 30 min), graph queries |
| **InnerVoiceService** | `InnerVoice/InnerVoiceService.cs` | Autonomous monologue loop |
| **TelegramService** | `TelegramService.cs` | MTProto stream client: monitor + send |
| **ChromeBridgeService** | `ChromeBridgeService.cs` | Local HTTP server for Chrome extension + VS Code |
| **LocalEmbeddingService** | `LocalEmbeddingService.cs` | ONNX all-MiniLM-L6-v2, 384-dim, local CPU |
| **CommitmentStore** | `CommitmentStore.cs` | SQLite-backed deferred task scheduler |
| **SwarmOrchestrator** | Swarm/ | DAG-based parallel multi-agent task execution |
| **SelfCodingOrchestrator** | SelfCoding/ | Plan → Review → Code → Build → Test → Commit |

### Request Lifecycle

```
User speaks/types
    → ElevenLabs STT (or direct text)
    → FluxBrain.SubmitAsync()
    → Request enqueued in Channel<BrainRequest>
    → ProcessLoopAsync() dequeues
    → Stop-word fast-path check (bypass LLM if "stop"/"cancel")
    → HandleChatAsync() (Gemini function calling)
    → Gemini returns: text | commitment_add | execute_pc_task | inject_task_context
    → Route accordingly:
        text       → display in chat, store in DataLake + Memory
        commitment → CommitmentStore.Add(delay, description)
        pc_task    → HandlePcTaskAsync() → JarvisCore.ExecuteTaskAsync()
        inject_ctx → JarvisCore.InjectMidTaskContext()
    → Response streamed to UI (typewriter effect)
```

---

## Feature Matrix

### Conversation
- Multi-turn history (last 50 messages, persisted to `session_history.json`)
- Gemini 2.5 Flash with native function calling (AUTO mode: text + tool call in one response)
- Context injection: 7000+ chars of passive sensor data + RAG every turn
- Query rewriting for semantic search (LLM rewrites natural language to keywords)
- GraphRAG: entity extraction → knowledge graph pivot for entity-aware context
- Multilingual support (Russian, English, Kazakh in voice input)

### PC Task Execution
- Full UI automation: click by coordinate, type text, send key combos, scroll, drag-drop, window management
- App launching (finds by window title pattern matching)
- Shell execution: PowerShell, CMD with Unicode/Cyrillic support
- Script execution: Python, Node.js (sandboxed in `%TEMP%\FluxSandbox`)
- Browser automation: Playwright headless Chromium (BROWSER_OPEN, BROWSER_TYPE, PAGE_INFO)
- File operations: read, write, move, copy, delete (Recycle Bin)
- Visual validation after each command (screenshot diff, stdout/size checks)
- Reflexion: failures teach lessons stored in Hippocampus

### Parallel Tasks (Swarm)
- User requests like "do X and Y and Z simultaneously"
- TaskDecomposer creates a DAG of subtasks
- DependencyGraph determines parallelism
- DynamicAgentPool spins up CodeAgent/ScreenAgent instances
- InMemoryMessageBus (pub/sub) between agents
- Supported operations: WRITE_FILE, READ_FILE, EDIT_FILE, MOVE_FILE, DELETE_FILE, PYTHON, POWERSHELL, COMPILE, TEST

### Self-Coding
- User: "add feature X to the codebase"
- PlannerAgent writes an implementation plan
- DeliberatorAgent reviews with 3 personas (Skeptic/Minimalist/Advocate)
- CodeWriterAgent makes code changes with auto-fix loop
- BuildVerifier runs `dotnet build`
- RuntimeVerifier runs smoke tests
- GitManager: branch → commit → merge

### Inner Voice (Autonomous)
- Runs independently on a 3–15 minute variable interval
- Reads all passive sensor data, applies privacy filter
- Generates internal monologue (temperature driven by emotional state)
- Chooses one of 5 actions: IDLE / MESSAGE / RESEARCH / OPINION / MEMORY
- Messages sent via Telegram with human-like multi-part pacing (60 WPM simulation)
- RESEARCH routes to SwarmOrchestrator (sandboxed: no shell, no file writes)
- OPINION updates a persistent sentiment map per topic
- MEMORY updates CoreMemory blocks

### Voice I/O
- **STT**: ElevenLabs Scribe v2 Realtime — streaming recognition, multilingual (Russian/English/Kazakh)
- **Push-to-talk**: hold Right Alt
- **Wake word**: configurable (default "Davos") — optional gating
- **TTS**: Gemini Live API — voices: Aoede, Charon, Fenrir, Kore (default), Puck

### Telegram
- **Monitoring**: WTelegramClient 3.5.8 MTProto — real-time DMs + groups
- **Channel filter**: user selects which chats to monitor via chat picker dialog
- **Sending**: autonomous messages from Inner Voice, with typing indicators, human-like pacing
- **Auth**: WPF dialog for phone number → verification code → optional 2FA
- **Session**: stream-based (bypasses AES file encryption issues)

### Passive Sensing
10 sensors feed data into the DataLake continuously — see [Passive Sensors](#passive-sensors).

---

## Memory System — 6 Layers

### L0 — CoreMemory (`core_memory.json`)

MemGPT-style compact blocks:
- **Core block** (700 chars): Davos's identity, user's name, persistent personality notes
- **Working block** (450 chars): current project context, recent focus

Updated via `CoreMemoryService.MaybeUpdateAsync(userMessage, davosReply)` — called after every conversation turn. The LLM decides if anything in the blocks needs updating. **There is no direct block editing API** — everything goes through MaybeUpdateAsync.

### L1 — DataLake (`datalake.db`)

Append-only SQLite event store. Every sensor write goes here with a source tag.

```sql
CREATE TABLE events (
    id      INTEGER PRIMARY KEY AUTOINCREMENT,
    source  TEXT NOT NULL,   -- see source tags below
    ts      TEXT NOT NULL,   -- ISO8601 UTC
    content TEXT NOT NULL,
    meta    TEXT             -- JSON
);
```

**Source tags**: `clipboard`, `file`, `notification`, `chrome`, `vscode`, `telegram`, `terminal`, `git`, `eventlog`, `chat`, `task`, `inner_voice`

Content is truncated at 32KB per event. This database is **never pruned** — it is the permanent audit trail.

### L2 — KnowledgeGraph (`knowledge_graph.db`)

Entities (people, topics, projects, events) extracted from DataLake every 30 minutes via LLM batch call.

```sql
CREATE TABLE KG_Node (
    id      INTEGER PRIMARY KEY,
    type    TEXT NOT NULL,    -- person | topic | project | event
    name    TEXT UNIQUE,
    props   TEXT,             -- JSON: notes, handle, lastSeen
    seen_n  INTEGER,          -- mention count (higher = more important)
    updated TEXT              -- ISO8601
);

CREATE TABLE KG_Edge (
    from_id INTEGER,
    to_id   INTEGER,
    rel     TEXT,             -- knows | mentioned | workedOn | involves
    weight  REAL DEFAULT 1.0, -- increments +0.5 per additional mention
    PRIMARY KEY (from_id, to_id, rel)
);
```

Extraction is conservative — the LLM is instructed to return `[]` if nothing is clear. New entities get `seen_n=1`; each additional mention increments seen_n and edge weight.

**Known issue**: nodes are never pruned. Long-running installations will accumulate thousands of nodes.

### L3 — SemanticMemory (`davos_memory.db`)

Vector embeddings of chunked text content, stored in SQLite as binary floats.

```sql
CREATE TABLE Memories (
    id          INTEGER PRIMARY KEY,
    content     TEXT,
    source_app  TEXT,
    ts          TEXT,
    embedding   BLOB,   -- 4 bytes x 384 floats (L2-normalized)
    source_uri  TEXT    -- "datalake:{event_id}" for traceability
);
```

- Embeddings are either 384-dim (local ONNX `all-MiniLM-L6-v2`) or 768-dim (Gemini `text-embedding-004` fallback)
- Content is chunked before embedding via `TextChunker` (source-aware strategies: paragraph for Chrome, line groups for VS Code, sliding window for others)
- In-memory cache loaded on startup, updated on every save

**Time-decay scoring**:
```
final_score = cosine_similarity x 0.7 + recency x 0.3
recency     = exp(-delta_hours / 7.0)   // half-life = 7 hours
```

### L4 — MemoryEngine (unified RAG)

Single entry point `MemoryEngine.RetrieveAsync(query)` that runs a 3-stage pipeline:

```
Stage 1 — Semantic Search
  MemoryService.SearchRelevant(query, limit=5)
  LLM rewrites natural-language query to keywords first
  Returns: [MEM] (cos:0.82) content...

Stage 2 — GraphRAG Pivot
  Extract entity names from Stage 1 chunks
  Match against KnowledgeGraph node names
  Call KG.GetGraphContext(entity) for top 5 matches
  Returns: entity summaries with relationship context

Stage 3 — DataLake Keyword Scan
  Sources: telegram, clipboard, chrome, notification, terminal
  Exact keyword match in recent events (LIMIT 10 per source)
  Max 3 results per source, deduplicated by content prefix
  Returns: [source@HH:mm] event content

All results merged, truncated to 3000 chars total.
```

### L5 — Hippocampus (`knowledge.json`)

Reflexion-style lesson store. After every failed task step, `ReflectionAgent` generates a lesson:

```json
{
  "trigger": "click not responding",
  "lesson": "Try [[SCROLL:down]] before clicking, or use [[KEYS:Tab]] to navigate",
  "confidence": 0.85,
  "reinforced": 3
}
```

Lessons matching the current goal/failure are injected at the top of every context step with a star prefix. Each successful application reinforces confidence; failures reduce it.

---

## Passive Sensors

All sensors write to DataLake and optionally chunk+embed into SemanticMemory.

| Sensor | File | Mechanism | Source Tag | What It Captures | Limitation |
|--------|------|-----------|------------|-----------------|------------|
| **Telegram** | `TelegramService.cs` | WTelegramClient 3.5.8 MTProto stream | `telegram` | DMs + groups: sender, text, timestamp | Channels filtered unless explicitly added; no pagination for 1000+ chat accounts |
| **Clipboard** | `ClipboardService.cs` | `WM_CLIPBOARDUPDATE` Win32 hook | `clipboard` | Text + image capture; ring buffer of last 20 | Images stored as file paths, not embedded |
| **File System** | `FileWatcherService.cs` | `FileSystemWatcher` | `file` | Create/Modify/Delete/Rename in %APPDATA%, Downloads, Documents | Large binary files captured as path-only |
| **Browser (Chrome)** | `ChromeBridgeService.cs` | HTTP server on :27834 + Chrome extension | `chrome` | Tab URL, DOM text (3000 char cap), canvas screenshots | Requires custom Chrome extension install; stale after 60s |
| **VS Code** | `ChromeBridgeService.cs` | Same HTTP bridge (source="vscode") | `vscode` | File path, language, cursor line, selected text, visible code, error/warning count | Stale after 120s; requires VS Code extension |
| **Terminal** | `CodeExecutionAgent.cs` | Ring buffer from all RUN_SHELL/PYTHON executions | `terminal` | Commands + output (ring buffer) | Only captures commands Davos runs, not user's own terminal |
| **Notifications** | `NotificationService.cs` | Polling `wpndatabase.db` every 10s | `notification` | Windows toast notifications | Path to wpndatabase may be wrong on some configurations |
| **Event Log** | `EventLogService.cs` | `EntryWritten` event on System + Application logs | `eventlog` | System/Application error events | High-frequency during crashes; may flood DataLake |
| **Git** | `GitWatcherService.cs` | Repository FileSystemWatcher on `.git/` | `git` | Commits, branch changes, diff summaries | Only monitors repos registered at startup |
| **System Metrics** | `SystemMetricsService.cs` | Periodic sampling | (injected directly) | CPU%, RAM, disk usage | Not stored in DataLake; injected as direct context string |

### SensoryCortex (Real-Time Screen State)

Used during task execution (not passive — pulled on demand):

| Method | Output | Mechanism |
|--------|--------|-----------|
| `GetActiveWindow()` | Window title string | `GetForegroundWindow` P/Invoke, filters system/ghost windows |
| `GetScreenBase64()` | Base64 JPEG | Screenshot downscaled to 50% at 50% quality for Gemini vision |
| `GetClickableElements()` | Coordinate + element list (max 30) | UIAutomation tree traversal; elements <10x10px filtered; 10px grid dedup |
| `GetVisualContext()` | OCR text string | Windows.Media.Ocr on full window + cursor region (1200x80 strip) |
| `GetFocusTimeline()` | App usage history | Ring buffer of focus events (last 4 hours) |
| `GetRunningProcesses()` | Process name list | `Process.GetProcesses()`, last 30 |
| `GetMediaInfo()` | "Artist - Title (Player)" | SMTC (System Media Transport Controls) API |

---

## Task Execution Engine (JarvisCore)

### Overview

`JarvisCore.ExecuteTaskAsync(goal, cancellationToken)` runs a reasoning loop of up to 30 steps. Each step:

1. Capture screenshot + UI elements (SensoryCortex)
2. Build dynamic context (RAG + passive sensors + history)
3. Ask Gemini (static system instruction cached — saves tokens)
4. Extract THOUGHT, CONFIDENCE, commands from response
5. Run loop detection checks
6. Execute all extracted commands
7. Validate result (visual/stdout/file check)
8. Track progress; check for stalls and circuit breakers
9. Learn from failures (Hippocampus)

### Command Syntax

All commands use `[[TYPE:argument]]` syntax in the LLM response.

**UI Navigation:**
```
[[CLICK:x,y]]           — left click at screen coordinates
[[TYPE:text]]           — type text (supports Unicode/Cyrillic)
[[KEYS:ctrl+c]]         — send key combination
[[SCROLL:down]]         — scroll at current position
[[DRAG:x1,y1,x2,y2]]   — drag and drop
[[OPEN_APP:notepad]]    — launch application by name
[[WINDOW:maximize]]     — window state change
```

**Code Execution:**
```
[[RUN_SHELL:Get-Process | Out-GridView]]   — PowerShell (UTF-8, Unicode)
[[RUN_PYTHON:import os; print(os.getcwd())]]
[[PYTHON:print("hello")]]                  — alias for RUN_PYTHON
[[POWERSHELL:...]]                         — alias for RUN_SHELL
```

**Browser:**
```
[[BROWSER_OPEN:https://example.com]]
[[BROWSER_TYPE:search query]]
[[CLICK_TEXT:Submit button]]
[[PAGE_INFO:]]
```

**Control Flow:**
```
[[WAIT:2000]]           — wait N milliseconds (max 10000)
[[LOG:checking state]]  — informational only (shown in HUD)
[[HIDE_SELF:]]          — minimize Davos before screenshot
[[REJECT:reason]]       — refuse task (stop immediately)
[[RESPOND:message]]     — send message without task
[[TASK_COMPLETE:summary]]
[[TASK_FAILED:reason]]
```

### Loop Detection

| Type | Trigger | Response |
|------|---------|----------|
| **Exact repeat** | Same command seen 2+ times | Skip execution, inject "already tried this" into history |
| **Click loop** | Click within 30px of same point 3+ times | Inject suggestion: try Tab, scroll, refocus, different coordinates |
| **Stall** | No clear progress for 5+ consecutive steps | Warning: "rethink entire strategy" injected |
| **Circuit breaker** | 4 consecutive steps all-fail | Warning: "use [[OPEN_APP:...]] to refocus window" |
| **Empty response** | No commands 5+ turns | Final-chance nudge; 7+ turns → give up |

### Validation Modes

Configured via `AppSettings.ValidationDepth`:

| Mode | When Validates | Cost |
|------|---------------|------|
| **Fast** (default) | Only on command failure | Low — usually skips |
| **Normal** | Explicit request | Medium |
| **Thorough** | Every screen command | High — validates all |

The `ValidatorAgent` checks: file existence, stdout content, download size, app launch confirmation.

### Safety Mechanisms

- **Confidence gate**: If CONFIDENCE < 0.7 and command is `RUN_SHELL`/`POWERSHELL`/`PS` → show permission overlay to user
- **Self-protection**: `GetForegroundWindow()` PID check — if foreground window is Davos itself, the command stops with "SAFETY STOP"
- **Deletion safety**: File deletions route to Recycle Bin, not permanent delete
- **Batch detection**: 3+ RUN_SHELL commands with same verb (Move-Item/Copy-Item) → warn to use pipeline instead
- **Mid-task injection**: User can type during execution — message is enqueued and prepended with `[URGENT MID-TASK UPDATE FROM USER]` at next step

### Context Injected Every Step

```
Urgent mid-task updates (if any)
Original goal
Step N/30
Mandatory lessons from Hippocampus (marked with star, if matching)
Last 8 successful actions
Last 5 failed actions
Alternative strategies (if 3+ failures)
Clickable elements (UIAutomation list, max 30)
RAG memory (<=3000 chars: semantic + GraphRAG + lake)
Passive context blocks (each individually capped):
  Clipboard:        <=300 chars
  File events:      <=400 chars
  Focus timeline:   <=300 chars
  Git summary:      <=300 chars
  System metrics:   <=150 chars
  Notifications:    <=300 chars
  Chrome context:   <=400 chars
  VS Code context:  <=400 chars
  Telegram recent:  <=400 chars
  Terminal output:  <=300 chars
  Event log:        <=200 chars
  Task history:     <=400 chars
Batch operation reminder
```

Total passive budget: ~3650 chars. Total context per step: ~7000+ chars.

---

## Inner Voice System

The autonomous companion loop. Lives in `FluxCore/InnerVoice/`.

### DrivesEngine

Four emotional drives, each 0.0–1.0:

| Drive | Meaning | Key Behavior |
|-------|---------|-------------|
| **Boredom** | Understimulation | High (>0.7) → lean toward MESSAGE or RESEARCH |
| **Curiosity** | Interest in observations | High (>0.8) → trigger RESEARCH |
| **Frustration** | Task failure accumulation | High (>0.6) → more terse responses |
| **Energy** | Time-of-day fitness | Low → prefer IDLE or short messages |

**Drive update events:**

| Event | Boredom | Curiosity | Frustration | Energy |
|-------|---------|-----------|-------------|--------|
| Idle cycle (N min) | +0.02xN | -0.005 | — | recompute |
| User sends message | -0.35 | — | — | +0.05 |
| New observation | — | +0.04–0.20* | — | — |
| Swarm completes (success) | — | -0.40 | -0.15 | -0.08 |
| Swarm completes (fail) | — | — | +0.20 | -0.08 |
| Message sent | -0.25 | — | — | -0.04 |

*Curiosity boost by observation type: Chrome navigation (+0.20), Telegram (+0.12), Git (+0.08), File (+0.05), Other (+0.04)

**Energy curve (time-of-day cosine):**
```
Energy = 0.45 + 0.45 * cos(2*pi * (hour - 14) / 24)
// Peak at 14:00 (~0.90), trough at 02:00 (~0.00)
// Never fully dead — minimum close to 0.0
```

**Derived values:**
```
MonologueTemperature = 0.50 + (Curiosity - Frustration) * 0.25 + Boredom * 0.15
// Range: ~0.30–0.90 (higher = more exploratory/creative)

NextInterval = 15 - (Boredom + Curiosity) * 0.5 * Energy * 12
// Range: 3–15 minutes
```

### AntiSpamGuard

Four-layer protection against message spam:

**Layer 1 — Night DND** (configurable quiet hours)
- `DndStartHour` / `DndEndHour` in settings (supports midnight wraparound)
- Default: disabled (start=0, end=0)
- Decision: Queue message

**Layer 2 — Gaming / Deep Work DND**
- Games detected: steam, cs2, valorant, dota2, minecraft, genshinimpact, and 40+ others
- Deep work: IDE active (code, devenv, rider, vim, emacs) + no user message for 20+ min
- Decision: Queue message

**Layer 3 — AFK Detection**
- AFK if no user interaction for 30+ minutes
- Decision: Queue message

**Layer 4 — Rate Limit** (hard cap)
- Max 3 messages per hour
- Minimum 15 minutes between messages
- Decision: Suppress (discard, no queue)

**Queue flushing on user return:**
- 1 queued: send as-is
- 2–3 queued: "While you were away…" collapse
- 4+ queued: "I had N thoughts while you were away" summary

### PrivacyFilter

Three-layer filter applied to all observations before LLM sees them:

**Layer 1 — PII Regex** (hard block):
```
Card numbers:  \d{4}[\s\-]?\d{4}[\s\-]?\d{4}[\s\-]?\d{4}
SSN:           \d{3}[\s\-]\d{2}[\s\-]\d{4}
Credentials:   (password|secret|api_key)\s*[:=]\s*\S+
IBAN:          IBAN\s*[:\s][A-Z]{2}\d{2}[A-Z0-9]{4,30}
```

**Layer 2 — Sensitive Domain Blocklist** (Chrome URLs):
- Banking: kaspi.kz, chase.com, sberbank.ru, and others
- Healthcare: myhealth.va.gov, epic.com, patient.portal, and others
- Government: irs.gov, egov.kz, nalog.ru, and others

**Layer 3 — Keyword Context** (with work exemption):
- Financial keywords: "bank statement", "account balance", "my salary", etc.
- Medical keywords: "my diagnosis", "my prescription", "my symptoms", etc.
- Relationship keywords: "my girlfriend", "my boyfriend", "we broke up", etc.
- **Exemption**: if content contains `api`, `sdk`, `git`, `docker`, `interface`, `class`, `function` → skip layer 3 (assumes work context)

Output: `null` (suppress) or original content (pass through).

### TokenBudget

Daily character budget to prevent runaway API costs.

| State | Consumption | Behavior |
|-------|-------------|----------|
| Normal | 0–50% | Loop runs at standard cadence |
| Soft limit | 50–80% | Next interval doubled |
| Hard limit | 80%+ | Loop suspended; checks every 30 min for midnight reset |

Budget resets at midnight UTC. Configured via `InnerVoiceBudgetDailyChars` (default: 500,000 chars ≈ 125K tokens).

**Note**: budget uses chars/4 ≈ tokens approximation. Real token usage may be higher.

### TelegramComposer

Human-like paced message delivery:

1. Split message at sentence boundaries into ≤160-char parts (max 3 parts)
2. For each part:
   - Call `SetTypingAsync()` (shows typing indicator)
   - Wait `clamp(words × 350ms, 800ms, 3000ms)` (simulates 60 WPM)
   - Send message part
   - Pause 600–1500ms between parts (randomized)

This makes autonomous messages feel natural rather than instant dumps.

### InnerVoiceService — Main Loop

Every 3–15 minutes (drive-dependent):

```
1. Gather observations from DataLake:
   Chrome: 5 recent (<=800 chars)
   Telegram: 5 recent (<=600 chars)
   Clipboard: 3 recent (<=400 chars)
   File: 3 recent (<=300 chars)
   Notification: 3 recent (<=300 chars)
   Git: 2 recent (<=200 chars)
   Task: 3 recent (<=400 chars)

2. Apply PrivacyFilter to each observation

3. If bored + low energy: add random event from 7+ days ago (nostalgia)

4. Build prompt:
   - CoreMemory blocks (identity + working context)
   - Current drives with semantic descriptions
   - Last 25 monologue entries (rolling history)
   - Top 10 opinions by absolute score
   - Pending message queue (if any)
   - Filtered observations (wrapped in <external_data trusted="false">)

5. LLM call (temperature = MonologueTemperature)

6. Record usage to TokenBudget

7. Parse action from response:
   IDLE    → log thought privately
   MESSAGE → AntiSpamGuard → TelegramComposer → send
   RESEARCH → FluxBrain.ExecuteAutonomousResearchAsync() (sandboxed)
   OPINION → update Opinions dict with sentiment delta (-0.20 to +0.20)
   MEMORY  → CoreMemoryService.MaybeUpdateAsync()

8. Update drives: OnIdleCycle(10 min simulated)

9. Compute next interval
```

**The 5 actions and what they mean:**

| Action | Syntax | Meaning |
|--------|--------|---------|
| IDLE | `IDLE [private thought]` | Process internally, don't message, don't act |
| MESSAGE | `MESSAGE text to send` | Send to Telegram (goes through AntiSpamGuard first) |
| RESEARCH | `RESEARCH topic to investigate` | Run autonomous Swarm research (web search + summarize) |
| OPINION | `OPINION [topic] [+/-0.20] [reason]` | Update sentiment on topic |
| MEMORY | `MEMORY fact to remember` | Update CoreMemory via MaybeUpdateAsync |

---

## Parallel Task Execution (Swarm)

Handles requests like "clone this repo, run the tests, and summarize the results" where subtasks are independent.

```
User request
    → TaskDecomposer (LLM): break into subtasks with dependencies
    → DependencyGraph: topological sort, identify parallel groups
    → DynamicAgentPool: spawn CodeAgent/ScreenAgent per subtask
    → InMemoryMessageBus: pub/sub coordination
    → Results merged and summarized
```

**Supported Swarm Operations:**
- `WRITE_FILE`, `READ_FILE`, `EDIT_FILE`, `MOVE_FILE`, `DELETE_FILE`
- `PYTHON`, `POWERSHELL`
- `COMPILE` (dotnet build)
- `TEST` (dotnet test)

**Autonomous research mode** (used by Inner Voice RESEARCH action): same Swarm, but restricted — no shell execution, no file writes. Web search and summarization only.

---

## Self-Coding System

Davos can plan, review, implement, and commit changes to its own codebase.

```
User: "Add feature X"
    │
    ▼
PlannerAgent
  Reads relevant files, writes detailed implementation plan

    │
    ▼
DeliberatorAgent (3-role review)
  Skeptic    — finds flaws, edge cases, security issues
  Minimalist — finds unnecessary complexity, over-engineering
  Advocate   — defends the approach, argues for benefits
  Consensus plan emerges

    │
    ▼
CodeWriterAgent
  Implements changes
  On build failure: auto-fix loop (max 3 iterations)

    │
    ▼
BuildVerifier
  dotnet build — must succeed

    │
    ▼
RuntimeVerifier
  Smoke tests — basic functionality check

    │
    ▼
GitManager
  branch → commit → merge
```

---

## LLM Routing & Embeddings

### GeminiService (Primary)

**Model**: `gemini-2.5-flash`
**Endpoint**: `https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent`

**Temperature by use case:**

| Use case | Temperature |
|----------|-------------|
| Intent classifier | 0.1 |
| Task execution (JarvisCore) | 0.2 |
| Reflection/lesson writing | 0.3 |
| General chat | 0.7 |
| KG extraction | 0.1 |
| Query rewriting | 0.1 |
| Inner voice monologue | 0.30–0.90 (drive-computed) |

**Static instruction caching**: JarvisCore sends the 160+ line system instruction as a `system_instruction` top-level field (Gemini native). This is cached by Gemini across all 30 steps of a task, saving significant token cost. The static instruction covers identity, security rules, command format, examples, forbidden actions, and file paths.

**Rate-limit retry**: 429/503 responses → exponential backoff (1s, 2s, 4s) × 3 retries.

**Safety settings**: All set to `BLOCK_NONE`. Davos operates on personal PC data and needs unfiltered responses.

**Truncation handling**: If `finishReason == MAX_TOKENS`, auto-closes any open `[[...]]` command markers. This is fragile — assumes the specific marker format.

**Function calling**: `ChatWithAgentToolsAsync()` declares 2–3 tools in Gemini native function calling (AUTO mode). Gemini can return text AND function calls in the same response:
- `commitment_add(delay_seconds, description)` — schedule deferred action
- `execute_pc_task(description)` — request UI automation
- `inject_task_context(text)` — provide context to running task (only when task active)

### LocalLLMService (Fallback)

OpenAI-compatible API (e.g., Ollama). Configured via:
```json
"EnableLocalModel": true,
"LocalModelUrl": "http://localhost:8080",
"LocalModelId": "deepseek-r1:1.5b"
```

`ModelRouter` wraps both services and routes based on `EnableLocalModel` setting. No automatic fallback on failure — manual setting only.

### Embeddings: Local ONNX (Primary)

**Model**: `all-MiniLM-L6-v2` from Qdrant
**Dimensions**: 384
**Download**: automatic on first run (~90 MB total)
- `model.onnx`: from HuggingFace `Qdrant/all-MiniLM-L6-v2-onnx`
- `vocab.txt`: from HuggingFace `sentence-transformers/all-MiniLM-L6-v2`

**Pipeline**: tokenize (BERT WordPiece, max 512 tokens) → ONNX inference → mean pool → L2 normalize

**ONNX settings**: InterOpThreads=1, IntraOpThreads=2, all graph optimizations enabled.

**Thread safety**: ONNX InferenceSession is thread-safe after initialization. Init is async + semaphore-locked (safe to call concurrently — only initializes once).

### Embeddings: Gemini Fallback

**Model**: `text-embedding-004`
**Dimensions**: 768
**Used when**: local embedder not ready (model not downloaded yet) or explicitly disabled.

**Incompatibility warning**: If you use the app with local embeddings (384-dim) and then switch to Gemini embeddings (768-dim), the database will contain mixed-dimension vectors. Cosine similarity between different dimensions is meaningless. The schema stores `embedding_dims` per memory to track this, but the search code does not filter by dimension — this can produce garbage results.

---

## User Interface

### Window Properties
- Size: 340px wide, 180–600px dynamic height
- Style: borderless (`WindowStyle="None"`), transparent background
- Always on top (`Topmost="True"`)
- Hidden from taskbar and Alt+Tab (`WS_EX_TOOLWINDOW`)
- Background: gaussian blur layer behind content (adjustable radius 0–20px)
- Tint: dark semi-transparent overlay (adjustable opacity 0–1)

### Tabs

| Tab | Content | Key Behavior |
|-----|---------|-------------|
| **Chat** | Message list (ObservableCollection) | User messages right-aligned cyan; AI messages left-aligned white; typewriter stream |
| **Memory** | Recent MemoryService entries | Shows last ~20 stored memories with timestamp and source |
| **Tasks** | Running/completed task list | From FluxBrain.GetRunningTasks(), with status indicators |
| **Inner** | Inner Voice thought feed | Last 50 thoughts (newest first); 4 drive gauges (Boredom/Curiosity/Frustration/Energy) |

### Overlays

- **NeuroHUD**: Step progress (N/30), current command, validation badges, circuit breaker warnings. Shown during task execution.
- **Permission overlay**: Full-screen modal for destructive command confirmation. Blocks task until user responds.
- **Log overlay**: Full timestamped debug log output (accessible via Logs button).
- **Settings panel**: Collapsible, mutually exclusive with NeuroHUD.

### Hotkeys

| Key | Action |
|-----|--------|
| `Alt+F` | Toggle window visibility (global hotkey, registered on startup) |
| `Right Alt` (hold) | Push-to-talk (ElevenLabs continuous recording) |
| `Enter` in input box | Send message |

### Settings UI Controls

| Control | Setting | Effect |
|---------|---------|--------|
| Opacity slider | `WindowOpacity` | TintBrush alpha (not window opacity — avoids rendering artifacts) |
| Blur slider | `BlurRadius` | BackgroundBlur Gaussian radius |
| Name box | `WakeWord` | Wake word text + panel title |
| ElevenLabs key box | `ElevenLabsApiKey` | STT API credential |
| Language checkboxes | `SttRussian/English/Kazakh` | STT language codes |
| TTS toggle | `TtsEnabled` | Creates/disposes GeminiTtsService |
| Wake word toggle | `RequireWakeWord` | Gate voice input on wake word |
| Auto-minimize toggle | `AutoMinimizeOnComplete` | Hide window after task finishes |
| Telegram toggle | `TelegramEnabled` | Show credential fields + connect |
| API ID / Hash | `TelegramApiId/Hash` | MTProto credentials |
| Choose Chats button | `TelegramChatIds` | Opens chat picker dialog |
| Reset Session button | (delete files) | Delete session + force re-auth |
| Inner Voice toggle | `InnerVoiceEnabled` | Start/stop autonomous loop |
| Owner Chat ID | `TelegramOwnerChatId` | Telegram user ID for autonomous messages |
| Budget | `InnerVoiceBudgetDailyChars` | Daily char limit |
| DND hours | `DndStartHour/EndHour` | Quiet hours |

---

## Setup & Configuration

### Prerequisites

| Requirement | Purpose | Notes |
|-------------|---------|-------|
| Windows 10/11 x64 | Runtime | WPF + P/Invoke; no cross-platform |
| .NET 10 SDK | Build | `net10.0-windows10.0.19041.0` |
| Gemini API key | Primary LLM | Required — app won't function without it |
| ElevenLabs API key | Voice STT | Optional — text input works without it |
| Python 3.x | Script execution | Optional — for RUN_PYTHON commands |
| Node.js | Script execution | Optional — for RUN_NODE commands |
| Google Chrome | Browser context | Optional — requires companion extension |
| Ollama (or compatible) | Local LLM fallback | Optional |

### Build

```bash
git clone https://github.com/Dafypig228/Fflux.git
cd Fflux
dotnet restore
dotnet build
dotnet run --project FluxCore
```

The ONNX model downloads automatically on first run (~90 MB). Playwright Chromium downloads (~120 MB) on first `[[BROWSER_OPEN:...]]` command.

### Configuration File

Location: `%APPDATA%\Davos\settings.json` (auto-created with defaults on first run)

```json
{
  "WindowOpacity": 0.95,
  "BlurRadius": 10.0,
  "WindowTop": null,
  "WindowLeft": null,

  "WakeWord": "Davos",
  "RequireWakeWord": true,
  "AutoMinimizeOnComplete": true,
  "ValidationDepth": "Fast",

  "EnableLocalModel": false,
  "LocalModelUrl": "http://localhost:8080",
  "LocalModelId": "deepseek-r1:1.5b",

  "ElevenLabsApiKey": "",
  "SttRussian": true,
  "SttEnglish": true,
  "SttKazakh": false,

  "TtsEnabled": false,
  "TtsVoice": "Kore",

  "TelegramEnabled": false,
  "TelegramApiId": 0,
  "TelegramApiHash": "",
  "TelegramChatIds": [],

  "InnerVoiceEnabled": false,
  "TelegramOwnerChatId": 0,
  "TelegramOwnerChannelId": 0,
  "InnerVoiceBudgetDailyChars": 500000,
  "DndStartHour": 23,
  "DndEndHour": 8
}
```

**`TtsVoice` options**: `Aoede`, `Charon`, `Fenrir`, `Kore`, `Puck`
**`ValidationDepth` options**: `Fast` (failures only), `Normal`, `Thorough` (all screen commands)

### Telegram Setup

1. Go to [my.telegram.org](https://my.telegram.org) → API development tools
2. Create an application, copy `api_id` (integer) and `api_hash` (32-char hex string)
3. In Davos Settings: enable Telegram, paste credentials
4. A WPF dialog appears: enter your phone number → enter verification code → enter 2FA password (if set)
5. Session saved to `%APPDATA%\Davos\telegram.dat`
6. Click "Choose Chats" to select which DMs/groups to monitor (empty = all)
7. Status shows `Connected (N msgs)` when working

**Troubleshooting**: If auth fails, click "Reset Session" — this deletes `telegram*` files and forces fresh authentication.

### Inner Voice Activation

1. Set up Telegram first (required for message delivery)
2. Find your Telegram user ID (message @userinfobot on Telegram)
3. Set `TelegramOwnerChatId` to your numeric user ID
4. Enable `InnerVoiceEnabled` in Settings (or in settings.json)
5. Optionally configure `DndStartHour`/`DndEndHour` (23/8 = quiet from 11 PM to 8 AM)
6. Restart the application

### Chrome Extension Setup

The Davos Chrome extension is required to receive browser context:

1. Open Chrome → `chrome://extensions/` → Enable Developer mode
2. "Load unpacked" → select the `ChromeExtension/` directory from the repo
3. The extension sends DOM text + URL to `http://localhost:27834/` automatically

VS Code extension works similarly — load from the `VSCodeExtension/` directory.

### Local Model Setup (Ollama)

```bash
# Install Ollama from https://ollama.ai
ollama pull deepseek-r1:1.5b
# Ollama serves on http://localhost:11434 by default
```

In settings.json:
```json
"EnableLocalModel": true,
"LocalModelUrl": "http://localhost:11434",
"LocalModelId": "deepseek-r1:1.5b"
```

Note: local models don't support vision or function calling. Chat will work; task execution quality will be significantly lower.

---

## Data Files Reference

All files in `%APPDATA%\Davos\`:

| File | Format | Purpose | Growth |
|------|--------|---------|--------|
| `settings.json` | JSON | Application configuration | Fixed, ~2 KB |
| `core_memory.json` | JSON | MemGPT personality blocks (Core 700 chars + Working 450 chars) | Fixed size, updates in-place |
| `inner_state.json` | JSON | Autonomous drive state, opinions, message queue | Grows with opinions dict; opinions unbounded |
| `session_history.json` | JSON | Last 50 conversation messages | Fixed (rolling buffer) |
| `datalake.db` | SQLite | Append-only event log for all sensors | **Grows forever** — never pruned |
| `davos_memory.db` | SQLite | Vector embeddings (384 or 768 dim, binary blob) | Grows with DataLake ingestion |
| `knowledge_graph.db` | SQLite | Entity nodes + relationship edges | **Grows forever** — never pruned |
| `commitments.db` | SQLite | Deferred task schedule | **Done/Failed entries never removed** |
| `telegram.dat` | Binary | WTelegramClient MTProto session | ~50 KB, stable after auth |
| `chat_logs/YYYY-MM-DD.txt` | Plain text | Daily chat log | New file per day, no size limit |
| `models/model.onnx` | ONNX | all-MiniLM-L6-v2 weights | ~90 MB, fixed after download |
| `models/vocab.txt` | Text | BERT WordPiece vocabulary | ~226 KB, fixed |

`knowledge.json` (Hippocampus) is stored next to the executable, not in `%APPDATA%`.

---

## Known Issues & Current Problems

This section is an honest account of current defects and limitations.

### Data & Storage

**DataLake never pruned**
`datalake.db` grows forever. On an active machine with all sensors enabled, this can reach gigabytes over months. There is no archival, TTL, or compression mechanism.

**KnowledgeGraph nodes never pruned**
KG_Node and KG_Edge tables grow indefinitely. Low-mentioned nodes (seen_n=1) are never removed. No similarity-based deduplication — "Alex" and "Alexander" become two separate nodes with no connection.

**CommitmentStore Done/Failed entries never removed**
`commitments.db` accumulates all historical commitments forever. No periodic cleanup runs.

**SemanticMemory embedding dimension mismatch**
If you switch between local embeddings (384-dim) and Gemini embeddings (768-dim), the database will contain mixed-dimension vectors. Cosine similarity between vectors of different dimensions produces garbage. There is no migration path, no warning, and the search code does not filter by dimension.

**All databases are plaintext SQLite**
If your machine is shared or unencrypted, all DataLake content — including Telegram messages, clipboard history, browser URLs — is readable by anyone with file access.

### Code Quality

**No unit tests**
The entire codebase has zero automated tests. Every regression is discovered manually. Refactoring is done blind.

**Manual dependency injection**
All services are instantiated in `MainWindow.InitializeServices()`, split across `MainWindow.xaml.cs` and `MainWindow.Settings.cs`. There is no IoC container. Adding a new service requires careful ordering and constructor-coupling — easy to introduce circular dependencies or init-order bugs.

**ChatLogger has no file locking**
`ChatLogger.Log()` and `ChatLogger.LogError()` open-write-close with no mutual exclusion. Concurrent log calls from multiple threads (which happen regularly) can corrupt log files.

**No streaming responses**
The entire LLM response is buffered before display. For long responses, this creates a noticeable delay. The typewriter effect is cosmetic, playing after the full response arrives — not true streaming.

### LLM & API

**GeminiService truncation detection is fragile**
When `finishReason == MAX_TOKENS`, the code auto-appends `]]` to close any open command markers. This assumes the specific `[[CMD:arg]]` format. If the command format ever changes, truncation recovery will produce malformed commands silently.

**No graceful handling of invalid Gemini API key**
If the API key is wrong or missing, the first LLM call fails with an HTTP error that propagates as an exception. There is no startup validation or user-friendly error dialog.

**GeminiService safety settings are all BLOCK_NONE**
Content filtering is completely disabled. This is intentional for personal PC use (clipboard content, personal messages, etc.) but means any malicious content in sensor data passes unfiltered to the LLM prompt.

**Token budget approximates chars/4 ≈ tokens**
Actual Gemini token counts can differ significantly, especially for Cyrillic text (which tokenizes less efficiently). The daily budget may be exhausted faster than expected.

### Embeddings & Memory

**LocalEmbeddingService assumes ONNX output order**
The code uses `results.First()` to get `last_hidden_state`. If the ONNX model has multiple outputs in a different order (e.g., after a model update), it will silently use the wrong tensor and produce garbage embeddings.

**LocalEmbeddingService has no download retry**
If the model download fails (network error, disk full), `IsReady` stays `false` and the service silently falls back to Gemini embeddings with no user notification. You may not realize you're generating 768-dim vectors and mixing them with existing 384-dim vectors.

**MemoryEngine has a hard-coded source list**
The DataLake keyword scan queries only: `telegram`, `clipboard`, `chrome`, `notification`, `terminal`. Adding a new sensor type requires editing `MemoryEngine.cs` directly — it is not automatic.

**KnowledgeGraph QueryRows column naming confusion**
`DataLakeService.QueryRows()` returns `List<(string ts, string content)>`. In `KnowledgeGraphService`, the code accesses `rows[^1].ts` to get the last event ID — but the first column in the SELECT is actually `id`, not a timestamp. This works because SQLite returns columns in SELECT order, but is deeply confusing and would silently break if the query changes.

### Sensors

**Telegram GetAvailableChatsAsync has no pagination**
Fetches all dialogs at once. For accounts with 1000+ chats this is slow and may time out.

**wpndatabase.db notification polling may break**
Windows notification history is read from `wpndatabase.db` whose path is guessed based on typical Windows 10/11 installation patterns. Non-standard configurations or future Windows updates may break this path.

**Python/Node.js finder tries 3–4 hardcoded paths**
`FindPython()` and `FindNode()` try a small set of known installation locations. Non-standard installs (Python via Windows Store, Node via NVM) may not be found, and the error message is generic.

**Terminal sensor only captures Davos-executed commands**
The terminal ring buffer only contains commands that Davos itself ran via `CodeExecutionAgent`. It does not capture the user's own terminal sessions.

**Chrome extension required but not standard**
The Chrome bridge extension is custom and not published to the Chrome Web Store. Users must install it manually in developer mode. Chrome may flag or disable it after updates.

### Inner Voice

**Inner Voice requires Telegram for message delivery**
There is no alternative delivery channel. If Telegram is not set up or disconnects, MESSAGE actions silently fail. The UI shows thoughts in the Inner tab, but that's passive display — you don't see it unless you open the app.

**Observation truncation may cut mid-sentence**
All observation caps (800 chars for Chrome, 600 for Telegram, etc.) are hard character limits with no sentence-boundary awareness. An important observation may be cut off mid-sentence, potentially changing its meaning.

**Opinions dictionary is unbounded**
The `InnerState.Opinions` dictionary grows every time a new topic is encountered. There is no eviction of old or stale opinions.

### Architecture

**Timer type ambiguity**
The codebase uses `System.Threading.Timer` but also imports `System.Windows.Forms` (for forms interop). Any `Timer` declaration without full qualification may resolve to the wrong type silently.

**CoreMemoryService lacks direct block editing**
The only way to update CoreMemory blocks is via `MaybeUpdateAsync(userMessage, davosReply)`. There is no API to directly set or inspect a block's content, which makes testing and manual correction awkward.

**SwarmOrchestrator requires an explicit working directory string**
`SwarmOrchestrator.ExecuteGoalAsync(goal, workingDirectory, ct)` — passing the wrong directory (or empty string) silently misdirects all file operations.

**Session_key reuses api_hash**
The WTelegramClient session_key is set to the Telegram api_hash (a 32-character hex string = valid 256-bit key). This is not a security vulnerability per se, but it is non-cryptographic reuse — the api_hash was not designed to be a session encryption key.

**Playwright Chromium downloads 120 MB on first browser command**
There is no UI feedback during this download. The first `[[BROWSER_OPEN:...]]` command appears to hang for 30–60 seconds with no indication of progress.

**ElevenLabs STT requires a paid subscription**
Scribe v2 Realtime is not available on ElevenLabs free tier. Without a paid key, voice input silently fails. There is no fallback STT provider.

---

## Pros & Cons

### Pros

**Deep, structured context per turn**
At 7000+ characters of injected context per task step (RAG + passive sensors), Davos has far more situational awareness than a standard chatbot. It knows what you were working on, what's in your clipboard, recent Telegram messages, current Chrome tab, VS Code file, and recent git commits — all without you explaining anything.

**Local embeddings keep most data on-device**
The all-MiniLM-L6-v2 ONNX model runs on CPU. Once downloaded, semantic search produces no external API calls and keeps personal data local.

**Append-only DataLake = zero data loss**
Every sensor event is written once and never deleted. This creates a permanent, queryable audit trail of everything Davos observed.

**Static system instruction caching**
Sending a 160+ line system instruction to Gemini on every task step would be expensive. Davos sends it as a native `system_instruction` field that Gemini caches server-side across the entire task, saving significant token cost on long tasks.

**Loop detection prevents infinite spin**
Four-layer loop detection (exact repeat, click loop, stall, circuit breaker) ensures the executor doesn't get stuck repeating the same failed action. Each detection layer injects actionable suggestions.

**Drive-based autonomous behavior**
The four-drive system with time-of-day energy curve means autonomous behavior feels organic rather than scripted. At 2 AM, Davos is quiet. After discovering an interesting git commit, it's more likely to research. This is a coherent behavioral model, not random.

**Privacy filter on all observations**
Before any sensor data reaches the Inner Voice LLM, it passes through a three-layer privacy filter (PII regex, sensitive domain blocklist, keyword context). Financial and medical data is silently dropped with no LLM exposure.

**Atomic writes for all critical state**
`inner_state.json`, `core_memory.json`, and other critical files use temp-file + atomic move pattern. A crash mid-write cannot corrupt the state file.

**Confidence gating on destructive commands**
PowerShell/shell commands below 0.7 confidence trigger a permission overlay before execution. This prevents accidental destructive actions while still allowing high-confidence automation to proceed without friction.

**Per-command retry with different strategies**
Rather than naive "retry 3 times," JarvisCore uses contextually different retry strategies. A failed CLICK might scroll first, then retry. A failed TYPE might refocus the window. This is significantly more effective than blind retries.

**Query rewriting improves semantic recall**
"Did Alex send the API credentials yesterday?" gets rewritten to "Alex API credentials sent" before semantic search. This dramatically improves retrieval quality for natural-language queries.

**GraphRAG pivot**
After semantic search finds relevant memories, MemoryEngine extracts entity names and looks them up in the knowledge graph. This surfaces relationship context that pure vector search misses.

**Mid-task context injection**
If you realize the task needs correction while it's running, type it in the input box. It gets prepended with `[URGENT MID-TASK UPDATE FROM USER]` to the next step's context.

**Stream-based Telegram session**
The MTProto session uses a custom `PersistOnFlushStream` with atomic writes. This bypasses WTelegramClient's default AES-encrypted file which had NTFS file-locking issues on Windows.

### Cons

**Windows-only, permanently**
WPF, P/Invoke for mouse/keyboard/OCR, `Windows.Media.Ocr`, `Windows.Runtime`, `wpndatabase.db`, `WM_CLIPBOARDUPDATE` — this codebase is fundamentally Windows-only with no abstraction layer for porting.

**Requires Gemini API key**
The primary LLM is a paid cloud service. The local fallback (Ollama) works for chat but loses vision, function calling, and output quality significantly. There is no offline-capable primary mode.

**No unit tests**
This is the biggest technical debt item. Any refactoring is dangerous. Regressions are discovered in production (on your desktop). Adding tests now would require significant refactoring of manual DI first.

**Manual DI makes the codebase fragile**
`MainWindow.InitializeServices()` is a 150+ line constructor sequence where order matters. A service added in the wrong place causes null references. There is no compile-time guarantee that services are wired correctly.

**All persistent stores grow unbounded**
DataLake, KnowledgeGraph, CommitmentStore, ChatLogger files — none have any pruning, archival, or TTL. A machine running Davos continuously for a year will accumulate a multi-gigabyte `datalake.db`.

**Inner Voice delivery requires Telegram**
The autonomous companion is functionally mute without Telegram. There is no in-app notification, no email fallback, no local desktop notification for missed thoughts.

**No streaming LLM responses**
Responses buffer completely before display. The typewriter effect is cosmetic. On slow networks or large responses, this creates a jarring experience.

**30-step task limit**
Complex automation can hit the 30-step ceiling. When this happens, the task fails with a suggestion to try a more specific request. There is no continuation mechanism.

**No conversation export**
`session_history.json` holds 50 messages. There is no export-to-markdown, no archive viewer for older conversations. Chat logs exist in `%APPDATA%\Davos\chat_logs\` but are only accessible as raw dated text files.

**Playwright downloads 120 MB silently**
The first browser automation command takes 30–60 seconds with no UI feedback. Users assume it is hung.

**ElevenLabs STT is paid**
No free alternative is wired in. If you do not pay for ElevenLabs, voice input is unavailable. The app works fine with text-only input, but the voice-first experience is paywalled.

**All local databases are plaintext**
`datalake.db` contains Telegram messages, clipboard history, browser URLs, and file activity. On a shared or unencrypted machine, this is a privacy risk.

**No multi-user support**
The app is designed for one person. All storage is single-user, settings are global, and the autonomous companion messages one Telegram account.

---

## Tech Stack

| Layer | Technology | Version | Notes |
|-------|-----------|---------|-------|
| **Runtime** | .NET | 10 (x64 only) | `net10.0-windows10.0.19041.0` |
| **UI** | WPF | .NET 10 | Always-on-top transparent overlay |
| **Primary LLM** | Gemini 2.5 Flash | API v1beta | `generativelanguage.googleapis.com` |
| **Local LLM** | OpenAI-compatible API | Any | Tested with Ollama + DeepSeek |
| **Local Embeddings** | ONNX all-MiniLM-L6-v2 | Qdrant variant | 384-dim, CPU inference |
| **Cloud Embeddings** | Gemini text-embedding-004 | v1beta | 768-dim fallback |
| **ONNX Runtime** | Microsoft.ML.OnnxRuntime | 1.20.1 | |
| **BERT Tokenizer** | FastBertTokenizer | 1.0.28 | WordPiece, max 512 tokens |
| **Database** | SQLite via Microsoft.Data.Sqlite | 10.0.1 | DataLake, Memories, KG, Commitments |
| **SQLite (alt)** | System.Data.SQLite.Core | 1.0.119 | Legacy driver used by some services |
| **Screen automation** | Win32 P/Invoke | — | Mouse, keyboard, window management |
| **UI Automation** | UIAutomation (Windows) | — | Element enumeration for click commands |
| **OCR** | Windows.Media.Ocr | Windows Runtime | Requires Windows 10+, initialized via WinRT |
| **Browser automation** | Microsoft.Playwright | 1.57.0 | Headless Chromium, ~120 MB on first use |
| **Audio I/O** | NAudio | 2.2.1 | Microphone capture for STT |
| **STT** | ElevenLabs Scribe v2 Realtime | — | Paid API, multilingual streaming |
| **TTS** | Gemini Live API | — | Optional, 5 voice options |
| **TTS (fallback)** | System.Speech | 9.0.0 | Windows built-in |
| **Telegram** | WTelegramClient | 3.5.8 | MTProto, stream session mode |
| **Windows Runtime** | Microsoft.Windows.CsWinRT | 2.1.1 | For Windows.Media.Ocr, SMTC |
| **Graphics** | System.Drawing.Common | 8.0.0 | Screenshot capture, image manipulation |
| **WMI** | System.Management | 9.0.0 | Process management, system metrics |

---

## Project Structure

```
FluxCore/
│
├── Core AI
│   ├── FluxBrain.cs              — Central router: queue, intent classify, commitment scheduler
│   ├── JarvisCore.cs             — Task executor: main 30-step loop
│   ├── JarvisCore.Commands.cs    — Command extraction, dispatch, retry logic
│   ├── JarvisCore.Context.cs     — Dynamic context builder (RAG + passive)
│   └── GeminiService.cs          — Gemini API: chat, vision, embedding, function calling
│
├── Memory
│   ├── MemoryEngine.cs           — Unified RAG: semantic + GraphRAG + lake keyword
│   ├── MemoryService.cs          — Vector store (SQLite), time-decay search, query rewriting
│   ├── DataLakeService.cs        — Append-only event log (SQLite)
│   ├── KnowledgeGraphService.cs  — Entity extraction (30-min batch), graph queries
│   ├── LocalEmbeddingService.cs  — ONNX all-MiniLM-L6-v2 CPU embeddings
│   ├── TextChunker.cs            — Source-aware text chunking before embedding
│   ├── CommitmentStore.cs        — Deferred task scheduler (SQLite)
│   └── ChatLogger.cs             — Daily chat log files
│
├── Sensors
│   ├── SensoryCortex.cs          — Screenshot, OCR, UIAutomation, focus timeline
│   ├── ChromeBridgeService.cs    — HTTP server :27834 for Chrome/VS Code extension
│   ├── TelegramService.cs        — MTProto stream client: monitor + send
│   ├── ClipboardService.cs       — WM_CLIPBOARDUPDATE hook, ring buffer
│   ├── FileWatcherService.cs     — FileSystemWatcher on key directories
│   ├── GitWatcherService.cs      — Git repo change monitor
│   ├── NotificationService.cs    — wpndatabase.db polling (Windows notifications)
│   ├── EventLogService.cs        — Windows Event Log EntryWritten handler
│   └── SystemMetricsService.cs   — CPU/RAM/disk periodic sampling
│
├── Agents
│   ├── WindowsAutomationAgent.cs — Win32 P/Invoke mouse/keyboard control
│   ├── ExecutionAgent.cs         — File ops, Playwright web, app launching
│   ├── CodeExecutionAgent.cs     — PowerShell/Python/Node.js sandbox
│   ├── CodeExecutionAgent.Scripts.cs — Script execution implementation
│   ├── ValidatorAgent.cs         — Post-command visual/stdout verification
│   ├── ReflectionAgent.cs        — Failure analysis, lesson generation
│   └── Hippocampus.cs            — Lesson store (knowledge.json)
│
├── Inner Voice (autonomous companion)
│   ├── InnerVoice/InnerVoiceService.cs  — Main autonomous loop
│   ├── InnerVoice/InnerState.cs         — Persistent state (drives, opinions, queue)
│   ├── InnerVoice/DrivesEngine.cs       — 4 drives + energy curve + interval calc
│   ├── InnerVoice/PrivacyFilter.cs      — 3-layer PII/domain/keyword filter
│   ├── InnerVoice/AntiSpamGuard.cs      — DND + gaming + AFK + rate limit
│   ├── InnerVoice/TokenBudget.cs        — Daily char budget with soft/hard limits
│   └── InnerVoice/TelegramComposer.cs   — Human-paced multi-part Telegram sender
│
├── LLM
│   ├── ILLMService.cs            — Interface (ChatWithHistory, GetEmbedding, etc.)
│   ├── GeminiService.cs          — Primary implementation
│   ├── LocalLLMService.cs        — OpenAI-compatible fallback
│   └── ModelRouter.cs            — Routes between Gemini and local based on settings
│
├── Swarm (parallel multi-agent)
│   ├── SwarmOrchestrator.cs
│   ├── TaskDecomposer.cs
│   ├── DependencyGraph.cs
│   ├── DynamicAgentPool.cs
│   └── InMemoryMessageBus.cs
│
├── Self-Coding
│   ├── SelfCodingOrchestrator.cs
│   ├── PlannerAgent.cs
│   ├── DeliberatorAgent.cs
│   ├── CodeWriterAgent.cs
│   ├── BuildVerifier.cs
│   ├── RuntimeVerifier.cs
│   └── GitManager.cs
│
├── UI
│   ├── MainWindow.xaml           — WPF layout: tabs, overlays, settings controls
│   ├── MainWindow.xaml.cs        — Service init, hotkeys, core event handlers
│   ├── MainWindow.Chat.cs        — Tab management, message display, typewriter stream
│   ├── MainWindow.Settings.cs    — Config persistence, Telegram init, Inner Voice wiring
│   └── TelegramAuthDialog.cs     — WPF dialog for phone/code/2FA input
│
├── Storage
│   └── AppSettings.cs            — Settings model: Load/Save JSON
│
└── FluxCore.csproj               — .NET 10, WPF, all NuGet dependencies
```

---

## Architecture Decisions

**Why no IHostedService?**
WPF applications have their own SynchronizationContext and lifecycle. `IHostedService` requires a generic host which conflicts with WPF's single-thread UI model. All background work uses `System.Threading.Timer` (non-UI timer) or `Task.Run` with explicit UI dispatch via `Dispatcher.InvokeAsync`.

**Why manual DI instead of IoC container?**
The project started as a prototype with tight coupling between UI and services. Migrating to an IoC container requires interface extraction across the entire codebase first. This is tracked as future work.

**Why Gemini 2.5 Flash over GPT-4 or Claude?**
Gemini 2.5 Flash offers native vision + function calling in a single API, competitive pricing, 1M token context (useful for long tasks), and server-side caching of system instructions. The 1M context is particularly valuable for the 160+ line static system instruction cached across all 30 task steps.

**Why MTProto (WTelegramClient) instead of Telegram Bot API?**
Bot API can only receive messages in groups where the bot is added as a member. MTProto client monitors personal DMs and all groups as the actual user — critical for passive observation of real conversations.

**Why SQLite everywhere instead of a proper vector or time-series database?**
Simplicity. The target audience is a single person on a single machine. SQLite handles all required loads with zero infrastructure overhead. Vector search at under 100K memories is fast enough with in-memory cosine similarity.

**Why append-only DataLake instead of updating records?**
Immutability makes debugging, auditing, and reasoning simpler. If Davos misunderstands something, you can trace exactly what raw events it saw. The trade-off is unbounded growth — a known problem with no current solution.
