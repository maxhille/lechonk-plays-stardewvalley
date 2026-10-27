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
	ln   net.Listener
	http *http.Server
	mu   sync.Mutex
}

func NewDebugServer(gm *GameManager) *DebugServer {
	return &DebugServer{gm: gm}
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
		if err := ds.gm.Start(); err != nil {
			ds.writeJSON(w, 502, map[string]string{"error": err.Error()})
		} else {
			ds.writeJSON(w, 200, map[string]string{"msg": "launched"})
		}
	case r.Method == http.MethodPost && r.URL.Path == "/stop":
		ds.gm.Stop()
		ds.writeJSON(w, 200, map[string]string{"msg": "stopped"})
	case r.Method == http.MethodPost && r.URL.Path == "/restart":
		ds.gm.Stop()
		time.Sleep(1 * time.Second)
		if err := ds.gm.Start(); err != nil {
			ds.writeJSON(w, 502, map[string]string{"error": err.Error()})
		} else {
			ds.writeJSON(w, 200, map[string]string{"msg": "relaunched"})
		}
	default:
		w.WriteHeader(http.StatusNotFound)
		fmt.Fprintf(w, "endpoints: GET /status  POST /launch /stop /restart\n")
	}
}

func (ds *DebugServer) writeJSON(w http.ResponseWriter, code int, v any) {
	w.WriteHeader(code)
	json.NewEncoder(w).Encode(v)
}
