package main

import (
	"fmt"
	"strings"
	"time"

	tea "github.com/charmbracelet/bubbletea"
	charmansi "github.com/charmbracelet/x/ansi"
	lipgloss "github.com/charmbracelet/lipgloss"
)

// Color palette: a warm, earthy "Stardew Valley" scheme over a dark terminal.
// parchment text, a gold wood-trim border, grass green for running, terracotta
// red for stopped. Note: there is no canonical SV TUI palette — these hexes are
// a reasoned guess; tweak the constants below to taste.
const (
	palBorder    = "#d4a843" // warm gold — the game's wood trim / money gold
	palTitle     = "#f7e5c6" // parchment cream (heading)
	palRunning   = "#8ec85f" // grass green (RUNNING)
	palStopped   = "#d95d5d" // terracotta red (STOPPED)
	palTranscript = "#ede0c4" // warm off-white (main transcript text)
	palFooter    = "#b8a88a" // muted tan (keys / secondary)
	palDivider   = "#6e6a5e" // subtle warm-gray (divider line)
)

// Styles for the full-screen frame.
var (
	titleStyle  = lipgloss.NewStyle().Bold(true).Foreground(lipgloss.Color(palTitle))
	transcriptStyle = lipgloss.NewStyle().Foreground(lipgloss.Color(palTranscript))
	dividerStyle = lipgloss.NewStyle().Foreground(lipgloss.Color(palDivider))
	statusRun   = lipgloss.NewStyle().Bold(true).Foreground(lipgloss.Color(palRunning))
	statusStop  = lipgloss.NewStyle().Bold(true).Foreground(lipgloss.Color(palStopped))
	footerStyle = lipgloss.NewStyle().Foreground(lipgloss.Color(palFooter))
	frameStyle  = lipgloss.NewStyle().
		Border(lipgloss.NormalBorder(), true, true, true, true). // top, right, bottom, left
		BorderForeground(lipgloss.Color(palBorder))
)

type tickMsg struct{}

// uiModel implements tea.Model (v1): Init() Cmd, Update(Msg) (Model, Cmd), View() string.
type uiModel struct {
	gm        *GameManager
	debugPort int
	lines     []string
	w, h      int // terminal size, set from tea.WindowSizeMsg (sent once, then on resize)
}

func newUIModel(gm *GameManager, debugPort int) uiModel {
	return uiModel{
		gm:        gm,
		debugPort: debugPort,
		lines:     []string{},
	}
}

func (m uiModel) addLine(s string) {
	m.lines = append(m.lines, s)
	if len(m.lines) > 500 {
		m.lines = m.lines[len(m.lines)-500:]
	}
}

// Init starts a status tick so the view auto-refreshes (game up, api up, uptime).
func (m uiModel) Init() tea.Cmd {
	return tea.Tick(500 * time.Millisecond, func(time.Time) tea.Msg {
		return tickMsg{}
	})
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
	status := statusLine(s)
	footer := fmt.Sprintf(
		"l launch   s stop   r restart   q quit   debug http://127.0.0.1:%d",
		m.debugPort,
	)
	divider := strings.Repeat("─", insideW)

	// Fixed rows: title, status, divider, footer. Everything between is the transcript.
	transcriptRows := insideH - 4
	if transcriptRows < 0 {
		transcriptRows = 0
	}

	content := make([]string, insideH)
	content[0] = padRight(titleStyle.Render(title), insideW)
	if s.Running {
		content[1] = padRight(statusRun.Render(status), insideW)
	} else {
		content[1] = padRight(statusStop.Render(status), insideW)
	}
	content[2] = dividerStyle.Render(divider)
	content[insideH-1] = padRight(footerStyle.Render(footer), insideW)

	// Transcript: bottom-anchored (latest line closest to the footer).
	var trans []string
	if len(m.lines) > transcriptRows {
		trans = m.lines[len(m.lines)-transcriptRows:]
	} else {
		trans = m.lines
	}
	topPad := transcriptRows - len(trans)
	for i := 0; i < topPad; i++ {
		content[3+i] = ""
	}
	for i, line := range trans {
		content[3+topPad+i] = padRight(transcriptStyle.Render(line), insideW)
	}

	return frameStyle.Render(strings.Join(content, "\n"))
}

// statusLine is the single status row shown under the title.
func statusLine(s GameStatus) string {
	if s.Running {
		port := "pending"
		if s.PortUp {
			port = "up"
		}
		return fmt.Sprintf("game: RUNNING   pid=%d   up=%s   api %s", s.PID, s.Uptime.Round(time.Second), port)
	}
	return "game: STOPPED"
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
	lines = append(lines, fmt.Sprintf("debug api: http://127.0.0.1:%d   (status / launch / stop / restart)", m.debugPort))
	if s.Error != "" {
		lines = append(lines, "last error: "+s.Error)
	}
	lines = append(lines, "")
	const show = 12
	n := len(m.lines)
	start := 0
	if n > show {
		start = n - show
	}
	for i := start; i < n; i++ {
		lines = append(lines, m.lines[i])
	}
	lines = append(lines, "")
	lines = append(lines, "keys:  l launch · s stop · r restart · q quit   (auto-refreshes ~0.5s)")
	return strings.Join(lines, "\n")
}

func (m uiModel) Update(msg tea.Msg) (tea.Model, tea.Cmd) {
	mm := uiModel(m)
	switch msg := msg.(type) {
	case tea.WindowSizeMsg:
		mm.w = msg.Width
		mm.h = msg.Height
	case tea.KeyMsg:
		if msg.Type == tea.KeyRunes && len(msg.Runes) > 0 {
			ch := strings.ToLower(string(msg.Runes[0]))
			switch ch {
			case "q":
				mm.addLine("quit… stopping the game")
				mm.gm.Stop()
				return tea.Model(mm), func() tea.Msg { return tea.Quit() }
			case "l":
				if err := mm.gm.Start(); err != nil {
					mm.addLine("launch: " + err.Error())
				} else {
					mm.addLine("launch: game starting — api will come up shortly…")
				}
			case "s":
				mm.gm.Stop()
				mm.addLine("stopped the game")
			case "r":
				mm.gm.Stop()
				time.Sleep(1 * time.Second)
				if err := mm.gm.Start(); err != nil {
					mm.addLine("restart: " + err.Error())
				} else {
					mm.addLine("restart: relaunching the game…")
				}
			case "?":
				mm.addLine("keys: l launch · s stop · r restart · q quit")
			}
		}
	}
	// View is re-rendered after every message; return the (possibly changed) model.
	return tea.Model(mm), nil
}
