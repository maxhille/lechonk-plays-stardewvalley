package main

import (
	"context"
	"fmt"
	"os"
	"os/signal"
	"strconv"
	"syscall"
	"time"

	tea "github.com/charmbracelet/bubbletea"
)

func main() {
	// LECHONK_MODELTEST=1: run the model client self-test once, print the
	// reply, and exit. Used to verify the endpoint/model without the TUI.
	if os.Getenv("LECHONK_MODELTEST") == "1" {
		c := NewModelClient()
		fmt.Printf("[modeltest] base=%s model=%s\n", c.baseURL, c.model)
		if err := c.SelfTest(); err != nil {
			fmt.Fprintf(os.Stderr, "[modeltest] error: %v\n", err)
			os.Exit(1)
		}
		return
	}
	// LECHONK_LOOPTEST=1: one model turn against the real tools — no TUI, no
	// game side effects. Shows what Le Chonk would do on its first decision.
	if os.Getenv("LECHONK_LOOPTEST") == "1" {
		client := NewModelClient()
		ms := newModelState(client, NewGameManager("", 17643), nil, make(chan thoughtEvent, 1))
		fmt.Printf("[looptest] model=%s base=%s\n", client.model, client.baseURL)
		ms.OneTurn()
		return
	}
	gd := defaultGameDir()
	debugPort := 17643
	if v, ok := os.LookupEnv("LECHONK_DEBUG_PORT"); ok {
		if n, err := strconv.Atoi(v); err == nil {
			debugPort = n
		}
	}

	gm := NewGameManager(gd, debugPort)
	events := make(chan thoughtEvent, 256)

	// The game controller: the goroutine that OWNS the process lifecycle and
	// publishes every state change to the TUI/log the moment it happens (not
	// on the next tick). Every caller (TUI, model, debug, signal) publishes a
	// command to it instead of poking gm.Start()/gm.Stop() directly.
	gc := NewGameController(gm, events)
	gctx, gcancel := context.WithCancel(context.Background())
	go gc.Run(gctx)

	// Local-only debug HTTP endpoint (relaunch / stop / status).
	ds := NewDebugServer(gm, gc)
	if err := ds.Start(); err != nil {
		fmt.Fprintf(os.Stderr, "[lechonk] debug http: %v\n", err)
	}

	// The Le Chonk thinking loop and its TUI feed.
	client := NewModelClient()
	ms := newModelState(client, gm, gc, events)

	// SIGTERM (e.g. systemctl --user stop / kill) -> graceful stop.
	// Ctrl+C is handled by the TUI itself.
	ch := make(chan os.Signal, 1)
	signal.Notify(ch, syscall.SIGINT, syscall.SIGTERM)
	go func() {
		<-ch
		// Graceful stop: drop new cmds to the controller, stop the model, then
		// reap the game process tree DIRECTLY (gm.Stop is idempotent — this is
		// the guarantee that harness death always kills the game, even mid-op).
		gcancel()
		ms.Stop()
		gm.Stop()
		ds.Stop()
		time.Sleep(300 * time.Millisecond)
		os.Exit(0)
	}()

	// Headless: no TTY attached (or LECHONK_HEADLESS=1). Skip the TUI and run
	// Le Chonk directly; the transcript log and the debug endpoint stay the
	// way to watch him. The signal handler above stops everything and exits.
	if os.Getenv("LECHONK_HEADLESS") == "1" || !stdoutIsTTY() {
		fmt.Fprintf(os.Stderr, "[lechonk] headless (no TTY) — running Le Chonk; transcript at /tmp/lechonk-transcript.log\n")
		ms.Start()
		<-ch
		os.Exit(0)
	}

	model := newUIModel(gm, gc, debugPort, ms, events)
	// Boot info goes to stderr — operator diagnostics, not transcript content.
	fmt.Fprintf(os.Stderr, "[lechonk] game dir: %s\n", gd)
	fmt.Fprintf(os.Stderr, "[lechonk] debug api: http://127.0.0.1:%d  (GET /status, POST /launch /stop /restart)\n", debugPort)
	fmt.Fprintf(os.Stderr, "[lechonk] model: %s   %s\n", client.model, client.baseURL)
	fmt.Fprintf(os.Stderr, "[lechonk] transcript: /tmp/lechonk-transcript.log  (tail it while he runs)\n")

	p := tea.NewProgram(model, tea.WithAltScreen())
	_, _ = p.Run()
	// Shutdown (always, regardless of how Run() returned): stop the controller,
	// the model, reap the game process tree directly (idempotent — the guarantee
	// that harness death kills the game), and the debug server.
	gcancel()
	ms.Stop()
	gm.Stop()
	ds.Stop()
}

// stdoutIsTTY reports whether stdout is attached to a terminal.
func stdoutIsTTY() bool {
	fi, err := os.Stdout.Stat()
	if err != nil {
		return false
	}
	return fi.Mode()&os.ModeCharDevice != 0
}
