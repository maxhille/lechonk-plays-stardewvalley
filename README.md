# Lechonk Plays Stardew Valley

Mistral Large 4 ("Le Chonk") plays Stardew Valley through a custom harness: the model
writes small Lua scripts that drive a SMAPI mod inside the game, one bounded action at
a time, and plans its days around them.

Built for the ["Le Chonk wants to play" community challenge](https://app.notion.com/p/mistralai/Le-Chonk-wants-to-play-Community-Challenge-Rules-3fb6ba59a7fe80a1a3fcd0646cd83595)
(16–29 October 2026). Submission: a demo video (≤ 2 min) + this repository + a short
description, posted to `#challenges-submissions`.

## Architecture

Three processes, two network hops, one tool:

```
┌────────────────────────┐        ┌─────────────────────────────┐
│  Go harness (TUI)      │        │  Stardew Valley + SMAPI mod │
│                        │        │                             │
│  • Mistral API loop    │  HTTP  │  • MoonSharp Lua sandbox    │
│  • context mgmt,       │◄─────►│  • HTTP server:             │
│    prompt caching,     │        │    POST /script, GET /api   │
│    compaction          │        │  • coroutine script exec    │
│  • launches game       │        │  • tick budget, cancel on   │
│    (parent process)    │        │    connection close         │
│  • meta controls       │        │  • documented Bot API       │
└───────────┬────────────┘        └─────────────────────────────┘
            │
            ▼
   Mistral Large 4 Preview (API, tool calls)
```

- **Harness (Go + Bubble Tea):** owns the Mistral conversation — system prompt, tool
  definitions, per-turn UTC header, prompt-cache key, compaction — and the meta controls
  (launch game, stop run, inspect context). Spawns the game as a child process and
  manages graceful shutdown.
- **Mod (C# / SMAPI):** the body. Exposes a documented game API to the sandbox and runs
  agent-written Lua scripts as coroutines over live game ticks.
- **Agent (Mistral Large 4):** writes one small script per goal (`plow a field`,
  `walk to town`), waits for the result, thinks again. Goals live in context for
  now; a resume checkpoint comes later.

### Design decisions

- **One tool: `run_script`.** The mod's entire surface is `POST /script`
  (`{ prints, result, error, ticksUsed }`) and `GET /api` (API schema + recipes).
  No abort endpoint, no state endpoint, no script registry.
- **Synchronous to the agent, coroutines underneath.** A script *looks* blocking
  (`bot.walkTo(x, y)` returns on arrival) but internally yields and resumes as the
  game ticks. The player can only move when the game's update loop runs, so true
  blocking is not an option.
- **Tick budget is the only safety fence.** Every script has a hard tick cap; a stuck
  script returns `stuck`, the agent re-plans. Connection close cancels the active
  script (the TCP connection is the run's lifetime handle).
- **Sandbox with no OS access.** MoonSharp runs stripped of `io`/`os`; scripts can only
  call the exposed Bot API. The API whitelist *is* the security boundary.
- **Prompt cache discipline.** Stable prefix (system prompt → tools →
  append-only turns), volatile data rides in the last message, one
  `prompt_cache_key` per run. Compaction fires near a tunable max context length and
  at day boundaries; the day summary carries the current goals forward so
  compaction stays lossless.
- **Graceful shutdown.** Harness exit → final agent turn ("save and quit"; going to bed
  is the save) → bounded wait → SIGTERM → SIGKILL.

### Persistence

- **MVP:** the game save file — that's the world, and it already exists.
- **Later:** a resume checkpoint (goals + structured context) for picking up an
  interrupted run; a script library (plain agent-side files). No full history log —
  it isn't needed.

## Roadmap

- [ ] **Phase 0 — Grounds.** Game + SMAPI installed and running; repo scaffold
  (`harness/`, `mod/`).
- [ ] **Phase 1 — The body.** Minimal SMAPI mod: HTTP server, MoonSharp, one script
  running as a coroutine over ticks, tick budget, cancel on close. Target: an external
  script can walk the farmer to a tile and get a receipt.
- [ ] **Phase 2 — The brain.** Go/TUI harness: Mistral tool loop, `run_script` +
  `launch_game` + `quit_game`, prompt cache key, compaction, UTC header.
  Target: the model walks the farmer by itself, unaided.
- [ ] **Phase 3 — The farmer.** Bot API surface grows (tools, inventory, dialogue,
  beds, shops); recipes in `GET /api`; tune the model until a full in-game day plays
  sensibly end to end.
- [ ] **Phase 4 — The submission.** Clean repo, README/description, demo video
  (format TBD), post to Discord before 29 October.
- [ ] **Later (post-contest).** Resume checkpoint (goals + structured context);
  script library + library tools; WebSocket transport for remote play; human override
  if it ever becomes "a game I play".

## Layout (planned)

```
harness/   Go + Bubble Tea TUI, Mistral loop, context mgmt
mod/       C# SMAPI mod (server, sandbox, Bot API)
```
