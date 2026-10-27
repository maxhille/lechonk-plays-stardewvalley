package main

import (
	"context"
	"strings"
	"testing"
	"time"
)

// TestControllerPublishesEvents drives the game controller without spawning any
// process: a nonexistent game dir makes gm.Start() return "game dir not found:
// …" before exec, so we only verify the controller (a) publishes lifecycle
// events in order and (b) completes the Launch reply path with a failure.
func TestControllerPublishesEvents(t *testing.T) {
	gm := NewGameManager("/nonexistent/game/dir/should-not-exist-xyz-1234", 0)
	events := make(chan thoughtEvent, 64)
	gc := NewGameController(gm, events)

	gctx, gcancel := context.WithCancel(context.Background())
	go gc.Run(gctx)
	defer gcancel()

	// Drive Launch and WAIT for the reply (the controller runs in another goroutine).
	replyCh := make(chan gameLaunchResult, 1)
	gc.Launch(replyCh)
	var r gameLaunchResult
	select {
	case r = <-replyCh:
	case <-time.After(5 * time.Second):
		t.Fatal("Launch did not reply within 5s")
	}

	// Now drain whatever lifecycle events the controller published.
	var got []string
	deadline := time.Now().Add(1 * time.Second)
	for time.Now().Before(deadline) {
		select {
		case e := <-events:
			got = append(got, e.Text)
		case <-time.After(50 * time.Millisecond):
		}
	}

	gcancel()
	t.Logf("events: %v", got)
	t.Logf("result: ok=%v pid=%d err=%q", r.ok, r.pid, r.err)

	if r.ok {
		t.Errorf("expected launch to fail (no game dir), but got ok=%v pid=%d", r.ok, r.pid)
		return
	}
	if r.err == "" {
		t.Errorf("expected a launch error, got empty err")
	}
	foundLaunch, foundFail := false, false
	var order []string
	for _, s := range got {
		order = append(order, s)
		switch {
		case s == "game: launching the game…":
			foundLaunch = true
		case strings.HasPrefix(s, "game: start failed:"):
			foundFail = true
		}
	}
	if !foundLaunch {
		t.Errorf("expected to publish \"game: launching the game…\"; got %v", got)
	}
	if !foundFail {
		t.Errorf("expected a \"game: start failed: …\" event; got %v", got)
	}
	// Verify ordering: launching before start failed.
	launchIdx, failIdx := -1, -1
	for i, s := range order {
		switch {
		case strings.HasPrefix(s, "game: launching"):
			launchIdx = i
		case strings.HasPrefix(s, "game: start failed:"):
			failIdx = i
		}
	}
	if launchIdx == -1 || failIdx == -1 || failIdx <= launchIdx {
		t.Errorf("expected \"launching\" before \"start failed\"; got %v", order)
	}
	t.Log("controller event-publish ordering OK")
}
