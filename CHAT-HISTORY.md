# Davos / FluxCore — short history of the Claude sessions (2026-06-11 … 06-12)

Compressed record of what happened, for humans and future AI sessions.
Details and invariants live in `FluxCore/CLAUDE.md`. Fixed state: git checkpoint `b2cfc1c`.

## Session 1 — full audit + first fix wave

Adil: *"Davos is smart on words only — can't clean my desktop, his verifying system is
wrong, everything is clumsy, and he's veryyy slow. Don't trust the README, look yourself."*

Findings (own code reading, no docs trusted):
- **Verification was dead code**: visual validation ran only on FAILED commands but its
  verdict was applied only to SUCCESSFUL ones — never both, so it burned vision calls and
  changed nothing. Validator prompt promised two screenshots, sent one; verdict parsing
  broke on any markdown.
- **Memory self-poisoning**: every failed command (incl. PowerShell) stored a permanent
  lesson "CLICK:... failed, use coordinates instead" → desktop-cleanup scripts taught
  Davos to abandon the shell and click around.
- **Clumsiness**: coordinates reported to the AI in full resolution while it clicks in
  half-resolution (clicks landed 2× off); failed clicks silently scrolled the page;
  safety-stops randomly opened Chrome; every task result appeared twice in chat;
  SCROLL/READ_LOG banned as "loops" after 2 uses.
- **Slowness #1 cause**: Gemini 2.5 Flash with no `thinkingConfig` → hidden "thinking" on
  every one of ~30 steps. Plus 3× blind re-runs of failing scripts, extra serial strategy
  LLM call, reflexion blocking the result.

All fixed; build green.

## Session 2 — "works on sticks" + hallucinations + cleanup

Adil: *"TASK_COMPLETE just gets ignored. Tons of these bugs. Even if smth works it doesn't
mean it's correct. Model hallucinates: asked for open Chrome tabs, he invented
`Chrome.Application` COM, Shell.Application, UIA by PID. Think you're in the Truman Show —
trust nothing."* (Davos eventually solved tabs himself via CTRL+SHIFT+A — clever.)

Findings:
- `[[RESPOND:...]]` was advertised and parsed but had NO executor case → every final answer
  died as "Unknown command"; tasks "completed" by accident with template text.
- `TASK_FAILED` was OR-ed into `TASK_COMPLETE` → failed tasks reported as "Task Completed".
- Four task-exit paths; three skipped cleanup (stale window lock, stuck HUD, no trace).
- Hallucination root cause = capability gap: ChromeBridge collected pages but exposed them
  to nobody, and the prompt said "NEVER say I cannot" → invented APIs. Fixed by grounding:
  `Chrome.GetRecentPages()` in RUN_CSHARP + `<grounding>` section naming the fake APIs +
  honest TASK_FAILED allowed + CTRL+SHIFT+A taught as the proven screen route.
- Dead-code cleanup: a second, unreachable execution pipeline in MainWindow.Permissions.cs,
  SelfCoding\ (8 files, no caller), 3 dead FluxBrain handlers, legacy ValidateAsync,
  empty/legacy files, `nul` file. Hardcoded Gemini API key found in source → moved to
  `%APPDATA%\Davos\settings.json`. Stale CLAUDE.md/NOTES.md (wrong instructions that
  mislead AI sessions) replaced with an accurate invariants doc.

Verdict on the project: good ideas (passive sensors, persona, router/executor split),
killed by accretion — layers added by different AI sessions, never reconciled, no tests,
no outcome measurement. The string `[[COMMAND:arg]]` protocol is the biggest structural
weakness; native function calling is the recommended migration.

## Session 3 — the overwrite + re-apply everything + vision

Adil copied his master project over the worktree → **all fixes from both sessions wiped**
(old code back, SelfCoding back, a NEW API key hardcoded again after rotating the old one).

Re-applied all ~26 fixes in one run (see catalog in CLAUDE.md), re-deleted dead code,
moved the new key to settings.json, rebuilt (0 errors), and **created a git checkpoint
`b2cfc1c` inside the project** so an overwrite can never again silently destroy work.

Vision discussion (Adil's plan):
- **Gemini Live watching the screen** instead of camera → feasible: stream ~1fps screenshots
  over the Live WebSocket. Live = eyes/voice; JarvisCore = hands; bridge between them.
- **Minecraft** → via Mineflayer bot (Davos writes/runs/steers it), not pixel clicking
  (2–5s/step loop can't play real-time). **Roblox** → honestly not feasible (no API, anti-cheat).
- **"Make him wise / connect the dots"** → routing (Flash executes, 2.5-pro plans/reflects),
  a synthesis loop that turns DataLake events into CoreMemory conclusions, honest feedback
  loops (the bug fixes), and an eval set to measure progress. Wisdom is built in the
  harness, not prompted into the model.

## Standing rules learned the hard way

1. Work INSIDE this folder, or sync both ways — overwrites destroyed two sessions of work.
2. Run the marker check from CLAUDE.md at session start to detect overwrites.
3. Never hardcode API keys (leaked twice).
4. "It works" ≠ "it's correct" — Adil's standard is works-by-design; verify, don't assume.
5. Commit checkpoints to the project git after meaningful changes.
