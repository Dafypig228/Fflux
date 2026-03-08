# Davos (FluxCore)

An AI-powered Windows desktop assistant that can see your screen, control your mouse and keyboard, execute code, and learn from its mistakes.

## What It Does

Davos is a multi-agent system that sits as an overlay on your desktop. You talk to it (voice or text), and it figures out whether you want a conversation, a PC task (clicking, typing, opening apps), a multi-step parallel operation, or a code change to its own source.

- **Screen automation** — Clicks, types, scrolls, drags using coordinates. Vision-based: takes screenshots, runs OCR, reads UI elements.
- **Code execution** — Runs Python, PowerShell, Node.js, CMD in a sandboxed temp directory with timeouts.
- **Self-healing tasks** — If a click misses, it scrolls and retries. If typing fails, it falls back to clipboard paste. Up to 30 steps and 3 retries per task.
- **Learning** — Stores lessons from past tasks (Hippocampus). Successful patterns get reinforced, failures get penalized. Lessons are recalled for similar future tasks.
- **Parallel execution** — Swarm system decomposes complex goals into subtasks, builds a dependency graph, and runs independent tasks on multiple CodeAgents simultaneously.
- **Self-coding** — Can modify its own source code: plan, deliberate (3 reviewers), implement, build, test, git commit.

## Architecture

```
User (voice/text)
    |
    v
FluxBrain (intent classifier)
    |
    +---> Chat (conversation with history)
    +---> JarvisCore (PC task: plan-execute-verify-reflect loop)
    +---> SwarmOrchestrator (parallel multi-agent tasks)
    +---> SelfCodingOrchestrator (modify own code)
```

**Key components:**
- `FluxBrain` — Routes requests. Never blocks, never drops.
- `JarvisCore` — Executes screen tasks step-by-step with LLM guidance.
- `Hippocampus` — Long-term lesson memory (JSON-based, confidence-scored).
- `MemoryService` — Semantic memory with embeddings in SQLite.
- `SensoryCortex` — Screenshots, OCR, UI element detection.
- `WindowsAutomationAgent` — Win32 P/Invoke mouse/keyboard control.
- `CodeExecutionAgent` — Script sandbox with path normalization.
- `SwarmOrchestrator` — Multi-agent parallel execution with dependency tracking.

## Tech Stack

- **Runtime:** .NET 10 (x64), WPF
- **LLM:** Gemini 2.5 Flash (primary), local models via OpenAI-compatible API (optional)
- **Vision:** Windows.Media.Ocr, System.Windows.Automation
- **Audio:** NAudio (16kHz recording, Gemini transcription)
- **Browser:** Microsoft.Playwright (headless Chromium)
- **Database:** SQLite via Microsoft.Data.Sqlite (embeddings)
- **Automation:** Win32 API (SetCursorPos, mouse_event, keybd_event, UIAutomation)

## Prerequisites

- Windows 10/11 (x64)
- .NET 10 SDK
- Gemini API key (set as environment variable or in settings)
- Python 3.x (optional, for script execution)
- Node.js (optional, for JS script execution)

## Build & Run

```bash
dotnet restore
dotnet build
dotnet run --project FluxCore
```

## Hotkeys

| Key | Action |
|-----|--------|
| Alt+F | Toggle window visibility (global) |
| Right Alt (hold) | Push-to-talk |
| Enter | Send text input |

## Configuration

Settings are stored in `%APPDATA%\Davos\settings.json`:
- Window opacity and blur
- Wake word
- Auto-minimize on task complete
- Validation depth (Fast / Normal / Thorough)
- Local model URL and ID

## Project Structure

```
FluxCore/
  FluxBrain.cs            # Central intent router
  JarvisCore.cs           # Task execution engine (+ .Context, .Commands, .Response)
  Hippocampus.cs          # Reflexion-based learning
  MemoryService.cs        # Semantic memory (SQLite + embeddings)
  SensoryCortex.cs        # Screen capture & OCR
  WindowsAutomationAgent.cs  # Win32 mouse/keyboard (+ .Click, .Type, .Input)
  CodeExecutionAgent.cs   # Script sandbox (+ .Scripts, .Files)
  ExecutionAgent.cs       # File/web/app operations (Playwright)
  AudioService.cs         # Voice input (NAudio + Gemini STT)
  MainWindow.xaml.cs      # UI bootstrapper (+ .Chat, .Input, .Settings, .Hud, .Permissions)
  LLM/
    ILLMService.cs        # LLM interface
    GeminiService.cs      # Gemini API client
    LocalLLMService.cs    # Local model client
    ModelRouter.cs        # Smart routing with fallback
  Swarm/
    SwarmOrchestrator.cs  # Multi-agent coordinator
    TaskDecomposer.cs     # Goal -> subtasks via LLM
    DependencyGraph.cs    # DAG-based task ordering
    DynamicAgentPool.cs   # Auto-scaling agent pool
    ConflictResolver.cs   # File lock conflict resolution
    Agents/
      BaseAgent.cs        # Abstract agent base
      CodeAgent.cs        # Background code agent
      ScreenAgent.cs      # Visual testing agent (Hyper-V / local)
    Infrastructure/
      MessageBus.cs       # Pub/sub + task queue
      AgentRegistry.cs    # Agent tracking + health
      FileLockManager.cs  # Concurrent file access control
    Environment/
      IScreenEnvironment.cs   # Isolated screen interface
      HyperVEnvironment.cs    # Hyper-V VM implementation
  SelfCoding/
    SelfCodingOrchestrator.cs  # Plan-deliberate-code-build-test-git
    PlannerAgent.cs       # Implementation planning
    DeliberatorAgent.cs   # 3-role review (Skeptic/Minimalist/Advocate)
    CodeWriterAgent.cs    # Code generation + auto-fix
    BuildVerifier.cs      # dotnet build wrapper
    RuntimeVerifier.cs    # Smoke test runner
    GitManager.cs         # Branch/commit/merge
    SelfCodingTypes.cs    # Data types
```

## Safety

- Never acts on its own window (foreground check)
- Destructive commands (shell, PowerShell) require user confirmation
- Wrong-window detection halts execution immediately
- File deletion uses Recycle Bin, not permanent delete
- Self-protection: blocks kill commands targeting own process
- Confidence gate: uncertain intent classifications ask the user
