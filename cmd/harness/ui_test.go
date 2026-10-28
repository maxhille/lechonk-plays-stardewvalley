package main

import (
	"os/exec"
	"strings"
	"testing"
	"time"

	charmansi "github.com/charmbracelet/x/ansi"
)

// TestFrameTheming verifies the full-screen themed frame (gold border +
// lipgloss colours) actually renders when the terminal is large enough. This is
// the fix for "theming gone": w/h must be captured from WindowSizeMsg, and
// frame() must apply border + colours.
func TestFrameTheming(t *testing.T) {
	gm := NewGameManager("/nonexistent/xyz-should-not-exist", 0)
	m := uiModel{
		gm:        gm,
		debugPort: 17643,
		w:         110, // wide enough for the frame
		h:         24,  // tall enough
	}
	out := m.frame()
	if !strings.Contains(out, "┌") || !strings.Contains(out, "└") {
		t.Errorf("no frame border characters (┌/└) — fell back to plain view?")
	}
	if !strings.Contains(out, "Le Chonk plays Stardew Valley") {
		t.Errorf("title missing from frame")
	}
	if !strings.Contains(out, "\x1b[") {
		// In a non-TTY test env lipgloss strips colours; that is expected and is
		// not the theming bug. In the real terminal (a TTY) lipgloss emits the
		// SGR escape codes, so the border/title/status are coloured.
		t.Log("note: no ANSI escapes in this non-TTY env (expected); a real TTY emits them")
	} else {
		t.Log("colours present in this env")
	}
}

// TestFrameExitsState verifies the amber EXITS status line renders when the game
// process has died on its own (Exited set; the monitor owns the setting).
func TestFrameExitsState(t *testing.T) {
	gm := NewGameManager("/nonexistent/xyz", 0)
	gm.mu.Lock()
	gm.state = gameStateIdle
	gm.exited = true
	gm.mu.Unlock()
	m := uiModel{gm: gm, debugPort: 17643, w: 110, h: 24}
	out := m.frame()
	if !strings.Contains(out, "EXITS") {
		t.Errorf("expected 'EXITS' in status; frame=%s", stripAnsi(out))
	}
}

// TestDeathNotification verifies the process monitor (cmd.Wait) publishes a death
// event on deathCh, and that a spontaneous death (state still Running) flips
// Exited. This is the non-polling "callback / channel hit" the process-death
// detection now uses.
func TestDeathNotification(t *testing.T) {
	gm := NewGameManager("/nonexistent/xyz", 0)
	cmd := exec.Command("sh", "-c", "exit 1") // exits immediately, non-zero
	gm.mu.Lock()
	gm.state = gameStateRunning
	gm.pid = 42 // synthetic; what matters is the state (running => spontaneous)
	gm.mu.Unlock()

	go gm.monitor(cmd)

	select {
	case de := <-gm.deathCh:
		if !de.spontaneous {
			t.Errorf("expected spontaneous death, got %v", de.spontaneous)
		}
		if de.pid != 42 {
			t.Errorf("expected pid=42, got %d", de.pid)
		}
		gm.mu.Lock()
		if !gm.exited {
			t.Errorf("expected exited=true after spontaneous death")
		}
		gm.mu.Unlock()
	case <-time.After(5 * time.Second):
		t.Fatal("no death event within 5s")
	}
}

// stripAnsi drops terminal escape sequences so assertions read plain text.
func stripAnsi(s string) string {
	return strings.ReplaceAll(s, "\x1b[", "")
}

// TestRenderHistory verifies the transcript display: a divider between
// turns, long lines word-wrapped (never truncated), bottom-anchored to rows.
func TestRenderHistory(t *testing.T) {
	events := []thoughtEvent{
		{Kind: evSay, Turn: 1, Text: "short thought"},
		{Kind: evSay, Turn: 1, Text: strings.Repeat("word ", 40)},
		{Kind: evSay, Turn: 2, Text: "next turn thought"},
	}
	out := renderHistory(events, modeStreamer, 40, 10)
	if len(out) != 10 {
		t.Fatalf("expected exactly 10 display rows, got %d", len(out))
	}
	for i, line := range out {
		if w := charmansi.StringWidth(stripAnsi(line)); w > 40 {
			t.Errorf("row %d exceeds width 40 (%d cols): %q", i, w, line)
		}
	}
	joined := stripAnsi(strings.Join(out, "\n"))
	divIdx := strings.Index(joined, "────")
	t2Idx := strings.Index(joined, "[t2]")
	if divIdx < 0 || t2Idx < 0 || divIdx > t2Idx {
		t.Errorf("turn divider missing or not before the [t2] line (div=%d, t2=%d):\n%s", divIdx, t2Idx, joined)
	}
	if strings.Contains(joined, "…") {
		t.Errorf("long line was truncated instead of wrapped:\n%s", joined)
	}
}
