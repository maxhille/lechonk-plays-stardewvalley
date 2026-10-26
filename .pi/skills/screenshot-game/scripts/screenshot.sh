#!/usr/bin/env bash
# Screenshot the Stardew Valley window on sway into /tmp.
# Prints the output file path on stdout.
set -euo pipefail

OUT="${1:-/tmp/lechonk-screenshot.png}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

read -r X Y W H < <(python3 "$SCRIPT_DIR/find_window.py")
grim -g "${X},${Y} ${W}x${H}" "$OUT"
echo "$OUT"
