package main

import (
	"encoding/xml"
	"fmt"
	"os"
	"strings"
)

// inputButton is one <InputButton> entry inside a keybind in the game's
// startup_preferences file.
type inputButton struct {
	Key        string `xml:"key"`
	MouseLeft  bool   `xml:"mouseLeft"`
	MouseRight bool   `xml:"mouseRight"`
}

type clientOptions struct {
	ActionButton   []inputButton `xml:"actionButton>InputButton"`
	CancelButton   []inputButton `xml:"cancelButton>InputButton"`
	UseToolButton  []inputButton `xml:"useToolButton>InputButton"`
	MoveUpButton   []inputButton `xml:"moveUpButton>InputButton"`
	MoveDownButton []inputButton `xml:"moveDownButton>InputButton"`
	MoveLeftButton []inputButton `xml:"moveLeftButton>InputButton"`
	MoveRightButton []inputButton `xml:"moveRightButton>InputButton"`
	MenuButton     []inputButton `xml:"menuButton>InputButton"`
	RunButton      []inputButton `xml:"runButton>InputButton"`
	ChatButton     []inputButton `xml:"chatButton>InputButton"`
	MapButton      []inputButton `xml:"mapButton>InputButton"`
	JournalButton  []inputButton `xml:"journalButton>InputButton"`
	EmoteButton    []inputButton `xml:"emoteButton>InputButton"`
	ToolbarSwap    []inputButton `xml:"toolbarSwap>InputButton"`
	InventorySlot1  []inputButton `xml:"inventorySlot1>InputButton"`
	InventorySlot2  []inputButton `xml:"inventorySlot2>InputButton"`
	InventorySlot3  []inputButton `xml:"inventorySlot3>InputButton"`
	InventorySlot4  []inputButton `xml:"inventorySlot4>InputButton"`
	InventorySlot5  []inputButton `xml:"inventorySlot5>InputButton"`
	InventorySlot6  []inputButton `xml:"inventorySlot6>InputButton"`
	InventorySlot7  []inputButton `xml:"inventorySlot7>InputButton"`
	InventorySlot8  []inputButton `xml:"inventorySlot8>InputButton"`
	InventorySlot9  []inputButton `xml:"inventorySlot9>InputButton"`
	InventorySlot10 []inputButton `xml:"inventorySlot10>InputButton"`
	InventorySlot11 []inputButton `xml:"inventorySlot11>InputButton"`
	InventorySlot12 []inputButton `xml:"inventorySlot12>InputButton"`
}

type startupPreferences struct {
	ClientOptions clientOptions `xml:"clientOptions"`
}

// stardewConfigPath returns the path of the game's startup_preferences file.
func stardewConfigPath() string {
	dir := os.Getenv("XDG_CONFIG_HOME")
	if dir == "" {
		home, _ := os.UserHomeDir()
		dir = home + "/.config"
	}
	return dir + "/StardewValley/startup_preferences"
}

// bindNames turns a list of input buttons into "X / MouseRight" style text.
func bindNames(list []inputButton) string {
	var parts []string
	for _, b := range list {
		if b.Key != "" && b.Key != "None" {
			parts = append(parts, b.Key)
		}
		if b.MouseLeft {
			parts = append(parts, "MouseLeft")
		}
		if b.MouseRight {
			parts = append(parts, "MouseRight")
		}
	}
	return strings.Join(parts, " / ")
}

// keybindsBlock reads the player's actual keybinds from the game config and
// renders them as a compact block for the system prompt. Empty string if the
// file is missing or unreadable.
func keybindsBlock() string {
	data, err := os.ReadFile(stardewConfigPath())
	if err != nil {
		return ""
	}
	var prefs startupPreferences
	if err := unmarshalPrefs(data, &prefs); err != nil {
		return ""
	}
	return renderKeybinds(prefs.ClientOptions)
}

func unmarshalPrefs(data []byte, prefs *startupPreferences) error {
	return xml.Unmarshal(data, prefs)
}

// renderKeybinds turns parsed client options into the prompt block.
func renderKeybinds(o clientOptions) string {
	slots := make([]string, 12)
	allSlots := [][]inputButton{
		o.InventorySlot1, o.InventorySlot2, o.InventorySlot3, o.InventorySlot4,
		o.InventorySlot5, o.InventorySlot6, o.InventorySlot7, o.InventorySlot8,
		o.InventorySlot9, o.InventorySlot10, o.InventorySlot11, o.InventorySlot12,
	}
	for i, s := range allSlots {
		if n := bindNames(s); n != "" {
			slots[i] = n
		}
	}

	var b strings.Builder
	b.WriteString(`

Keybinds (your game's ACTUAL controls — pass these names to bot.tap):
`)
	fmt.Fprintf(&b, "- interact/action: %s\n", orNA(o.ActionButton))
	fmt.Fprintf(&b, "- use tool: %s\n", orNA(o.UseToolButton))
	fmt.Fprintf(&b, "- cancel/close menu: %s\n", orNA(o.CancelButton))
	fmt.Fprintf(&b, "- pause menu: %s\n", orNA(o.MenuButton))
	fmt.Fprintf(&b, "- run: %s\n", orNA(o.RunButton))
	fmt.Fprintf(&b, "- chat: %s\n", orNA(o.ChatButton))
	fmt.Fprintf(&b, "- map: %s\n", orNA(o.MapButton))
	fmt.Fprintf(&b, "- journal: %s\n", orNA(o.JournalButton))
	fmt.Fprintf(&b, "- emote wheel: %s\n", orNA(o.EmoteButton))
	fmt.Fprintf(&b, "- swap hotbar: %s\n", orNA(o.ToolbarSwap))
	if s := strings.Join(filterEmpty(slots), ", "); s != "" {
		fmt.Fprintf(&b, "- hotbar slots 1-12: %s\n", s)
	}
	b.WriteString(`
Do NOT probe unknown button names: an unplanned press can open an overlay
(emote wheel, a menu) that eats your movement. If bot.getMenuType() reports
the emote wheel, close it by tapping the emote button again.`)
	return b.String()
}

func orNA(list []inputButton) string {
	if s := bindNames(list); s != "" {
		return s
	}
	return "(unbound)"
}

func filterEmpty(parts []string) []string {
	var out []string
	for _, p := range parts {
		if p != "" {
			out = append(out, p)
		}
	}
	return out
}

func init() {
	if kb := keybindsBlock(); kb != "" {
		systemPrompt += kb
	}
}
