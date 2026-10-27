package main

import (
	"fmt"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"
)

const gameAPIPort = 17642

// gameState is the lifecycle state of the managed game process.
type gameState int

const (
	gameStateIdle gameState = iota
	gameStateStarting
	gameStateRunning
	gameStateStopping
)

// GameStatus is a snapshot of the managed game.
type GameStatus struct {
	Running bool
	PID     int
	Uptime  time.Duration
	PortUp  bool
	Error   string
}

// GameManager launches and stops the game as a child process. The game lives in
// the same cgroup / process tree as the harness, so stopping the harness (via
// systemd, or the process group) takes the game down with it.
type GameManager struct {
	mu        sync.Mutex
	state     gameState
	cmd       *exec.Cmd
	pid       int
	started   time.Time
	error     string
	gameDir   string
	debugPort int
	logPath   string
}

func NewGameManager(gameDir string, debugPort int) *GameManager {
	return &GameManager{gameDir: gameDir, debugPort: debugPort, state: gameStateIdle}
}

// defaultGameDir returns the default Stardew Valley folder.
func defaultGameDir() string {
	if d, ok := os.LookupEnv("SDV_GAME_DIR"); ok && d != "" {
		return d
	}
	home, _ := os.UserHomeDir()
	return filepath.Join(home, ".local/share/Steam/steamapps/common/Stardew Valley")
}

func stateName(s gameState) string {
	switch s {
	case gameStateIdle:
		return "idle"
	case gameStateStarting:
		return "starting"
	case gameStateRunning:
		return "running"
	case gameStateStopping:
		return "stopping"
	default:
		return "?"
	}
}

func (gm *GameManager) Start() error {
	gm.mu.Lock()
	defer gm.mu.Unlock()
	if gm.state == gameStateStarting || gm.state == gameStateRunning {
		return fmt.Errorf("game already %s", stateName(gm.state))
	}
	gm.error = ""
	gm.state = gameStateStarting
	gm.logPath = filepath.Join("/tmp", "lechonk-game.log")

	cmd, err := gm.buildCommand()
	if err != nil {
		gm.state = gameStateIdle
		return err
	}
	if err := cmd.Start(); err != nil {
		gm.state = gameStateIdle
		return fmt.Errorf("launch failed: %w", err)
	}
	gm.pid = cmd.Process.Pid
	gm.state = gameStateRunning
	gm.started = time.Now()
	return nil
}

func (gm *GameManager) buildCommand() (*exec.Cmd, error) {
	if gm.gameDir == "" {
		return nil, fmt.Errorf("no game dir")
	}
	if _, err := os.Stat(gm.gameDir); os.IsNotExist(err) {
		return nil, fmt.Errorf("game dir not found: %s", gm.gameDir)
	}
	launch := os.Getenv("SV_SMAPI_LAUNCH_CMD")
	if launch == "" {
		launch = "steam-run ./StardewValley"
	}
	cmd := exec.Command("bash", "-c", launch)
	cmd.Dir = gm.gameDir
	cmd.Env = gm.gameEnv()
	stdin, _ := os.Open("/dev/null")
	cmd.Stdin = stdin
	logf, _ := os.Create(gm.logPath)
	cmd.Stdout = logf
	cmd.Stderr = logf
	return cmd, nil
}

// gameEnv returns the environment for the game launcher: the inherited env with
// DISPLAY / XDG_RUNTIME_DIR defaulted and steam-run's dir on PATH.
func (gm *GameManager) gameEnv() []string {
	env := os.Environ()
	if _, ok := os.LookupEnv("DISPLAY"); !ok {
		env = append(env, "DISPLAY=:0")
	}
	if xdg, ok := os.LookupEnv("XDG_RUNTIME_DIR"); !ok {
		xdg = "/run/user/" + strconv.Itoa(os.Getuid())
		env = append(env, "XDG_RUNTIME_DIR="+xdg)
	}
	// Ensure steam-run is on PATH (it can live in a nix sw path not on the default).
	if sr := findSteamRun(); sr != "" {
		dir := filepath.Dir(sr)
		if dir != "" && dir != "." {
			preexisting := false
			for _, e := range env {
				if strings.HasPrefix(e, "PATH=") && strings.Contains(e, dir) {
					preexisting = true
				}
			}
			if !preexisting {
				var p string
				for _, e := range env {
					if strings.HasPrefix(e, "PATH=") {
						p = e[len("PATH="):]
					}
				}
				if p == "" {
					env = append(env, "PATH="+dir)
				} else {
					for i, e := range env {
						if strings.HasPrefix(e, "PATH=") {
							env[i] = "PATH=" + dir + ":" + p
							break
						}
					}
				}
			}
		}
	}
	return env
}

func (gm *GameManager) Status() GameStatus {
	gm.mu.Lock()
	defer gm.mu.Unlock()
	s := GameStatus{}
	if gm.state == gameStateRunning {
		s.Running = true
		s.PID = gm.pid
		s.Uptime = time.Since(gm.started)
		s.PortUp = gm.apiUp()
	}
	s.Error = gm.error
	return s
}

// apiUp reports whether the mod's HTTP server is listening.
func (gm *GameManager) apiUp() bool {
	c, err := net.DialTimeout("tcp",
		fmt.Sprintf("127.0.0.1:%d", gameAPIPort), 300*time.Millisecond)
	if err != nil {
		return false
	}
	c.Close()
	return true
}

// Stop kills the whole game process tree (the launcher and all its descendants,
// walked via /proc). The harness itself is not in that subtree, so it survives.
func (gm *GameManager) Stop() {
	gm.mu.Lock()
	defer gm.mu.Unlock()
	if gm.state != gameStateRunning {
		gm.error = ""
		return
	}
	gm.state = gameStateStopping

	pids := subtreeOf(gm.pid)
	for _, p := range pids {
		syscall.Kill(p, syscall.SIGTERM)
	}
	// Give the game a moment to unwind, then hard-kill anything still alive.
	time.Sleep(1 * time.Second)
	for _, p := range pids {
		if _, err := os.Stat("/proc/" + strconv.Itoa(p)); os.IsNotExist(err) {
			continue
		}
		syscall.Kill(p, syscall.SIGKILL)
	}
	gm.state = gameStateIdle
	gm.pid = 0
	gm.cmd = nil
	gm.error = ""
	gm.started = time.Time{}
}

// findSteamRun locates the steam-run wrapper.
func findSteamRun() string {
	for _, p := range []string{
		"/run/current-system/sw/bin/steam-run",
		"steam-run",
	} {
		if _, err := exec.LookPath(p); err == nil {
			return p
		}
	}
	return "steam-run"
}

// readPPid returns the parent pid of pid by reading /proc/<pid>/stat.
func readPPid(pid int) (int, error) {
	b, err := os.ReadFile("/proc/" + strconv.Itoa(pid) + "/stat")
	if err != nil {
		return 0, err
	}
	s := strings.TrimSpace(string(b))
	open := strings.Index(s, "(")
	close := strings.IndexRune(s, ')')
	if open < 0 || close < 0 {
		return 0, fmt.Errorf("no comm in stat of %d", pid)
	}
	fields := strings.Fields(s[close+1:])
	if len(fields) < 2 {
		return 0, fmt.Errorf("short stat for %d", pid)
	}
	ppid, _ := strconv.Atoi(fields[1])
	return ppid, nil
}

// subtreeOf returns the root and all its descendants (the whole process tree).
func subtreeOf(root int) []int {
	m := make(map[int]int)
	entries, _ := filepath.Glob("/proc/[0-9]*")
	for _, e := range entries {
		pid, err := strconv.Atoi(filepath.Base(e))
		if err != nil {
			continue
		}
		ppid, err := readPPid(pid)
		if err != nil {
			continue
		}
		m[pid] = ppid
	}
	out := []int{root}
	seen := map[int]bool{root: true}
	q := []int{root}
	for len(q) > 0 {
		cur := q[0]
		q = q[1:]
		for c, p := range m {
			if p == cur && !seen[c] {
				seen[c] = true
				q = append(q, c)
				out = append(out, c)
			}
		}
	}
	return out
}
