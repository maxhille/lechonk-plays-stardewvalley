package main

import (
	"strings"
	"testing"
)

func TestGamepadBlock(t *testing.T) {
	for _, want := range []string{
		"A      action / select / confirm",
		"X      use tool",
		"left stick",
		"right stick",
		"emote wheel",
		"bot.cursorUp",
	} {
		if !strings.Contains(gamepadBlock, want) {
			t.Errorf("gamepad block missing %q:\n%s", want, gamepadBlock)
		}
	}
	// The block must not leak any keyboard or mouse vocabulary.
	for _, banned := range []string{"MouseLeft", "MouseRight", "Escape", "keybind"} {
		if strings.Contains(gamepadBlock, banned) {
			t.Errorf("gamepad block must not mention %q:\n%s", banned, gamepadBlock)
		}
	}
}
