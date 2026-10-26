#!/usr/bin/env bash
# Manage the Stardew Valley game as a user systemd unit:
#   manage.sh start | stop | status
#
# The game is launched via `systemd-run --user` so it is reparented to the
# user systemd manager, OUTSIDE pi's process group. Pi group-kills (SIGKILL)
# every process it spawned when a bash tool call completes or is aborted, so a
# game started inline (even with nohup/setsid/&) dies when the call returns.
# A user unit survives across tool calls.
set -euo pipefail

GAME="${SDV_GAME_DIR:-/home/mh/.local/share/Steam/steamapps/common/Stardew Valley}"
UNIT="${SDV_UNIT:-sdvgame}"
DISP="${DISPLAY:-:0}"
RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"

case "${1:-}" in
  start)
    if systemctl --user is-active --quiet "$UNIT"; then
      echo "$UNIT already running (HTTP: http://127.0.0.1:17642)"
      exit 0
    fi
    systemd-run --user --unit="$UNIT" --working-directory="$GAME" \
      bash -c "DISPLAY=$DISP XDG_RUNTIME_DIR=$RUNTIME_DIR steam-run ./StardewValley"
    echo "started $UNIT. Wait ~30s until port 17642 answers before driving the bot."
    ;;
  stop)
    if systemctl --user is-active --quiet "$UNIT"; then
      systemctl --user stop "$UNIT"
      echo "stopped $UNIT"
    else
      echo "$UNIT not running"
      exit 0
    fi
    ;;
  status)
    if systemctl --user is-active --quiet "$UNIT"; then
      echo "running"
    else
      echo "stopped"
    fi
    ;;
  *)
    printf 'usage: %s {start|stop|status}\n' "$0" >&2
    exit 2
    ;;
esac
