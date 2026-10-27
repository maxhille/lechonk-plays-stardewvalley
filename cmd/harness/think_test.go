package main

import (
	"strings"
	"testing"
)

// TestFrameThinkingRenders proves a thought line (the "Le Chonk (model):" prefix)
// appears in the frame and gets the distinct thinkingStyle (bright gold).
func TestFrameThinkingRenders(t *testing.T) {
	gm := NewGameManager("/nonexistent/xyz", 0)
	m := uiModel{gm: gm, debugPort: 17643, w: 80, h: 16}
	m.lines = []string{
		"Le Chonk (model): I'll start by launching the game and reading the bot API schema.",
		"Le Chonk (model):    (thinking) Let's start by launching the game.",
		"    → action to game: launch_game({})",
	}
	out := m.frame()

	if !strings.Contains(out, "I'll start by launching the game") {
		t.Fatalf("visible-thought line missing from frame")
	}
	if !strings.Contains(out, "(thinking) Let's start by launching the game") {
		t.Fatalf("reasoning ('thinking') line missing from frame")
	}
	if !strings.Contains(out, "launch_game({})") {
		t.Fatalf("action line missing from frame")
	}
	t.Logf("thought lines render in the frame (gold style applied in a real TTY)")
}
