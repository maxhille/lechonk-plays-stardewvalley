# Lechonk Plays Stardew Valley

An LLM ("Le Chonk") plays Stardew Valley through a custom harness: the model
writes small Lua scripts that drive a SMAPI mod inside the game, one bounded action at
a time, and plans its days around them. Currently pointed at Qwen3.8-27B on a local
ninfer endpoint — any OpenAI-compatible model works, it's pure config.

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
- **Model endpoint** — any OpenAI-compatible API. `.envrc` points the harness at
  `http://saturn:9933/v1` (local ninfer server, model `Qwen3.8-27B`, no auth);
  override with `LECHONK_BASE_URL` / `LECHONK_MODEL` / `LECHONK_API_KEY`.
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
- **Process lifetime.** Phase 1 ran the game as a user systemd unit (the
  `manage-game` skill); in Phase 2 the harness owns the game as a child process,
  so killing the harness kills the game. Note for agents: never launch the game
  inline from pi's bash tool — it group-kills (`SIGKILL`) the whole process
  group when the call finishes, so an inline game dies instantly.

## Running it

Two buildable things:

1. **The mod (C# SMAPI).** Rebuild to redeploy — `dotnet build` auto-copies
   `Lechonk.dll` into `Mods/Lechonk/` in the game folder:
   ```bash
   nix develop mod/Lechonk --command dotnet build
   ```
2. **The harness (Go).** From the repo root, in the flake devshell (`.envrc`)
   — the canonical build path:
   ```bash
   go build -o ./lechonk ./cmd/harness
   ```

Run the harness:

- **Directly.** `./lechonk` opens a full-screen TUI. Keys: `l` launch, `s` stop,
  `r` restart, `p` pause (freezes the game *and* any running script),
  `q` quit (also reaps the game child). Run it under a real
  terminal, or `script -qc ./lechonk /tmp/lechonk.log` to log it.
- **Via user systemd (for unattended runs).** Keeps the harness — and thus
  the game — up across a crash and makes kill-harness=kill-game airtight:
   ```bash
   systemd-run --user --unit=lechonk --working-directory="$PWD" ./lechonk
   systemctl --user stop lechonk
   ```
  With no TTY the harness runs **headless**: no TUI, Le Chonk starts
  immediately, and everything lands in `/tmp/lechonk-transcript.log`
  (force it with `LECHONK_HEADLESS=1`). `.envrc` (via `use flake` +
  nix-direnv) already sets `SV_SMAPI_LAUNCH_CMD`, `LECHONK_DEBUG_PORT`, and
  the model vars; the client's defaults match, so no env forwarding is
  needed. `systemctl --user is-active lechonk` checks it.

Endpoints, once the game is up:

- **Mod Bot API** — `127.0.0.1:17642`: `POST /script`, `GET /api`, plus admin
  `POST /pause` (opens/closes the in-game menu directly, freezes any running
  script, works even when the game window has lost focus; not exposed to the
  model).
- **Harness debug API** — `127.0.0.1:17643`: `GET /status`, `POST /launch`,
  `/stop`, `/restart`.

Mod reload = rebuild (1) → `systemctl --user restart lechonk` (or `r` in the
TUI).

Configuration (all optional — the defaults work out of the box):

| Var | Purpose | Default |
|---|---|---|
| `LECHONK_BASE_URL` | model endpoint | `http://saturn:9933/v1` |
| `LECHONK_MODEL` | model id | `Qwen3.8-27B` |
| `LECHONK_API_KEY` | model key, if the endpoint needs one | unset |
| `SV_SMAPI_LAUNCH_CMD` | how the game is launched | `steam-run ./StardewValley` |
| `LECHONK_DEBUG_PORT` | debug endpoint port | `17643` |
| `LECHONK_HEADLESS` | `1` = skip the TUI even with a TTY | off |

## Architecture

Three processes, two network hops, three tools:

```
┌────────────────────────┐        ┌─────────────────────────────┐
│  Go harness (TUI)      │        │  Stardew Valley + SMAPI mod │
│                        │        │                             │
│  • model loop + tools  │  HTTP  │  • MoonSharp Lua sandbox    │
│  • transcript + log    │◄─────►│  • HTTP server:             │
│  • game child (parent) │        │    POST /script, GET /api   │
│  • debug HTTP, shutdown│        │  • coroutine script exec    │
└───────────┬────────────┘        └─────────────────────────────┘
            │
            ▼
   Qwen3.8-27B (local ninfer, OpenAI-compat, tool calls)
```

- **Harness (Go + Bubble Tea):** owns the model conversation — system prompt, tool
  definitions, the think→tool→observe loop, reasoning capture — and the meta
  controls (launch game, stop run, debug HTTP). Spawns the game as a child process
  and manages shutdown.
- **Mod (C# / SMAPI):** the body. Exposes a documented game API to the sandbox and runs
  agent-written Lua scripts as coroutines over live game ticks.
- **Agent (Le Chonk, the model):** writes one small script per goal (`plow a
  field`, `walk to town`), waits for the result, thinks again. Goals live in
  context for now; a resume checkpoint comes later.

### The model loop

Le Chonk's think→tool→observe cycle runs in its own goroutine and pushes
thoughts, actions, and results over a channel to the TUI (and to
`/tmp/lechonk-transcript.log`), so a long `run_script` never freezes the
screen. Failed model calls and stuck scripts are fed back to him for
re-planning; his hidden reasoning (`reasoning_content`) is shown as gold
"thinking" lines; he ends a day with `DONE`. Three tools:

| Tool | What it does |
|---|---|
| `launch_game()` | Starts the game as a child (if not already up) and returns once the mod API answers on `:17642`. Le Chonk then loads his own save and waits for the world via `run_script`. |
| `run_script(lua)` | `POST /script`; blocks; returns `{result, error, ticksUsed, evt}`. |
| `read_script_api()` | `GET /api` — the full bot schema. This is how Le Chonk reads his own manual. |

### Design decisions

- **Two mod endpoints, three harness tools.** The mod's entire surface is
  `POST /script` (`{ result, error, ticksUsed, evt }`) and `GET /api` (API
  schema). The harness exposes three tools over it: `run_script`,
  `launch_game`, `read_script_api`. No abort endpoint, no status endpoint,
  no script registry.
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
- **Prompt cache discipline.** Every request carries a constant
  `prompt_cache_key` (`lechonk-plays-stardewvalley`), so an endpoint that
  supports it (e.g. Mistral, where cached tokens bill at 10%) can cache the
  shared conversation prefix; local servers ignore the field. Still planned:
  stable-prefix hygiene and compaction (near a tunable max context length and
  at day boundaries; the day summary carries the current goals forward so
  compaction stays lossless).
- **Graceful shutdown.** Harness exit (signal or `q`) → stop the model loop →
  reap the game process tree (SIGTERM, then SIGKILL for survivors). Killing the
  harness cgroup always takes the game down with it.

### Persistence

- **MVP:** the game save file — that's the world, and it already exists.
- **Later:** a resume checkpoint (goals + structured context) for picking up an
  interrupted run; a script library (plain agent-side files). No full history log —
  it isn't needed.

## Bot API

The agent-facing surface is `POST /script` (response: `{ result, error, ticksUsed, evt }`)
and `GET /api` (full schema). There is also an admin-only `POST /pause` (no
body): it opens or closes the in-game menu (`GameMenu`) directly on the game
thread — no input is involved, so it works even when the game window has lost
focus. That is what the TUI `p` key calls, and it is not exposed to the model.
While the menu is open the bot's script is frozen. Send the Lua code directly
as the request body:
```
curl -s -X POST http://127.0.0.1:17642/script \
  -H 'Content-Type: application/lua' \
  -d 'return bot.getPosition()'
```
No JSON wrapper, no `scriptId`. Bodies are capped at 1024 bytes (a `413` above
that) — a full Lua script, not a single call. The response is written back on the same connection after
the coroutine finishes: a `200` with `{ result, error, ticksUsed, evt }`.
No `GET /status`, no polling.

Lua scripts see a `bot` table:

- **Movement:** `bot.moveUp(ticks)`, `bot.moveRight(ticks)`, `bot.moveDown(ticks)`,
  `bot.moveLeft(ticks)` — hold the LEFT thumbstick for `ticks` input ticks.
  The game runs in gamepad mode; the controller is the sole input source.
  Movement stops automatically when a script ends.
- **Input:** `bot.tap(name)` — tap a gamepad button (A = action/select/confirm,
  X = use tool, B = close menu, Start = menu, Y = craft, Back = journal,
  LT/RT = cycle hotbar item, LB/RB = shift hotbar). `bot.cursorUp/Down/Left/Right(ticks)`
  — hold the RIGHT thumbstick (the cursor; scrolls in menus).
  `bot.selectTool(name)` — put the named item/tool in your hand (e.g. `Hoe`).
- **Saves:** `bot.listSaves()`, `bot.loadGame(slotName)` (from the title screen,
  skips character creation), `bot.exitToTitle()`.
- **State:** `bot.getPosition()`, `bot.getFacing()` (which way you face —
  interaction targets the faced tile), `bot.isTilePassable(x, y)`, `bot.getTileObject(x, y)`,
  `bot.getEnergy()`, `bot.getInventory()`, `bot.getTime()`, `bot.getDate()`, `bot.wait(ticks)`.
- **Location:** `bot.listObjects()` (placed objects, incl. ground debris
  Stone/Weeds/Twig), `bot.listTerrainFeatures()`
  (trees, grass, bushes, hoe-dirt — with `watered`/`crop`/`ready` for planted dirt),
  `bot.listFurniture()`, `bot.getWarps()` (doors/exits), `bot.listNpcs()`,
  `bot.listAnimals()`.
- **Menus/dialogue:** `bot.getMenuType()` (type, buttonCount, **text** — the
  narrative dialogue text), `bot.waitForMenu(timeout)` (block until a menu
  opens — menus appear asynchronously, e.g. chest lids animate ~1s before the
  menu shows), `bot.getMenuButtons()` (component index/type/
  label/visible/bounds — **label** is the response text for `DialogueBox`
  menus), `bot.clickMenuButton(index)`, `bot.advanceDialogue()`,
  `bot.selectDialogueOption(index)`, `bot.closeDialogue()`.
- **Debug:** `bot.print(msg)`.

## Roadmap

- [x] **Phase 0 — Grounds.** Game + SMAPI installed and running (SMAPI 4.5.2,
  Stardew Valley 1.6.15, native Linux build, launched via `steam-run`); repo
  scaffold (`cmd/harness/`, `mod/`).
- [x] **Phase 1 — The body.** SMAPI mod with HTTP server (`POST /script`,
  `GET /api`), MoonSharp Lua sandbox running scripts as coroutines over game
  ticks, tick budget, cancel on connection close. Bot API: movement via
  synthetic input, save load/exit, wake-up, state queries, menu/dialogue
  navigation. Tested end to end, driven over the mod's HTTP API: load a save, wake, walk the farmer, open the
  TV channel menu.
- [x] **Phase 2 — The brain.** Go/TUI harness: OpenAI-compatible model client
  (tool calls, non-streaming), the three tools, the think→tool→observe loop,
  live thought transcript (TUI + `/tmp/lechonk-transcript.log`), the game as a
  child process (kill the harness = kill the game), debug HTTP, headless mode.
  Verified live: Le Chonk launches the game, finds and loads his save, and
  plays — the model walks the farmer by itself, unaided.
- [ ] **Phase 3 — The farmer.** Bot API surface grows (tools, inventory, dialogue,
  beds, shops); recipes in `GET /api`; tune the model until a full in-game day
  plays sensibly end to end. Also queued from Phase 2: streaming model output,
  per-turn UTC header, compaction, top status bar, and the
  flake `buildGoApplication` package.
- [ ] **Phase 4 — The submission.** Clean repo, README/description, demo video
  (format TBD), post to Discord before 29 October.
- [ ] **Later (post-contest).** Resume checkpoint (goals + structured context);
  script library + library tools; WebSocket transport for remote play; human override
  if it ever becomes "a game I play".

## Layout

```
flake.nix  devShell (Go + .NET SDK); Go module at the repo root (cmd/harness)
cmd/harness/  Go + Bubble Tea TUI + model loop (built) — owns the game as a child process
mod/Lechonk  C# SMAPI mod: HTTP server, Lua sandbox, Bot API
.pi/skills/screenshot-game  pi skill: capture the game window to /tmp for reading
.pi/skills/manage-game  pi skill: launch/stop/status the game (phase 1; the harness owns the game from phase 2)
```
