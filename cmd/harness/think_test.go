package main

import (
	"strings"
	"testing"
)

// TestFrameHidesThinking proves the model's plain-text thoughts are NOT shown
// in the TUI, while the action and the pretty-printed multi-line result are.
func TestFrameHidesThinking(t *testing.T) {
	gm := NewGameManager("/nonexistent/xyz", 0)
	m := uiModel{gm: gm, debugPort: 17643, w: 80, h: 24}

	for _, ev := range []thoughtEvent{
		{Kind: evThought, Turn: 1, Text: "I'll start by launching the game."},
		{Kind: evTool, Turn: 1, Text: "launch_game({})"},
		{Kind: evResult, Turn: 1, Text: "{\n  \"ok\": true,\n  \"evt\": \"completed\"\n}"},
	} {
		for _, ln := range evLines(ev) {
			m.addLine(ln)
		}
	}

	out := m.frame()
	if strings.Contains(out, "I'll start by launching the game") {
		t.Fatalf("thinking should not be shown in the TUI")
	}
	if !strings.Contains(out, "launch_game({})") {
		t.Fatalf("action line missing from frame")
	}
	if !strings.Contains(out, "← game → model:") {
		t.Fatalf("result arrow missing from frame")
	}
	if !strings.Contains(out, "\"ok\": true") {
		t.Fatalf("result JSON key line missing from frame")
	}
}

// TestPrettyResult keeps newlines in JSON and leaves non-JSON alone.
func TestPrettyResult(t *testing.T) {
	if got := prettyResult(`{"a":1,"b":[2,3]}`); !strings.Contains(got, "\n") {
		t.Fatalf("expected indented multi-line JSON, got %q", got)
	}
	if got := prettyResult("ERROR: no save"); got != "ERROR: no save" {
		t.Fatalf("non-JSON should pass through untouched, got %q", got)
	}
	if got := prettyResult("  \n  "); got != "" {
		t.Fatalf("whitespace-only should be empty, got %q", got)
	}
}
