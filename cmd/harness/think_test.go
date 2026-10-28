package main

import (
	"strings"
	"testing"
)

// feed runs a few model-loop events through the transcript the way Update does,
// in the given display mode, and returns the rendered frame.
func feedEvents(mode string, evs ...thoughtEvent) string {
	m := uiModel{gm: NewGameManager("/nonexistent/xyz-should-not-exist", 0), debugPort: 17643, w: 80, h: 24, mode: mode}
	for _, ev := range evs {
		for _, ln := range evLines(ev, mode) {
			m.addLine(ln)
		}
	}
	return m.frame()
}

// Streamer mode (the default) shows only Le Chonk's words and the session
// lifecycle — never his hidden reasoning, never the tool calls or results.
func TestStreamerModeShowsOnlyWords(t *testing.T) {
	out := feedEvents(modeStreamer,
		thoughtEvent{Kind: evStart, Turn: 0, Text: ""},
		thoughtEvent{Kind: evSay, Turn: 1, Text: "I'll head to the TV and watch the fortune teller."},
		thoughtEvent{Kind: evThink, Turn: 1, Text: "  (thinking) the TV is at (3,5)"},
		thoughtEvent{Kind: evTool, Turn: 1, Text: "run_script(moveDown(30))"},
		thoughtEvent{Kind: evResult, Turn: 1, Text: "{\"x\":64}"},
	)
	if !strings.Contains(out, "Le Chonk: I'll head to the TV") {
		t.Errorf("streamer mode must show his words; frame=%s", stripAnsi(out))
	}
	if strings.Contains(out, "(thinking)") {
		t.Errorf("streamer mode must not show reasoning; frame=%s", stripAnsi(out))
	}
	if strings.Contains(out, "action to game") || strings.Contains(out, "game → model") {
		t.Errorf("streamer mode must not show tool calls/results; frame=%s", stripAnsi(out))
	}
}

// Debug mode shows the mechanical view — tool calls and results — and not his
// words.
func TestDebugModeShowsToolCalls(t *testing.T) {
	out := feedEvents(modeDebug,
		thoughtEvent{Kind: evSay, Turn: 1, Text: "I'll head to the TV"},
		thoughtEvent{Kind: evTool, Turn: 1, Text: "run_script(moveDown(30))"},
		thoughtEvent{Kind: evResult, Turn: 1, Text: "{\"x\":64}"},
	)
	if !strings.Contains(out, "→ action to game: run_script") {
		t.Errorf("debug mode must show tool calls; frame=%s", stripAnsi(out))
	}
	if !strings.Contains(out, "← game → model:") {
		t.Errorf("debug mode must show results; frame=%s", stripAnsi(out))
	}
	if strings.Contains(out, "Le Chonk: I'll head to the TV") {
		t.Errorf("debug mode must not show his words; frame=%s", stripAnsi(out))
	}
}

// A multi-line (pretty-printed JSON) result spans several wrapped lines in the
// transcript, never collapsed onto one.
func TestMultiLineResultSpansLines(t *testing.T) {
	result := "{\n  \"x\": 64,\n  \"y\": 15\n}"
	out := feedEvents(modeDebug, thoughtEvent{Kind: evResult, Turn: 1, Text: result})
	flat := stripAnsi(out)
	for _, want := range []string{"\"x\": 64", "\"y\": 15"} {
		if !strings.Contains(flat, want) {
			t.Errorf("expected %q on its own line; frame=%s", want, flat)
		}
	}
}
