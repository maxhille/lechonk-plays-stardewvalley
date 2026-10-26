name: manage-game
description: Start, stop, and check the status of the running Stardew Valley game. The game runs as a user systemd unit because pi group-kills (SIGKILL) any process its bash tool spawns when a call finishes — launching inline (even with nohup/setsid/&) dies as soon as the call returns. Use when you need to launch the game to drive the bot, stop it, or check whether it is running.
---

# Manage the game (start / stop / status)

## Why systemd-run and not `steam-run ... &`

Pi's bash tool spawns its shell **detached** and, when the call completes or is
aborted, runs `killProcessTree` = `process.kill(-pid, "SIGKILL")` — the leading
minus kills the **whole process group**. So anything started inline (via
`nohup`, `setsid`, `&`, `disown`) is reparented-into-a-group pi owns and gets
reaped the moment the call ends. That is the "game died right after you checked it."
`systemd-run --user` hands the process to the **user systemd manager** (a separate
daemon), outside pi's tree, so it survives every tool-call boundary.

## Commands

Run the bundled script (it resolves itself relative to this skill):

```bash
bash "$(dirname "$(readlink -f .pi/skills/manage-game/SKILL.md)")/scripts/manage.sh" {start|stop|status}
```

- **`start`** — idempotent. Launches the game under the user unit (default
  `sdvgame`) if it is not already running. Wait for the Lechonk HTTP API to come
  up before driving anything — the server binds `127.0.0.1:17642` once the mod
  finishes booting (~30 s). Confirm with:
  ```bash
  until ss -ltn | grep -q ':17642'; do sleep 2; done
  echo "HTTP up"
  ```
  Then drive it:
  ```bash
  curl -s -d @script.json localhost:17642/script          # -> {"scriptId":N}
  curl -s "localhost:17642/status?scriptId=$N"            # poll until "completed"
  ```
- **`stop`** — stops the unit. This is the safe replacement for
  `pkill StardewValley`: `pkill -f` matches by pattern and can kill your own
  shell (argv contains "StardewValley"); the unit stop is exact.
- **`status`** — prints `running` or `stopped`.

## After a mod build

Rebuild and redeploy the mod (see the repo README for the exact `nix develop
 -c dotnet build` + copy), then pick it up with:

```bash
manage.sh stop   # then
manage.sh start
```

The mod loads at game launch, so a stop/start reloads it.

## Do not

- Launch the game with `nohup steam-run ... &`, `setsid ...`, or `&` from a
  bash call — it will be reaped when the call finishes.
- Use `pkill -f StardewValley` — it matches your own shell's argv. Use
  `manage.sh stop`.
