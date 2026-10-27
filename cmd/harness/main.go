package main

import (
	"fmt"
	"os"
	"os/signal"
	"strconv"
	"syscall"
	"time"

	tea "github.com/charmbracelet/bubbletea"
)

func main() {
	gd := defaultGameDir()
	debugPort := 17643
	if v, ok := os.LookupEnv("LECHONK_DEBUG_PORT"); ok {
		if n, err := strconv.Atoi(v); err == nil {
			debugPort = n
		}
	}

	gm := NewGameManager(gd, debugPort)

	// Local-only debug HTTP endpoint (relaunch / stop / status).
	ds := NewDebugServer(gm)
	if err := ds.Start(); err != nil {
		fmt.Fprintf(os.Stderr, "[lechonk] debug http: %v\n", err)
	}

	// SIGTERM (e.g. systemctl --user stop / kill) -> graceful stop of the game.
	// Ctrl+C is handled by the TUI itself.
	ch := make(chan os.Signal, 1)
	signal.Notify(ch, syscall.SIGINT, syscall.SIGTERM)
	go func() {
		<-ch
		gm.Stop()
		ds.Stop()
		time.Sleep(300 * time.Millisecond)
		os.Exit(0)
	}()

	model := newUIModel(gm, debugPort)
	model.addLine(fmt.Sprintf("game dir: %s", gd))
	model.addLine(fmt.Sprintf("debug api: http://127.0.0.1:%d  (GET /status, POST /launch /stop /restart)", debugPort))

	p := tea.NewProgram(model)
	if _, err := p.Run(); err != nil {
		// Ctrl+C (interrupt) or fatal: make sure the game is down too.
		gm.Stop()
		ds.Stop()
	}
}
