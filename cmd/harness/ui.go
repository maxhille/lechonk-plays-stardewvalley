package main

import (
	"fmt"
	"strings"
	"time"

	tea "github.com/charmbracelet/bubbletea"
)

type tickMsg struct{}

// uiModel implements tea.Model (v1): Init() Cmd, Update(Msg) (Model, Cmd), View() string.
type uiModel struct {
	gm        *GameManager
	debugPort int
	lines     []string
}

func newUIModel(gm *GameManager, debugPort int) uiModel {
	return uiModel{
		gm:        gm,
		debugPort: debugPort,
		lines:     []string{"LeChonk — Phase 2: the brain (debug TUI)"},
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

func (m uiModel) View() string {
	s := m.gm.Status()
	lines := []string{"  LeChonk — Phase 2: the brain"}
	if s.Running {
		port := "pending"
		if s.PortUp {
			port = "up"
		}
		lines = append(lines, fmt.Sprintf(
			"  game: RUNNING   pid=%d   up=%s   api %s", s.PID, s.Uptime.Round(time.Second), port))
	} else {
		lines = append(lines, "  game: STOPPED")
	}
	lines = append(lines, fmt.Sprintf(
		"  debug api: http://127.0.0.1:%d   (status / launch / stop / restart)", m.debugPort))
	if s.Error != "" {
		lines = append(lines, "  last error: "+s.Error)
	}
	lines = append(lines, "")
	// show the most recent N lines
	const show = 12
	n := len(m.lines)
	start := 0
	if n > show {
		start = n - show
	}
	for i := start; i < n; i++ {
		lines = append(lines, "  " + m.lines[i])
	}
	lines = append(lines, "")
	lines = append(lines, "  keys:  l launch · s stop · r restart · q quit   (auto-refreshes ~0.5s)")
	return strings.Join(lines, "\n")
}

func (m uiModel) Update(msg tea.Msg) (tea.Model, tea.Cmd) {
	mm := uiModel(m)
	switch msg := msg.(type) {
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
