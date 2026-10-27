# Phase 2 — The Brain

Build the Go harness so LeChonk (the model) plays by itself: it reads the
API, launches the game, runs Lua, thinks, and repeats.

## Goal

A Go + Bubble Tea TUI that owns the Mistral (or any OpenAI-compatible)
conversation loop and manages the game as a child process. LeChonk walks the
farmer unaided; the human watches him think.

## Architecture

The game is a **child process of the harness** — not its own systemd unit.
This is deliberate: it is what makes "kill the harness" reliably kill the
game (same cgroup).

```
user systemd
  └─ lechonk (Go harness, the unit)
       ├─ tool: launch_game       → exec the game as a child
       ├─ tool: run_script        → HTTP POST /script
       ├─ tool: read_script_api   → HTTP GET  /api
       └─ owns the game: SIGTERM → graceful, SIGKILL last resort
             │
             └─ steam-run → Stardew Valley (game + SMAPI Lechonk mod)
   ────────────────────────────────────────────────────
   external (network): Mistral / any OpenAI-compatible endpoint
```

Process tree while the game is running:

```
user systemd
  └─ lechonk (harness)               [the unit, owns the tree]
       └─ steam-run → StardewValley  [game + SMAPI Lechonk mod]
```

`manage-game` / `sdvgame` unit are retired for this phase; the harness owns
the game. Mod reload = rebuild (auto-deploys the DLL) →
`systemctl --user restart lechonk`.

## Status

**Built and verified:**
- Module at the repo root (`go.mod`/`go.sum` at the top); main package at
  `cmd/harness/`.
- Full-screen Bubble Tea TUI: alternate screen, bottom-anchored transcript,
  live game status, `l`/`s`/`r`/`q` keys, and a warm "Stardew Valley" palette
  (parchment text, gold border, green = running, terracotta = stopped).
- Game mgmt: the harness owns the game as a child; SIGTERM graceful → SIGKILL
  last resort; kill-harness = kill-game verified; ad-hoc run via
  `systemd-run --user --unit=lechonk`.
- Debug HTTP on `127.0.0.1:17643`: `GET /status`, `POST /launch /stop /restart`.
- Mod build/redeploy loop verified (`dotnet build` auto-deploys the DLL).

**Still to build (the brain itself):**
- Model client: OpenAI-compat streaming + tool-call parsing, env config, UTC
  header.
- The three tools wired: `launch_game`, `run_script`, `read_script_api`.
- Prompt mgmt: stable prompt-cache key, compaction, system prompt, the
  think→act→observe loop.
- Fake OpenAI-compat echo endpoint for zero-token harness testing.
- Streaming-thoughts panel + top status bar (model / endpoint / in-game time /
  day) and the "running a script…" elapsed clock.
- Flake `buildGoApplication` package (unverified recipe).

## The flake — module root is the repo

The Go module root moved to the repo: `go.mod`/`go.sum` at the top, main
package at `cmd/harness/`.

1. **Devshell build (canonical, used today):**
   ```sh
   go build -o ./lechonk ./cmd/harness
   ```
2. **Flake package (later — unverified recipe).** A `buildGoApplication` whose
   `src` is the repo root (the `go.mod` lives there; `cmd/harness` is the main
   package to build):
   ```nix
   lechonk = forSystems (pkgs: pkgs.buildGoApplication {
     pname = "lechonk";
     src    = .;   # module root; pick the cmd/harness package
   });
   ```
   ⚠ The exact package-selection flags here are *not yet written/verified* —
   this is a plan entry, not working Nix.
3. The devshell already has `go` and `dotnet-sdk`. **No change.** The game is
   still a runtime requirement, not a flake input.

## Harness design

### Model client: one OpenAI-compatible endpoint

Mistral *is* OpenAI-compatible, so a single client hits
`POST {base}/v1/chat/completions` for both prod and any cheaper endpoint.
Swap is pure config, no code:

- `LECHONK_BASE_URL` — default `https://api.mistral.ai/v1`
- `LECHONK_MODEL` — default the large model
- `LECHONK_API_KEY` — default `MISTRAL_API_KEY` for compatibility

This is how LeChonk talks to other endpoints during dev without burning money:
point the base URL at OpenRouter's cheaper models, or a local Ollama. No code
change.

For testing the harness with **zero tokens**, stand up a tiny fake OpenAI-compat
echo endpoint that returns canned tool calls. The whole harness (loop, TUI,
streaming, game launch) can be exercised against it.

### The three tools

| Tool | Behavior |
|---|---|
| `launch_game(slot?)` | If the game is not up, exec it as a child via `SV_SMAPI_LAUNCH_CMD`; wait for `:17642` (bounded timeout); optionally load the named save and `waitForWorldReady`; return "up". Errors if already running (re-exec on explicit relaunch). |
| `run_script(lua)` | `POST /script`; blocks for the result; returns `{result, error, ticksUsed, evt}`. HTTP timeout set above the tick budget so a legitimately long script never false-timeouts. |
| `read_script_api()` | `GET /api`; returns the schema. Cheap, no Lua — this is how LeChonk self-documents its own game. |

### Thinking out loud (TUI) — shell built, content next

The TUI shell is **built and verified**: a full-screen frame (alternate screen),
a bottom-anchored transcript, a live status line, and `l`/`s`/`r`/`q` keys, in
a warm "Stardew Valley" palette. What remains is the *content* the model loop
will feed into it:

LeChonk's thoughts are shown live so the human follows the run:

- A scrollable **transcript**: `LeChonk's thoughts` → action → result, in order.
- A top **status bar**: model, endpoint, in-game time and day.
- A bottom line: "LeChonk's thinking…" (spinner) / "running a script…"
  (elapsed clock).

Stream the model output (`stream:true`), appending each token to the "thinking"
panel. When the response completes, accumulate the streamed `tool_calls` and run
the tool, printing the action and the result to the transcript.

**Critical:** run the model loop in a goroutine that pushes `thought` / `tool`
events onto a channel; the Bubble Tea main loop (ticker) reads the channel and
updates state. If the model loop blocked the TUI's event loop, a long
`run_script` would freeze the screen.

### Graceful shutdown

On `SIGTERM` / TUI quit: one final agent turn ("save and quit, bed"), then
`SIGTERM` the game child, `SIGKILL` after a timeout. Stardew auto-saves, so the
hard path is safe too.

## Config (all env or flags)

| Var | Purpose | Default |
|---|---|---|
| `LECHONK_BASE_URL` | model endpoint | `https://api.mistral.ai/v1` |
| `LECHONK_MODEL` | model id | large model |
| `LECHONK_API_KEY` | model key | `MISTRAL_API_KEY` |
| `SV_SMAPI_LAUNCH_CMD` | game launch command | `steam-run <game dir>/StardewValley` |
| `LECHONK_AUTO_START` | launch + load on start | off |
| `LECHONK_TICK_BUDGET` | override mod tick cap | mod default |

## Systemd — the harness unit

The harness is a **user systemd unit** `lechonk`. Killing it reaps the cgroup,
so the game dies with it — this is the guarantee that a separate unit (which
`kill -9` on the harness would *not* take down) cannot give us.

Ad-hoc (matches how the game is run):

```bash
systemd-run --user --unit=lechonk --working-directory="$REPO" \
  bash -c 'export DISPLAY=:0 XDG_RUNTIME_DIR=/run/user/10000 \
    SV_SMAPI_LAUNCH_CMD="..." LECHONK_BASE_URL=... LECHONK_API_KEY=...; \
    <path to lechonk binary>'
```

Or a tiny `~/.config/systemd/user/lechonk.service` with `ExecStart` /
`WorkingDirectory` / `Environment=` (cleaner for the config vars). `systemctl
--user start|stop|kill lechonk` is the interface.

## Build order

1. ✅ **Scaffold** — module at the repo root (`go.mod`), main package at
   `cmd/harness/`, `go get bubbletea`. Flake `buildGoApplication`: **not yet**
   (unverified recipe, see "The flake").
2. ✅ **Game process mgmt** — child `exec`, port-wait, SIGTERM/SIGKILL; kill-
   harness=kill-game verified.
3. ⬜ **Model client** — OpenAI-compat streaming + tool-call parsing, env config.
   Unit-test against the fake echo endpoint (no money).
4. ⬜ **The three tools** — `launch_game`, `run_script`, `read_script_api`
   wired to the client + game manager.
5. ⬜ **TUI** — shell is done (full-screen, transcript, status, keys); remaining:
   streaming-thoughts panel, top status bar, input (pause/kill). Goroutine↔
   channel decoupling.
6. ⬜ **Model loop** — system prompt, tool defs, UTC header, the think→act→
   observe cycle.
7. ⬜ **Systemd** — `lechonk` unit; verify kill→game-dies; verify `restart`
   reloads the mod.
8. ⬜ **Smoke test** — LeChonk reads the API, launches the game, loads the save,
   walks to the TV, and shows everything as it goes.

## Risks

- **Streaming + tool calls.** A streamed `tool_call`'s argument JSON arrives in
  token chunks; we must accumulate it correctly before executing or the tool
  misfires. Mitigate with a non-streaming fallback and unit tests of the client
  against the fake endpoint with canned multi-chunk tool args.
- **`steam-run` under systemd.** Must be on the harness's PATH with the right
  env (`DISPLAY`, `XDG_RUNTIME_DIR`). Bake it into the unit.
- **Long `run_script`.** It is a single blocking call — LeChonk shows
  "running…" but emits no thoughts mid-script (there is nothing to stream — he
  is waiting). Show elapsed time. Intrinsic to a synchronous `run_script`.
