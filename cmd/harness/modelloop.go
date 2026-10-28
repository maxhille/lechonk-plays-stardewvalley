package main

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

// thoughtEventKind tags one line the TUI transcript appends for the model loop.
type thoughtEventKind int

const (
	evStart   thoughtEventKind = iota // Le Chonk starting
	evStop                            // Le Chonk stopped / cancelled
	evInfo                            // harness notice (launching, waiting…)
	evThought                         // Le Chonk's plain-text thought
	evTool                            // Le Chonk called a tool
	evResult                          // a tool's result
	evError                           // something went wrong
	evDone                            // Le Chonk said DONE
)

// thoughtEvent is one line the TUI transcript and log record for the model loop.
type thoughtEvent struct {
	Kind thoughtEventKind // one of the ev* constants
	Turn int              // which loop iteration produced it (0 = boot)
	Text string
}

// modelState drives the Le Chonk thinking loop. It owns the context, the
// conversation, and dispatches tool calls to the game manager + mod API.
// Every event is sent to the TUI channel AND written to the transcript log
// (logPath), so you can tail it independent of the TUI.
type modelState struct {
	client  *ModelClient
	gm      *GameManager
	gc      *gameController
	apiPort int
	events  chan thoughtEvent
	http    *http.Client
	mu      sync.Mutex
	running atomic.Bool
	ctx     context.Context
	cancel  context.CancelFunc
	logPath string
	logFile *os.File
	ctxTokens atomic.Int64 // prompt tokens of the last model request
}

func newModelState(client *ModelClient, gm *GameManager, gc *gameController, events chan thoughtEvent) *modelState {
	return &modelState{
		client:  client,
		gm:      gm,
		gc:      gc,
		apiPort: gameAPIPort,
		events:  events,
		http:    &http.Client{Timeout: 10 * time.Minute}, // long: run_script blocks on the tick budget
		logPath: "/tmp/lechonk-transcript.log",
	}
}

// CtxTokens returns the prompt token count of the last model request (0 if unknown).
func (m *modelState) CtxTokens() int64 {
	return m.ctxTokens.Load()
}

func (m *modelState) Running() bool { return m.running.Load() }

// kindLabel is a short tag for the log + TUI line prefix.
func kindLabel(k thoughtEventKind) string {
	switch k {
	case evStart:
		return "boot"
	case evStop:
		return "stop"
	case evDone:
		return "done"
	case evInfo:
		return "harness"
	case evThought:
		return "thought"
	case evTool:
		return "tool"
	case evResult:
		return "result"
	case evError:
		return "error"
	default:
		return "?"
	}
}

func (m *modelState) emit(ev thoughtEvent) {
	m.mu.Lock()
	line := fmt.Sprintf("[%s][turn %d] %-8s %s\n",
		time.Now().Format("15:04:05"), ev.Turn, kindLabel(ev.Kind), ev.Text)
	if m.logFile == nil {
		if f, err := os.OpenFile(m.logPath, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0644); err == nil {
			m.logFile = f
		}
	}
	if m.logFile != nil {
		_, _ = m.logFile.WriteString(line)
	}
	m.mu.Unlock()
	select {
	case m.events <- ev:
	default:
		// TUI busy (shouldn't happen); drop rather than stall the model.
	}
}

func (m *modelState) emitText(kind thoughtEventKind, turn int, text string) {
	m.emit(thoughtEvent{Kind: kind, Turn: turn, Text: text})
}

// Start kicks off the thinking loop (idempotent; ignored if already running).
func (m *modelState) Start() {
	if !m.running.CompareAndSwap(false, true) {
		return
	}
	m.ctx, m.cancel = context.WithCancel(context.Background())
	m.emitText(evStart, 0, "Le Chonk is booting up…")
	go m.loop()
}

// Stop cancels the loop. The game child is reaped separately (the TUI's s/r/q
// handlers also call gm.Stop()).
func (m *modelState) Stop() {
	m.mu.Lock()
	if m.cancel != nil {
		m.cancel()
		m.cancel = nil
	}
	m.mu.Unlock()
}

// finished is deferred in the loop; it just marks the state idle. The loop
// itself is responsible for the final evDone / evStop event.
func (m *modelState) finished() {
	m.running.Store(false)
}

// loop is the think → tool → observe cycle, running in its own goroutine.
func (m *modelState) loop() {
	defer m.finished()
	defer func() {
		if r := recover(); r != nil {
			m.emitText(evError, 999, fmt.Sprintf("loop PANIC: %v", r))
		}
	}()
	messages := m.seedMessages()
	const maxTurns = 1000
	for turn := 0; turn < maxTurns; turn++ {
		if m.ctx.Err() != nil {
			m.emitText(evStop, turn, "run cancelled")
			return
		}

		resp, err := m.client.Chat(m.ctx, messages, m.tools())
		if err != nil {
			m.emitText(evError, turn, "model call: "+err.Error())
			messages = append(messages, ChatMessage{
				Role: "user",
				Content: "The model call failed: " + err.Error() +
					"\nTry a different, simpler action. (You can also say DONE to end the day.)",
			})
			select {
			case <-m.ctx.Done():
				m.emitText(evStop, turn, "run cancelled")
				return
			case <-time.After(2 * time.Second):
			}
			continue
		}
		msg := resp.Choices[0].Message
		m.ctxTokens.Store(int64(resp.Usage.Prompt))
		messages = append(messages, msg)

		// Show him as a thought. This model does its real thinking in a hidden
		// lane (reasoning_content); the visible content is often empty when it
		// acts. So emit the reasoning too — fall back to it when the visible
		// text is blank.
		visible := strings.TrimSpace(msg.Content)
		reasoning := strings.TrimSpace(msg.Reasoning)
		switch {
		case visible == "" && reasoning == "":
			// pure tool call, no words; nothing to show as a thought.
		case visible != "" && reasoning != "":
			m.emitText(evThought, turn, visible)
			m.emitText(evThought, turn, "  (thinking) "+truncateDisplay(reasoning, 500))
		case visible != "":
			m.emitText(evThought, turn, visible)
		default: // reasoning only
			m.emitText(evThought, turn, truncateDisplay(reasoning, 500))
		}
		if isDone(msg.Content) {
			m.emitText(evDone, turn, "Le Chonk says DONE — ending the day.")
			return
		}

		// No tool called: nudge to act so it can't loop on thoughts alone.
		if len(msg.ToolCalls) == 0 {
			messages = append(messages, ChatMessage{
				Role:    "user",
				Content: "Keep going. Take an action with a tool, or say DONE when the day is finished.",
			})
			continue
		}

		// Dispatch each tool call and feed the results back for the next turn.
		for _, tc := range msg.ToolCalls {
			m.emitText(evTool, turn, "→ "+tc.Function.Name+"("+tc.Function.Arguments+")")
			result := m.dispatch(tc.Function.Name, tc.Function.Arguments, turn)
			m.emitText(evResult, turn, truncateDisplay(result, 180))
			messages = append(messages, ChatMessage{
				Role:       "tool",
				ToolCallID: tc.ID,
				Content:    truncateModel(result, 8000),
			})
		}
	}
	m.emitText(evStop, maxTurns, "run stopped (turn budget reached)")
}

// seedMessages is the system prompt + the initial "start the game" user turn.
func (m *modelState) seedMessages() []ChatMessage {
	return []ChatMessage{
		{Role: "system", Content: systemPrompt},
		{Role: "user", Content: "Start the game now. First call launch_game to get the game up. It " +
			"will sit at the title screen — find your save slot with run_script: " +
			"return bot.listSaves(), load the one that belongs to you (it is named after " +
			"you), then wait for the world to be ready (bot.waitForWorldReady()), call " +
			"read_script_api to see your tools, and begin playing your day one action at " +
			"a time — first explore the house you wake up in (the bed, the TV, the door) " +
			"before you leave it. Say DONE when I tell you to stop."},
	}
}

func (m *modelState) tools() []Tool {
	return []Tool{
		{Type: "function", Function: ToolFunctionDef{
			Name: "read_script_api",
			Description: "Return the bot API schema — the list of bot.* game functions and how to call " +
				"them. Costs nothing; call it at the start of each day to orient yourself. No arguments.",
			Parameters: json.RawMessage(`{"type":"object","properties":{},"required":[]}`),
		}},
		{Type: "function", Function: ToolFunctionDef{
			Name: "launch_game",
			Description: "Start the game (a child process) and wait for the mod API. If no save is " +
				"loaded the game sits at the title screen — load the save afterwards with " +
				"run_script: return bot.loadGame(slot). Call this at the start of a run, or " +
				"whenever the game seems down. No arguments.",
			Parameters: json.RawMessage(`{"type":"object","properties":{},"required":[]}`),
		}},
		{Type: "function", Function: ToolFunctionDef{
			Name: "run_script",
			Description: "Run a Lua script against the bot API and get its result. It runs as a coroutine " +
				"over live game ticks and blocks until done. Pass the code in the lua field (max 1024 bytes).",
			Parameters: json.RawMessage(`{"type":"object","properties":{"lua":{"type":"string","description":"Lua code, e.g. bot.moveUp(10) or return bot.getTime() (max 1024 bytes)"}},"required":["lua"]}`),
		}},
	}
}

// dispatch maps a tool name to its effect and returns a string the model reads
// as the tool result. Errors are returned as text so the model can re-plan.
func (m *modelState) dispatch(name, args string, turn int) string {
	switch name {
	case "read_script_api":
		body, err := m.modGet("/api")
		if err != nil {
			return "ERROR: " + err.Error()
		}
		return body
	case "launch_game":
		return m.launchGame(args, turn)
	case "run_script":
		return m.runScript(args)
	default:
		return fmt.Sprintf("ERROR: unknown tool %q", name)
	}
}

func (m *modelState) runScript(args string) string {
	var a struct {
		Lua string `json:"lua"`
	}
	if args != "" {
		if err := json.Unmarshal([]byte(args), &a); err != nil {
			return fmt.Sprintf("ERROR: bad run_script args (%s): %v", args, err)
		}
	}
	if a.Lua == "" {
		return "ERROR: run_script needs a non-empty \"lua\" argument"
	}
	body, err := m.modScript(a.Lua)
	if err != nil {
		return "ERROR: " + err.Error()
	}
	return body
}

// launchGame gets the game + world up and reports state. It does NOT load a save
// itself; if the world is at the title screen, the model gets that as its result
// and can run loadGame via run_script.
func (m *modelState) launchGame(args string, turn int) string {
	// If the game is already up — whether the harness started it or an external
	// process did (mod API listening) — no need to start again.
	if m.gm.Status().Running || m.gm.APIUp() {
		m.emitText(evInfo, turn, "game already up (mod API listening)")
	} else if m.gc != nil {
		// Normal path: hand the launch to the game controller (the goroutine that
		// owns the process), and block for its result.
		m.emitText(evInfo, turn, "launching the game (asked the game controller)")
		replyCh := make(chan gameLaunchResult, 1)
		m.gc.Launch(replyCh)
		select {
		case r := <-replyCh:
			if !r.ok {
				m.emitText(evError, turn, "launch failed: "+r.err)
				return "ERROR starting the game: " + r.err
			}
		case <-m.ctx.Done():
			return "ERROR: run cancelled during launch"
		}
	} else {
		// Fallback (no controller, e.g. the headless looptest): drive the manager
		// directly. No event-pub here — the looptest has no TUI.
		m.emitText(evInfo, turn, "launching the game (direct)")
		if err := m.gm.Start(); err != nil {
			return "ERROR starting the game: " + err.Error()
		}
	}
	m.emitText(evInfo, turn, "waiting for the mod API to come up…")
	if !m.waitForAPI() {
		return "ERROR: game started but the mod API did not come up"
	}
	s := m.gm.Status()
	pid := s.PID
	if pid == 0 {
		pid = -1
	}
	return fmt.Sprintf("game up (pid %d), mod API ready. The game is at the title " +
		"screen — load your save with run_script: return bot.loadGame('<slot>'), " +
		"then wait for the world: return bot.waitForWorldReady().", pid)
}

func (m *modelState) waitForAPI() bool {
	for i := 0; i < 40; i++ {
		if m.gm.APIUp() {
			return true
		}
		select {
		case <-m.ctx.Done():
			return false
		case <-time.After(500 * time.Millisecond):
		}
	}
	return m.gm.APIUp()
}

// modScript POSTs raw Lua to the mod's /script endpoint and returns the body.
func (m *modelState) modScript(lua string) (string, error) {
	req, err := http.NewRequest("POST",
		fmt.Sprintf("http://127.0.0.1:%d/script", m.apiPort), bytes.NewBufferString(lua))
	if err != nil {
		return "", err
	}
	req.Header.Set("Content-Type", "application/lua")
	return m.doHTTP(req)
}

// modGet GETs a path on the mod's API and returns the body.
func (m *modelState) modGet(path string) (string, error) {
	req, err := http.NewRequest("GET",
		fmt.Sprintf("http://127.0.0.1:%d%s", m.apiPort, path), nil)
	if err != nil {
		return "", err
	}
	return m.doHTTP(req)
}

func (m *modelState) doHTTP(req *http.Request) (string, error) {
	resp, err := m.http.Do(req)
	if err != nil {
		return "", err
	}
	defer resp.Body.Close()
	body, _ := io.ReadAll(io.LimitReader(resp.Body, 4<<20))
	if resp.StatusCode >= 300 {
		return fmt.Sprintf("HTTP %d: %s", resp.StatusCode, strings.TrimSpace(string(body))), nil
	}
	return string(body), nil
}

func isDone(content string) bool {
	return strings.Contains(strings.ToUpper(strings.TrimSpace(content)), "DONE")
}

func truncateDisplay(s string, n int) string {
	if s == "" {
		return ""
	}
	s = strings.TrimSpace(strings.ReplaceAll(s, "\n", " "))
	if s == "" {
		return ""
	}
	if len(s) <= n {
		return s
	}
	return s[:n] + " …"
}

func truncateModel(s string, n int) string {
	if len(s) <= n {
		return s
	}
	return s[:n] + "\n[truncated]"
}

// OneTurn runs a single model call with the real three tools (no game side
// effects) and prints Le Chonk's first decision. Used with LECHONK_LOOPTEST=1.
func (m *modelState) OneTurn() {
	messages := m.seedMessages()
	resp, err := m.client.Chat(context.Background(), messages, m.tools())
	if err != nil {
		fmt.Printf("one-turn model call failed: %v\n", err)
		return
	}
	msg := resp.Choices[0].Message
	if reasoning := strings.TrimSpace(msg.Reasoning); reasoning != "" {
		fmt.Println("Le Chonk thinking:")
		fmt.Println("  " + reasoning)
	}
	if content := strings.TrimSpace(msg.Content); content != "" {
		fmt.Println("Le Chonk says:")
		fmt.Println("  " + content)
	}
	if len(msg.ToolCalls) == 0 {
		fmt.Println("(no tool call)")
	} else {
		for _, tc := range msg.ToolCalls {
			fmt.Printf("→ %s(%s)\n", tc.Function.Name, tc.Function.Arguments)
		}
	}
}

var systemPrompt = `
You are Le Chonk, an AI playing Stardew Valley by yourself. You act by calling
tools. There is no human at the controls; you make every decision.

How you act
Each turn you do one of:
- Think: reply with a short plain-text thought of what you are considering.
- Act: call a tool to do something in the game.
- Say DONE: only when you are told to stop. Replying DONE ends your run.

Prefer acting. If you only think and never act, you accomplish nothing, and the
harness will keep nudging you to act.

Tools
- read_script_api(): returns the bot API schema (the list of bot.* methods and
  how to call them). Costs nothing. CALL IT FIRST, at the start of each day, to
  orient yourself.
- launch_game(slot?): starts the game (a child process) and loads it to the world.
  Call it when a run begins, or whenever the game seems down. The save slot is
  optional.
- run_script(lua): runs a Lua script against the bot API, synchronously. It blocks
  until the script finishes (or hits the tick budget) and hands back:
  {"result","error","ticksUsed","evt"}. Keep the script under 1024 bytes.
    * evt "completed": the script did what it asked.
    * evt "stuck": it hit the tick budget — you are blocked or going too far.
      Re-plan: check state, take a smaller step, or move. Do not just retry.
    * evt "cancelled": the connection closed.
    * error non-empty: the script failed. Read the error, fix it, try again.

Lua (bot) basics — you see a bot table:
- bot.moveUp(ticks), bot.moveRight(ticks), bot.moveDown(ticks), bot.moveLeft(ticks)
  (movement stops automatically when a script ends; ~10-16 ticks per tile)
- bot.tap(name)   tap a gamepad button and wait one tick: A (action/select/confirm),
  X (use tool), B (close menu), Start (menu), Y (craft), Back (journal),
  LT/RT (cycle hotbar item), LB/RB (shift hotbar)
- bot.cursorUp/Down/Left/Right(ticks)  move the cursor (right stick); scrolls in menus
- bot.getTime(), bot.getDate()        the in-game clock / date
- bot.getPosition()                   where you are (x, y)
- bot.isTilePassable(x,y), bot.getTileObject(x,y)   probe the map — walls/buildings, then what's on the tile
- bot.listObjects()                   placed objects on the floor (chests, machines, items)
- bot.listTerrainFeatures()           trees, weeds, stones, hoe-dirt — hoe-dirt entries carry watered, and when planted: crop (name) + ready
- bot.listFurniture()                 furniture with footprints {name, x, y, w, h, isBed}
- bot.getWarps()                      doors/exits of this location {x, y, target, ...}
- bot.listNpcs(), bot.listAnimals()   who and what is in this location {name, x, y}
- bot.getEnergy()                     your stamina {current, max}
- bot.getInventory()                  what you are holding
- bot.getMenuType(), bot.getMenuButtons()   what menu/dialogue is open
  (getMenuButtons lists each button and its label; the label is what the user
  reads on a DialogueBox menu)
- bot.clickMenuButton(index), bot.advanceDialogue(),
  bot.selectDialogueOption(index), bot.closeDialogue()   menu navigation
- bot.listSaves(), bot.loadGame(slot), bot.exitToTitle()  saves
- bot.waitForWorldReady()             wait until the world is loaded

Write small, focused scripts — one action at a time. To learn something, observe
first (e.g. return bot.getTime(), bot.getPosition()), then act.

Lua pitfalls (the interpreter is MoonSharp, not standard Lua):
- Never put a number literal between two .. concatenators: x..17..y is a
  parse error. Write x..tostring(17)..y or x..'17'..y instead.

Navigation: plan your own movement. Move in short bursts (~15-30 ticks), then
re-check bot.getPosition(). If you fall short of your target, a tile ahead is
blocked — probe it with getTileObject (trees, weeds, stones, twigs and bushes
all block) and route around.

Rules
- The game is single-player on a gamepad. You drive it.
- If a run_script keeps coming back "stuck", the situation is wrong — change the
  plan (move, reload, close a menu) rather than retrying the same script.
- Keep thinking concise. Act.
- Watch your energy (bot.getEnergy()): when it runs low, eat a meal or go to
  bed — passing out in the middle of the day is a failure.
- Saying DONE ends the whole run — see the Goal below.

Goal: get the farm running
Turn the farm into a productive operation. Work toward it every day:
- clear weeds, stones and twigs; chop trees; dig up artifact spots
- till soil, plant seeds, and water your crops each morning
- walk to town (bus stop) to buy seeds and supplies at Pierre's shop and
  sell what you have gathered
- forage for seasonal plants; mine in the caves for ore
- when you have money, expand: buy animals and farm buildings
At night, go to bed — the game advances to the next day and you keep working.
A night of sleep is NOT DONE.

Your name is Le Chonk. launch_game brings the game up but it sits at the title
screen — the world is NOT ready yet. Once the game is up, find your save slot:
  return bot.listSaves()
Your save is the one named after you (Le Chonk). Load it:
  return bot.loadGame('<your save slot>')
then wait for the world to be ready:
  return bot.waitForWorldReady()
Only start playing once the world is ready.

Begin: launch the game, load your save, and read the API.
On your first day you wake up in your house. Explore it before you leave:
list the furniture (bot.listFurniture()),
check the floor for items (bot.listObjects()), and find the bed (that is where
you sleep), the TV, and the door to the farm.
Do not leave the house until you have done this. Then go outside and get to work.
`
