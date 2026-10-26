#!/usr/bin/env python3
"""Find the Stardew Valley window in the sway tree.

Prints "x y width height" in absolute screen coordinates on stdout,
or exits non-zero with an error message if the game window is not found.
"""
import json
import subprocess
import sys


def find(node):
    props = node.get("window_properties") or {}
    cls = props.get("class") or ""
    name = node.get("name") or ""
    if "StardewValley" in cls or "Stardew Valley" in name:
        return node["rect"]
    for child in node.get("nodes", []) + node.get("floating_nodes", []):
        rect = find(child)
        if rect is not None:
            return rect
    return None


def main():
    tree = json.loads(subprocess.run(
        ["swaymsg", "-t", "get_tree"],
        capture_output=True, text=True, check=True,
    ).stdout)
    rect = find(tree)
    if rect is None:
        sys.exit("game window not found (is the game running?)")
    # sway rect is relative to the output origin; include it for absolute coords
    print(rect["x"], rect["y"], rect["width"], rect["height"])


if __name__ == "__main__":
    main()
