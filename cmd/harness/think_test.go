package main

import (
	"strings"
	"testing"
)

// feed pushes raw events into a uiModel's history the way Update does and
// returns the rendered frame for the given display mode.
func feedEvents(mode string, evs ...thoughtEvent) string {
	m := uiModel{gm: NewGameManager("/nonexistent/xyz-should-not-exist", 0), debugPort: 17643, w: 80, h: 24, mode: mode}
	for _, ev := range evs {
		m.history = append(m.history, ev)
	}
	return m.frame()
}

// Streamer mode shows ONLY his words — no reasoning, no tool calls, no
// results, no lifecycle events, no harness notices.
func TestStreamerModeShowsOnlyWords(t *testing.T) {
	out := feedEvents(modeStreamer,
		thoughtEvent{Kind: evSay, Turn: 1, Text: "heading to the TV to check the weather 📺"},
		thoughtEvent{Kind: evThink, Turn: 1, Text: "  (thinking) the TV is at (3,5)"},
		thoughtEvent{Kind: evTool, Turn: 1, Text: "run_script(moveDown(30))"},
		thoughtEvent{Kind: evResult, Turn: 1, Text: "{\"x\":64}"},
		thoughtEvent{Kind: evStart, Turn: 0, Text: ""},
		thoughtEvent{Kind: evInfo, Turn: 0, Text: "starting Le Chonk…"},
	)
	if !strings.Contains(out, "heading to the TV") {
		t.Errorf("streamer mode must show his words; frame=%s", stripAnsi(out))
	}
	// Chat chrome: no prefix, no turn tags — the raw message alone. (The frame's
	// title divider is expected; turn dividers between messages are not — assert
	// that separately on renderHistory.)
	for _, chrome := range []string{"Le Chonk:", "[t"} {
		if strings.Contains(stripAnsi(out), chrome) {
			t.Errorf("streamer mode leaked %q chrome; frame=%s", chrome, stripAnsi(out))
		}
	}
	for _, leak := range []string{"(thinking)", "action to game", "game → model", "starting", "run_script"} {
		if strings.Contains(stripAnsi(out), leak) {
			t.Errorf("streamer mode leaked %q into the view; frame=%s", leak, stripAnsi(out))
		}
	}
}

// Debug mode shows the mechanical view — tool calls, results, errors, harness
// notices — and NOT his words (those are the streamer's lane).
func TestDebugModeShowsMechanics(t *testing.T) {
	out := feedEvents(modeDebug,
		thoughtEvent{Kind: evSay, Turn: 1, Text: "found the chest ✨"},
		thoughtEvent{Kind: evTool, Turn: 1, Text: "run_script(moveDown(30))"},
		thoughtEvent{Kind: evResult, Turn: 1, Text: "{\"x\":64}"},
		thoughtEvent{Kind: evInfo, Turn: 0, Text: "game PAUSED"},
	)
	for _, want := range []string{"→ action to game: run_script", "← game → model:", "· game PAUSED"} {
		if !strings.Contains(stripAnsi(out), want) {
			t.Errorf("debug mode must show %q; frame=%s", want, stripAnsi(out))
		}
	}
	if strings.Contains(stripAnsi(out), "Le Chonk: found the chest") {
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

// The thinking indicator: while the model is generating, the streamer view
// shows "…"; once his message arrives, the indicator is gone.
func TestThinkingIndicatorLifecycle(t *testing.T) {
	pending := renderHistory([]thoughtEvent{
		{Kind: evSay, Turn: 1, Text: "first message"},
		{Kind: evThinking, Turn: 2, Text: "…"},
	}, modeStreamer, 40, 10)
	if !strings.Contains(stripAnsi(strings.Join(pending, "\n")), "…") {
		t.Errorf("pending turn must show the thinking indicator:\n%s", stripAnsi(strings.Join(pending, "\n")))
	}

	arrived := renderHistory([]thoughtEvent{
		{Kind: evSay, Turn: 1, Text: "first message"},
		{Kind: evThinking, Turn: 2, Text: "…"},
		{Kind: evSay, Turn: 2, Text: "second message"},
	}, modeStreamer, 40, 10)
	joined := stripAnsi(strings.Join(arrived, "\n"))
	if strings.Contains(joined, "…") {
		t.Errorf("indicator must disappear once his words arrive:\n%s", joined)
	}
	if !strings.Contains(joined, "second message") {
		t.Errorf("his message must be shown:\n%s", joined)
	}
}
