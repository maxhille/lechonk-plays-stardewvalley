package main

import (
	"strings"
	"testing"
)

const samplePrefsXML = `<StartupPreferences>
  <clientOptions>
    <actionButton>
      <InputButton><key>X</key><mouseLeft>false</mouseLeft><mouseRight>false</mouseRight></InputButton>
      <InputButton><key>None</key><mouseLeft>false</mouseLeft><mouseRight>true</mouseRight></InputButton>
    </actionButton>
    <cancelButton>
      <InputButton><key>V</key><mouseLeft>false</mouseLeft><mouseRight>false</mouseRight></InputButton>
    </cancelButton>
    <menuButton>
      <InputButton><key>E</key><mouseLeft>false</mouseLeft><mouseRight>false</mouseRight></InputButton>
      <InputButton><key>Escape</key><mouseLeft>false</mouseLeft><mouseRight>false</mouseRight></InputButton>
    </menuButton>
    <emoteButton>
      <InputButton><key>Y</key><mouseLeft>false</mouseLeft><mouseRight>false</mouseRight></InputButton>
    </emoteButton>
    <inventorySlot1>
      <InputButton><key>D1</key><mouseLeft>false</mouseLeft><mouseRight>false</mouseRight></InputButton>
    </inventorySlot1>
    <inventorySlot12>
      <InputButton><key>OemPlus</key><mouseLeft>false</mouseLeft><mouseRight>false</mouseRight></InputButton>
    </inventorySlot12>
  </clientOptions>
</StartupPreferences>`

func TestRenderKeybinds(t *testing.T) {
	var prefs startupPreferences
	if err := unmarshalPrefs([]byte(samplePrefsXML), &prefs); err != nil {
		t.Fatalf("unmarshal: %v", err)
	}
	out := renderKeybinds(prefs.ClientOptions)

	for _, want := range []string{
		"- interact/action: X / MouseRight",
		"- cancel/close menu: V",
		"- pause menu: E / Escape",
		"- emote wheel: Y",
		"- hotbar slots 1-12: D1, OemPlus",
	} {
		if !strings.Contains(out, want) {
			t.Errorf("missing line %q in:\n%s", want, out)
		}
	}
	// Unbound actions must not leak empty values.
	if strings.Contains(out, "(unbound)") == false && !strings.Contains(out, "- run: (unbound)") {
		t.Errorf("expected run to be marked unbound:\n%s", out)
	}
}

func TestKeybindsBlockMissingFile(t *testing.T) {
	t.Setenv("XDG_CONFIG_HOME", "/nonexistent-lechonk-test")
	if b := keybindsBlock(); b != "" {
		t.Errorf("expected empty block for missing file, got:\n%s", b)
	}
}
