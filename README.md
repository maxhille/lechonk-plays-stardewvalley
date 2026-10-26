# Lechonk Plays Stardew Valley

Mistral Large 4 ("Le Chonk") plays Stardew Valley through a custom harness: the model
writes small Lua scripts that drive a SMAPI mod inside the game, one bounded action at
a time, and plans its days around them.

Built for the ["Le Chonk wants to play" community challenge](https://app.notion.com/p/mistralai/Le-Chonk-wants-to-play-Community-Challenge-Rules-3fb6ba59a7fe80a1a3fcd0646cd83595)
(16–29 October 2026). Submission: a demo video (≤ 2 min) + this repository + a short
description, posted to `#challenges-submissions`.

## Prerequisites

- **Stardew Valley** via Steam, with **SMAPI 4.x** installed in the game folder.
  Runtime requirement, deliberately not a build input.
- **Steam launch wrapper** — on this NixOS box the game runs through
  `steam-run`. The harness launches the game with the command given in
  `SV_SMAPI_LAUNCH_CMD` (e.g. `steam-run <game dir>/StardewValley`). With a manual
  SMAPI install the SMAPI launcher replaces the game binary (the original is
  preserved as `StardewValley-original`); an installer-based install keeps
  `StardewModdingAPI` instead.
- **Mistral API key** (`MISTRAL_API_KEY`) for the harness.
- **Nix** — `nix develop` gives the dev shell for building the mod
  (Go + .NET SDK). The mod build locates the game folder via a machine-local
  `stardewvalley.targets` in `$HOME` (not committed); `dotnet build` auto-deploys
  the mod to `Mods/Lechonk/` in the game folder.
- **Window manager (sway)** — the game window must float so tiling never clips
  it: `for_window [class="StardewValley"] floating enable; inhibit_idle focus`
  (configured in `~/dotfiles/modules/home/sway.nix`). Floating window plus the
  no-pause setting below are both required for the bot to play while the human
  looks elsewhere.
- **No-pause when unfocused** — the per-player value the game actually reads is
  `<pauseWhenOutOfFocus>false</pauseWhenOutOfFocus>` in the **save file**
  (`~/.config/StardewValley/Saves/<save>/<save>`); the game overwrites
  `startup_preferences`/`default_options` on close, so editing those alone does not
  stick. Set it in the save file.
- **Process lifetime — run the game as a user systemd unit.** The game must be
  started under `systemd-run --user`, not as a background process of the agent's
  bash tool. Pi's bash tool group-kills (`SIGKILL`) the whole process group it
  spawned when a tool call finishes or is aborted, so anything started inline
  (`nohup`/`setsid`/`&`) dies the instant the call returns. A transient user
  unit is reparented to the user systemd manager, outside pi's tree, and stays
  up across tool calls. Start/stop/status via the `manage-game` skill:
  `bash .pi/skills/manage-game/scripts/manage.sh {start|stop|status}`.

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
  (`{ result, error, ticksUsed, evt }`) and `GET /api` (API schema).
  No abort endpoint, no status endpoint, no script registry.
- **Synchronous to the agent, coroutines underneath.** `POST /script` blocks the
  HTTP connection until the Lua coroutine finishes (or hits the tick budget) and
  writes the result back in the same response. Inside the mod the script yields
  each game tick — the game loop resumes it — but the caller never polls.
- **Tick budget is the only safety fence.** Every script has a hard tick cap; a stuck
  script returns `{ error: "stuck: tick budget exceeded…", evt: "stuck" }`, the
  agent re-plans. Connection close cancels the active script (the TCP connection
  is the run's lifetime handle).
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

## Bot API

The mod's entire surface is `POST /script` (response: `{ result, error, ticksUsed, evt }`)
and `GET /api` (full schema). Send the Lua code directly as the request body:
```
curl -s -X POST http://127.0.0.1:17642/script \
  -H 'Content-Type: application/lua' \
  -d 'local m = bot.getMapSchema()\nreturn {x = m.player.x, y = m.player.y}'
```
No JSON wrapper, no `scriptId`. The response is written back on the same connection after
the coroutine finishes: a `200` with `{ result, error, ticksUsed, evt }`.
No `GET /status`, no polling.

Lua scripts see a `bot` table:

- **Movement:** `bot.move(dir, ticks)` — hold the LEFT thumbstick (0=down, 1=left,
  2=up, 3=right) for `ticks` input ticks. The game runs in gamepad mode; the
  controller is the sole input source. `bot.stop()` halts immediately.
- **Saves:** `bot.listSaves()`, `bot.loadGame(slotName)` (from the title screen,
  skips character creation), `bot.exitToTitle()`, `bot.isWorldReady()`,
  `bot.wakeUp()` (teleports off the bed footprint — required after loading a
  save made in bed, the game re-flags `isInBed` while standing on a bed tile).
- **State:** `bot.getPosition()`, `bot.isWalkable(x, y)`, `bot.getTileObject(x, y)`,
  `bot.getInventory()`, `bot.getTime()`, `bot.getDate()`, `bot.wait(ticks)`.
- **Menus/dialogue:** `bot.getMenuType()` (type, buttonCount, **text** — the
  narrative dialogue text), `bot.getMenuButtons()` (component index/type/
  label/visible/bounds — **label** is the response text for `DialogueBox`
  menus), `bot.clickMenuButton(index)`, `bot.advanceDialogue()`,
  `bot.selectDialogueOption(index)`, `bot.closeDialogue()`.
- **Cursor:** `bot.cursorTo(x, y)` — move the on-screen cursor (right thumbstick)
  to absolute screen pixels (x, y), then press A once at the target. For
  mouse-driven menus (TV channels, etc.). Top-left origin, +Y downward.
- **Debug:** `bot.getMovementState()`, `bot.listFurniture()`, `bot.print(msg)`.

## Roadmap

- [x] **Phase 0 — Grounds.** Game + SMAPI installed and running (SMAPI 4.5.2,
  Stardew Valley 1.6.15, native Linux build, launched via `steam-run`); repo
  scaffold (`harness/`, `mod/`).
- [x] **Phase 1 — The body.** SMAPI mod with HTTP server (`POST /script`,
  `GET /api`), MoonSharp Lua sandbox running scripts as coroutines over game
  ticks, tick budget, cancel on connection close. Bot API: movement via
  synthetic input, save load/exit, wake-up, state queries, menu/dialogue
  navigation. Tested end to end, driven by the pi agent over HTTP (the Go
  harness below is not yet built): load a save, wake, walk the farmer, open the
  TV channel menu.
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
flake.nix  devShell (Go + .NET SDK); harness package lands in phase 2
harness/   Go + Bubble Tea TUI, Mistral loop, context mgmt
mod/Lechonk  C# SMAPI mod: HTTP server, Lua sandbox, Bot API
.pi/skills/screenshot-game  pi skill: capture the game window to /tmp for reading
.pi/skills/manage-game  pi skill: launch/stop/status the game as a user systemd unit
```
