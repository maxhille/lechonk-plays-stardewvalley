package main

import (
	"encoding/json"
	"fmt"
	"net"
	"net/http"
	"sync"
	"time"
)

// DebugServer is a local-only HTTP endpoint that exposes the game manager
// for manual relaunch / stop / status (debugging). It is bound to 127.0.0.1 only.
type DebugServer struct {
	gm   *GameManager
	gc   *gameController
	ln   net.Listener
	http *http.Server
	mu   sync.Mutex
}

func NewDebugServer(gm *GameManager, gc *gameController) *DebugServer {
	return &DebugServer{gm: gm, gc: gc}
}

func (ds *DebugServer) Start() error {
	addr := fmt.Sprintf("127.0.0.1:%d", ds.gm.debugPort)
	ln, err := net.Listen("tcp", addr)
	if err != nil {
		return err
	}
	ds.ln = ln
	ds.http = &http.Server{Handler: ds}
	go ds.http.Serve(ln)
	return nil
}

func (ds *DebugServer) Stop() {
	if ds.ln != nil {
		ds.ln.Close()
	}
}

func (ds *DebugServer) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Content-Type", "application/json")
	switch {
	case r.Method == http.MethodGet && r.URL.Path == "/status":
		w.WriteHeader(http.StatusOK)
		json.NewEncoder(w).Encode(ds.gm.Status())
	case r.Method == http.MethodPost && r.URL.Path == "/launch":
		replyCh := make(chan gameLaunchResult, 1)
		ds.gc.Launch(replyCh)
		var lr gameLaunchResult
		select {
		case lr = <-replyCh:
			if lr.ok {
				ds.writeJSON(w, 200, map[string]string{"msg": "launched", "pid": fmt.Sprintf("%d", lr.pid), "state": lr.state})
			} else {
				ds.writeJSON(w, 502, map[string]string{"error": lr.err})
			}
		case <-time.After(30 * time.Second):
			ds.writeJSON(w, 504, map[string]string{"error": "launch timed out"})
		}
	case r.Method == http.MethodPost && r.URL.Path == "/stop":
		ds.gc.StopCmd()
		ds.writeJSON(w, 200, map[string]string{"msg": "stop command sent to game controller"})
	case r.Method == http.MethodPost && r.URL.Path == "/restart":
		ds.gc.RestartCmd()
		ds.writeJSON(w, 200, map[string]string{"msg": "restart command sent to game controller"})
	default:
		w.WriteHeader(http.StatusNotFound)
		fmt.Fprintf(w, "endpoints: GET /status  POST /launch /stop /restart\n")
	}
}

func (ds *DebugServer) writeJSON(w http.ResponseWriter, code int, v any) {
	w.WriteHeader(code)
	json.NewEncoder(w).Encode(v)
}
