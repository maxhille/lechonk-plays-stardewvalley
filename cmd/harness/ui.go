package main

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strings"
	"time"

	tea "github.com/charmbracelet/bubbletea"
	lipgloss "github.com/charmbracelet/lipgloss"
	charmansi "github.com/charmbracelet/x/ansi"
)

// Color palette: a warm, earthy "Stardew Valley" scheme over a dark terminal.
// parchment text, a gold wood-trim border, grass green for running, terracotta
// red for stopped. Note: there is no canonical SV TUI palette — these hexes are
// a reasoned guess; tweak the constants below to taste.
const (
	palBorder     = "#d4a843" // warm gold — the game's wood trim / money gold
	palTitle      = "#f7e5c6" // parchment cream (heading)
	palRunning    = "#8ec85f" // grass green (RUNNING)
	palStopped    = "#d95d5d" // terracotta red (STOPPED)
	palExited     = "#e0b13a" // amber — process died on its own (not a clean stop)
	palTranscript = "#ede0c4" // warm off-white (main transcript text)
	palFooter     = "#b8a88a" // muted tan (keys / secondary)
	palDivider    = "#6e6a5e" // subtle warm-gray (divider line)
	palPaused     = "#8fb8d4" // calm blue (PAUSED)
)

// Styles for the full-screen frame.
var (
	titleStyle      = lipgloss.NewStyle().Bold(true).Foreground(lipgloss.Color(palTitle))
	transcriptStyle = lipgloss.NewStyle().Foreground(lipgloss.Color(palTranscript))
	dividerStyle    = lipgloss.NewStyle().Foreground(lipgloss.Color(palDivider))
	statusRun       = lipgloss.NewStyle().Bold(true).Foreground(lipgloss.Color(palRunning))
	statusStop      = lipgloss.NewStyle().Bold(true).Foreground(lipgloss.Color(palStopped))
	statusExited    = lipgloss.NewStyle().Bold(true).Foreground(lipgloss.Color(palExited))
	statusPaused    = lipgloss.NewStyle().Bold(true).Foreground(lipgloss.Color(palPaused))
	footerStyle     = lipgloss.NewStyle().Foreground(lipgloss.Color(palFooter))
	frameStyle      = lipgloss.NewStyle().
			Border(lipgloss.NormalBorder(), true, true, true, true). // top, right, bottom, left
			BorderForeground(lipgloss.Color(palBorder))
)

// Display modes for the transcript. Streamer (the default at launch) shows only
// Le Chonk's words; debug shows the mechanical view (tool calls, results,
// errors, harness notes). Press d to toggle between them.
const (
	modeStreamer = "streamer"
	modeDebug    = "debug"
)

type tickMsg struct{}

// uiModel implements tea.Model (v1): Init() Cmd, Update(Msg) (Model, Cmd), View() string.
type uiModel struct {
	gm        *GameManager
	gc        *gameController
	debugPort int
	ms        *modelState       // the Le Chonk thinking loop
	events    chan thoughtEvent // raw events pushed by the model loop
	history   []thoughtEvent    // raw events, rendered per display mode in View()
	mode      string            // modeStreamer (default) or modeDebug
	paused    bool              // game paused via the admin /pause toggle (key p)
	w, h      int               // terminal size, set from tea.WindowSizeMsg
}

func newUIModel(gm *GameManager, gc *gameController, debugPort int, ms *modelState, events chan thoughtEvent) uiModel {
	return uiModel{
		gm:        gm,
		gc:        gc,
		debugPort: debugPort,
		ms:        ms,
		events:    events,
		history:   []thoughtEvent{},
		mode:      modeStreamer,
	}
}

// addLine records a harness notice as an evInfo event. Shown in debug mode
// only; streamer mode ignores it (it shows his words alone).
func (m *uiModel) addLine(s string) {
	m.history = append(m.history, thoughtEvent{Kind: evInfo, Turn: 0, Text: s})
	m.trimHistory()
}

func (m *uiModel) trimHistory() {
	if len(m.history) > 1000 {
		m.history = m.history[len(m.history)-1000:]
	}
}

// togglePause hits the mod's admin /pause endpoint: it taps Escape on the
// game thread and freezes any running script. The response is the source of
// truth for the pause state — the TUI does not guess.
func (m *uiModel) togglePause() {
	if !m.gm.APIUp() {
		m.addLine("game not up — cannot pause")
		return
	}
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, fmt.Sprintf("http://127.0.0.1:%d/pause", gameAPIPort), nil)
	if err != nil {
		m.addLine("pause: " + err.Error())
		return
	}
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		m.addLine("pause failed: " + err.Error())
		return
	}
	defer resp.Body.Close()
	body, err := io.ReadAll(resp.Body)
	if err != nil {
		m.addLine("pause: read response: " + err.Error())
		return
	}
	var out struct {
		Ok     bool   `json:"ok"`
		Paused bool   `json:"paused"`
		Error  string `json:"error"`
	}
	if err := json.Unmarshal(body, &out); err != nil {
		m.addLine("pause: bad response: " + string(body))
		return
	}
	if !out.Ok {
		msg := out.Error
		if msg == "" {
			msg = "unknown error"
		}
		m.addLine("pause: " + msg)
		return
	}
	m.paused = out.Paused
	if out.Paused {
		m.addLine("game PAUSED (script frozen — p to resume)")
	} else {
		m.addLine("game RESUMED")
	}
}

// evLines turns one raw event into zero or more transcript lines for the given
// display mode. Multi-line payloads (pretty-printed JSON results) split into
// several lines so the frame wraps and pads each one.
//
// Streamer mode shows only his words (evSay). Debug mode shows the mechanical
// view — tool calls, results, errors, harness notices — and NOT his words
// (those are the streamer's lane). Hidden reasoning (evThink) is never shown.
func evLines(ev thoughtEvent, mode string) []string {
	streamer := mode == modeStreamer
	if streamer != (ev.Kind == evSay) {
		return nil
	}
	var lines []string
	switch ev.Kind {
	case evSay:
		if streamer {
			// Chat view: the raw message alone — viewers know who's talking.
			lines = []string{ev.Text}
		} else {
			lines = []string{"Le Chonk: " + ev.Text}
		}
	case evTool:
		lines = []string{"    → action to game: " + ev.Text}
	case evResult:
		lines = []string{"    ← game → model:"}
		if ev.Text != "" {
			for _, ln := range strings.Split(ev.Text, "\n") {
				lines = append(lines, "    "+ln)
			}
		}
	case evError:
		lines = []string{"    ✗ error: " + ev.Text}
	case evInfo:
		lines = []string{"    · " + ev.Text}
	default: // lifecycle (evStart/evStop/evDone) and evThink: debug mode only via addLine never fires them; kept for completeness
		switch ev.Kind {
		case evStart:
			lines = []string{"Le Chonk is starting…"}
		case evStop:
			lines = []string{"Le Chonk stopped."}
		case evDone:
			lines = []string{"Le Chonk: DONE — day complete."}
		}
	}
	// Turn tags are debug-lane chrome — chat lines carry no numbering.
	if !streamer && ev.Turn > 0 && len(lines) > 0 {
		lines[0] = fmt.Sprintf("[t%d] %s", ev.Turn, lines[0])
	}
	return lines
}

// refreshTick is how often the frame re-fetches game state / uptime / api.
const refreshTick = 200 * time.Millisecond

// tickCmd returns the Cmd that produces one refresh tick after refreshTick.
// tea.Tick is ONE-SHOT — the loop must hand back a new one after each tick to
// keep the screen alive. Init seeds the first, Update re-schedules the rest.
func tickCmd() tea.Cmd {
	return tea.Tick(refreshTick, func(time.Time) tea.Msg {
		return tickMsg{}
	})
}

// Init schedules the first status tick (game up, api up, uptime).
func (m uiModel) Init() tea.Cmd {
	return tickCmd()
}

// padRight truncates s to w display-columns (tail "…") and right-pads with
// spaces to exactly w. Used to make every frame line an exact width so the
// outer border renders at exactly the terminal size.
func padRight(s string, w int) string {
	if w <= 0 {
		return ""
	}
	if charmansi.StringWidth(s) > w {
		return charmansi.Truncate(s, w, "…")
	}
	return s + strings.Repeat(" ", w-charmansi.StringWidth(s))
}

// wrapText word-wraps s to at most width columns (rune-aware). Long tokens
// with no space break hard at the edge.
func wrapText(s string, width int) []string {
	runes := []rune(s)
	if width <= 0 || len(runes) <= width {
		return []string{s}
	}
	var out []string
	rest := runes
	for len(rest) > width {
		cut := width
		// Prefer a word boundary: back up to the last space in this row.
		if rest[cut-1] != ' ' {
			for i := cut - 1; i > 0; i-- {
				if rest[i] == ' ' {
					cut = i
					break
				}
			}
		}
		out = append(out, string(rest[:cut]))
		rest = rest[cut:]
		if len(rest) > 0 && rest[0] == ' ' {
			rest = rest[1:]
		}
	}
	if len(rest) > 0 {
		out = append(out, string(rest))
	}
	return out
}

// renderHistory turns raw events into display lines for the active mode: a
// divider between turns, word-wrapped text, bottom-anchored to rows. Returns
// exactly rows entries (nil when rows <= 0).
// hasLaterEvent reports whether any event follows the one at ev's position in
// the slice (identity by index, since events repeat).
func hasLaterEvent(events []thoughtEvent, ev thoughtEvent) bool {
	for i := len(events) - 1; i >= 0; i-- {
		if events[i] == ev {
			return i < len(events)-1
		}
	}
	return false
}

func renderHistory(events []thoughtEvent, mode string, width, rows int) []string {
	if rows <= 0 {
		return nil
	}
	debug := mode == modeDebug
	var all []string
	lastTurn := 0
	seenTurn := false
	for _, ev := range events {
		// The thinking indicator is ephemeral: show it only while it is the
		// newest thing — once his words (or any later event) arrive, it goes.
		if ev.Kind == evThinking {
			if mode != modeStreamer || hasLaterEvent(events, ev) {
				continue
			}
			all = append(all, transcriptStyle.Render("…"))
			continue
		}
		lines := evLines(ev, mode)
		if len(lines) == 0 {
			continue
		}
		// Turn dividers are debug-lane chrome; chat lines just flow.
		if debug && ev.Turn > 0 {
			if seenTurn && ev.Turn > lastTurn {
				all = append(all, dividerStyle.Render(strings.Repeat("─", width)))
			}
			lastTurn = ev.Turn
			seenTurn = true
		}
		for _, ln := range lines {
			for _, wl := range wrapText(ln, width) {
				all = append(all, transcriptStyle.Render(wl))
			}
		}
	}
	start := 0
	if len(all) > rows {
		start = len(all) - rows
	}
	out := make([]string, rows)
	for i := 0; i < rows; i++ {
		idx := start + i
		if idx < len(all) {
			out[i] = padRight(all[idx], width)
		}
	}
	return out
}

// View renders the full-screen frame, falling back to a plain compact view
// when the terminal size is still unknown (first paint) or the window is tiny.
func (m uiModel) View() string {
	if m.w < 50 || m.h < 8 {
		return m.plainView()
	}
	return m.frame()
}

// frame builds a border that is exactly the terminal size.
func (m uiModel) frame() string {
	w, h := m.w, m.h
	insideW := w - 2
	insideH := h - 2

	s := m.gm.Status()
	title := "Le Chonk plays Stardew Valley"
	divider := strings.Repeat("─", insideW)

	// Streamer mode is a pure chat view: the title and his words, nothing else.
	if m.mode == modeStreamer {
		content := make([]string, insideH)
		content[0] = padRight(titleStyle.Render(title), insideW)
		content[1] = dividerStyle.Render(divider)
		rows := insideH - 2
		if rows > 0 {
			for i, line := range renderHistory(m.history, modeStreamer, insideW, rows) {
				content[2+i] = line
			}
		}
		return frameStyle.Render(strings.Join(content, "\n"))
	}

	// Debug mode: the full mechanical view.
	status := statusLine(s, m.ms, m.gm, m.paused)
	footer := fmt.Sprintf(
		"mode: %s (d toggle)   l start   s stop   r restart   p pause   q quit   debug http://127.0.0.1:%d",
		strings.ToUpper(m.mode), m.debugPort,
	)

	// Fixed rows: title, status, divider, footer. Everything between is the transcript.
	transcriptRows := insideH - 4
	if transcriptRows < 0 {
		transcriptRows = 0
	}

	content := make([]string, insideH)
	content[0] = padRight(titleStyle.Render(title), insideW)
	// Color by whether the game is paused (admin toggle), genuinely up (harness-owned
	// OR any external process with the mod API listening), down-but-self-exited
	// (process died), or a clean stop.
	if m.paused {
		content[1] = padRight(statusPaused.Render(status), insideW)
	} else if s.Running || m.gm.APIUp() {
		content[1] = padRight(statusRun.Render(status), insideW)
	} else if s.Exited {
		content[1] = padRight(statusExited.Render(status), insideW)
	} else {
		content[1] = padRight(statusStop.Render(status), insideW)
	}
	content[2] = dividerStyle.Render(divider)
	content[insideH-1] = padRight(footerStyle.Render(footer), insideW)

	// Transcript: bottom-anchored, turn-divided, word-wrapped.
	if transcriptRows > 0 {
		for i, line := range renderHistory(m.history, modeDebug, insideW, transcriptRows) {
			content[3+i] = line
		}
	}

	return frameStyle.Render(strings.Join(content, "\n"))
}

// statusLine is the single status row shown under the title.
func statusLine(s GameStatus, ms *modelState, gm *GameManager, paused bool) string {
	var base string
	if s.Running {
		port := "pending"
		if s.PortUp {
			port = "up"
		}
		base = fmt.Sprintf("game: RUNNING   pid=%d   up=%s   api %s", s.PID, s.Uptime.Round(time.Second), port)
	} else if gm != nil && gm.APIUp() {
		base = "game: UP (external — mod API listening)"
	} else if s.Exited {
		base = "game: EXITS (process died on its own — not a clean stop)"
	} else {
		base = "game: STOPPED"
	}
	if paused {
		base += "   PAUSED"
	}
	lc := "idle"
	if ms != nil && ms.Running() {
		lc = "ON"
	}
	return base + "   Le Chonk: " + lc + ctxSuffix(ms)
}

// ctxSuffix shows the size of the model's last prompt, so you can feel how
// big the conversation is getting.
func ctxSuffix(ms *modelState) string {
	if ms == nil {
		return ""
	}
	t := ms.CtxTokens()
	if t <= 0 {
		return ""
	}
	if t >= 1000 {
		return fmt.Sprintf("   ctx: %.1fk tok", float64(t)/1000)
	}
	return fmt.Sprintf("   ctx: %d tok", t)
}

// plainView is the compact pre-full-screen layout, used when the terminal is
// still unknown or too small to fit a frame.
func (m uiModel) plainView() string {
	s := m.gm.Status()
	lines := []string{"Le Chonk plays Stardew Valley"}
	if s.Running {
		port := "pending"
		if s.PortUp {
			port = "up"
		}
		lines = append(lines, fmt.Sprintf("game: RUNNING   pid=%d   up=%s   api %s", s.PID, s.Uptime.Round(time.Second), port))
	} else {
		lines = append(lines, "game: STOPPED")
	}
	lc := "idle"
	if m.ms != nil && m.ms.Running() {
		lc = "ON"
	}
	lines = append(lines, fmt.Sprintf("Le Chonk: %s   debug api: http://127.0.0.1:%d  (status / launch / stop / restart)", lc, m.debugPort))
	if s.Error != "" {
		lines = append(lines, "last error: "+s.Error)
	}
	lines = append(lines, "")
	lines = append(lines, renderHistory(m.history, modeDebug, 80, 12)...)
	lines = append(lines, "")
	lines = append(lines, fmt.Sprintf("mode: %s (d)   keys: l start · s stop · r restart · p pause · q quit   (auto-refreshes ~0.2s)", strings.ToUpper(m.mode)))
	return strings.Join(lines, "\n")
}

func (m uiModel) Update(msg tea.Msg) (tea.Model, tea.Cmd) {
	mm := uiModel(m)

		// Drain any model-loop events (non-blocking so we never stall the TUI).
	// Raw events go into history; View() renders them per the display mode.
drain:
	for {
		select {
		case ev, ok := <-mm.events:
			if !ok {
				break drain
			}
			mm.history = append(mm.history, ev)
			mm.trimHistory()
		default:
			break drain
		}
	}

	switch msg := msg.(type) {
	case tea.WindowSizeMsg:
		mm.w = msg.Width
		mm.h = msg.Height
	case tickMsg:
		// Re-render happens automatically (View re-fetches gm.Status()). Just
		// keep the refresh chain going.
		return tea.Model(mm), tickCmd()
	case tea.KeyMsg:
		if msg.Type == tea.KeyRunes && len(msg.Runes) > 0 {
			ch := strings.ToLower(string(msg.Runes[0]))
			switch ch {
			case "q":
				mm.addLine("quit… stopping Le Chonk + the game")
				mm.ms.Stop()
				mm.gc.StopCmd()
				return tea.Model(mm), func() tea.Msg { return tea.Quit() }
			case "l":
				if mm.ms.Running() {
					mm.addLine("Le Chonk is already running")
				} else {
					mm.ms.Start()
					mm.addLine("starting Le Chonk… (l start · s stop · r restart · p pause · q quit)")
				}
			case "s":
				if mm.ms.Running() {
					mm.ms.Stop()
					mm.addLine("stopping Le Chonk…")
				} else {
					mm.addLine("Le Chonk is not running")
				}
				mm.gc.StopCmd()
				mm.paused = false
			case "r":
				mm.ms.Stop()
				mm.gc.StopCmd()
				mm.paused = false
				time.Sleep(2 * time.Second)
				mm.ms.Start()
				mm.addLine("restarting Le Chonk…")
			case "p":
				mm.togglePause()
			case "d":
				// Toggle the display mode. Rendering is retroactive — history holds
				// raw events, so the whole session re-renders in the new mode.
				if mm.mode == modeStreamer {
					mm.mode = modeDebug
				} else {
					mm.mode = modeStreamer
				}
				mm.addLine("display mode: " + mm.mode)
			case "?":
				mm.addLine("keys: l start · s stop · r restart · p pause · d mode · q quit · ? help")
			}
		}
	}
	// View is re-rendered after every message; return the (possibly changed) model.
	return tea.Model(mm), nil
}
