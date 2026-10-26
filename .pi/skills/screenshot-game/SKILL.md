---
name: screenshot-game
description: Take a screenshot of the running Stardew Valley game window on sway/Wayland and save it to /tmp, so the agent can read the image to see what is on screen. Use when asked to look at the game, check what the game shows, verify a menu or dialogue is on screen, or debug what the bot sees.
---

# Screenshot the game window

Run the bundled script; it finds the Stardew Valley window in the sway tree,
captures exactly that window with `grim`, and writes a PNG to `/tmp`:

```bash
bash "$(dirname "$(readlink -f .pi/skills/screenshot-game/SKILL.md)")/scripts/screenshot.sh"
```

The script prints the output path (e.g. `/tmp/lechonk-screenshot.png`).
Then use the `read` tool on that path to view the image.

If the game is not running, the script prints an error — launch the game first
(via `steam-run` in the game directory, see the repo README).

Do not screenshot the whole screen; capture only the game window so menus and
dialogue are legible.
