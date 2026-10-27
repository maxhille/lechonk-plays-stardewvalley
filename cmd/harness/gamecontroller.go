package main

import (
	"context"
	"fmt"
	"time"
)

// gameController is the goroutine that OWNS the game process lifecycle. Every
// caller (TUI keys, the model's launch_game, the debug HTTP endpoints, the
// signal handler) publishes a command to cmdCh; the controller performs it
// serially in its own goroutine and pushes a lifecycle event to `events` the
// moment state changes. The TUI (and log) therefore react to start/stop
// immediately instead of waiting for the periodic tick, and the blocking
// reaping in Stop() never stalls the TUI event loop.
type gameController struct {
	gm     *GameManager
	cmdCh  chan gameCmd
	events chan thoughtEvent
}

type gameCmdKind int

const (
	opStart gameCmdKind = iota
	opStop
	opRestart
	opLaunch // blocking: result sent back on cmd.replyCh
)

type gameCmd struct {
	op      gameCmdKind
	replyCh chan gameLaunchResult // set only for opLaunch
}

type gameLaunchResult struct {
	ok    bool
	pid   int
	err   string
	state string
}

func NewGameController(gm *GameManager, events chan thoughtEvent) *gameController {
	return &gameController{
		gm:     gm,
		cmdCh:  make(chan gameCmd, 8),
		events: events,
	}
}

// Run must be called from a goroutine. It owns the process lifecycle and
// publishes every state change to events.
func (g *gameController) Run(ctx context.Context) {
	for {
		select {
		case cmd := <-g.cmdCh:
			g.handle(cmd)
		case de := <-g.gm.deathCh:
			g.handleDeath(de)
		case <-ctx.Done():
			return
		}
	}
}

// handleDeath reacts to the process monitor's death notification. A spontaneous
// death (window closed / crash, not a stop we initiated) gets announced to the
// transcript; a clean stop is a no-op (the TUI's amber EXITS state already
// reflects the dead process and the stop is not "news").
func (g *gameController) handleDeath(de deathEvent) {
	if !de.spontaneous {
		return
	}
	g.publish(fmt.Sprintf("process died on its own (pid %d) — window closed or crash; Le Chonk can relaunch with launch_game", de.pid))
}

// publish pushes a lifecycle line to the TUI/log. Non-blocking: if the TUI
// is busy it will catch up on the next drain.
func (g *gameController) publish(text string) {
	select {
	case g.events <- thoughtEvent{Kind: evInfo, Turn: 0, Text: "game: " + text}:
	default:
	}
}

// sendReply pushes the launch outcome back to the caller. Non-blocking.
func (g *gameController) sendReply(replyCh chan gameLaunchResult, r gameLaunchResult) {
	select {
	case replyCh <- r:
	default:
	}
}

func (g *gameController) handle(cmd gameCmd) {
	switch cmd.op {
	case opLaunch:
		g.launch(cmd.replyCh)
	case opStart:
		_ = g.doStart()
	case opStop:
		g.doStop()
	case opRestart:
		g.doRestart()
	}
}

func (g *gameController) doStart() error {
	g.publish("launching the game…")
	if err := g.gm.Start(); err != nil {
		g.publish("start failed: " + err.Error())
		return err
	}
	g.publish(fmt.Sprintf("started  pid=%d", g.gm.pid))
	return nil
}

func (g *gameController) doStop() {
	g.publish("stopping the game…")
	g.gm.Stop()
	g.publish("stopped")
}

func (g *gameController) doRestart() {
	g.publish("restarting…")
	g.gm.Stop()
	time.Sleep(1 * time.Second)
	g.publish("launching the game…")
	if err := g.gm.Start(); err != nil {
		g.publish("restart failed: " + err.Error())
		return
	}
	g.publish(fmt.Sprintf("restarted  pid=%d", g.gm.pid))
}

func (g *gameController) launch(replyCh chan gameLaunchResult) {
	if s := g.gm.Status(); s.Running || g.gm.APIUp() {
		g.publish("already up — checking world state…")
		g.sendReply(replyCh, gameLaunchResult{ok: true, pid: s.PID, state: "already up"})
		return
	}
	if err := g.doStart(); err != nil {
		g.sendReply(replyCh, gameLaunchResult{ok: false, err: err.Error()})
		return
	}
	g.sendReply(replyCh, gameLaunchResult{ok: true, pid: g.gm.pid, state: "started"})
}

// Fire-and-forget command publishers (buffered cmdCh).
func (g *gameController) StartCmd() {
	select {
	case g.cmdCh <- gameCmd{op: opStart}:
	default:
	}
}
func (g *gameController) StopCmd() {
	select {
	case g.cmdCh <- gameCmd{op: opStop}:
	default:
	}
}
func (g *gameController) RestartCmd() {
	select {
	case g.cmdCh <- gameCmd{op: opRestart}:
	default:
	}
}

// Launch asks the controller to ensure the game is up and reports the result.
// The caller blocks on replyCh (it must not be closed by anything else).
func (g *gameController) Launch(replyCh chan gameLaunchResult) {
	select {
	case g.cmdCh <- gameCmd{op: opLaunch, replyCh: replyCh}:
	default:
	}
}
