package main

import (
	"encoding/json"
	"strings"
	"testing"
)

// Mistral's reasoning models stream content as typed chunks, not a plain
// string. "jour chat!" lost its "Bon" to a silently-dropped array chunk —
// both shapes must decode.
func TestDecodeContentArrayForm(t *testing.T) {
	// plain-string form
	text, thinking := decodeContent(json.RawMessage(`"bonjour"`))
	if text != "bonjour" || thinking != "" {
		t.Errorf("string form: text=%q thinking=%q", text, thinking)
	}
	// typed-chunk form (as Mistral streams it)
	raw := `[{"type":"thinking","thinking":[{"text":"The user says "},{"text":"Bonjour"}],"closed":true},{"type":"text","text":"Bonjour chat! Time to farm."}]`
	text, thinking = decodeContent(json.RawMessage(raw))
	if text != "Bonjour chat! Time to farm." {
		t.Errorf("chunk form text=%q", text)
	}
	if thinking != "The user says Bonjour" {
		t.Errorf("chunk form thinking=%q", thinking)
	}
	// null / empty
	if text, _ = decodeContent(json.RawMessage(`null`)); text != "" {
		t.Errorf("null decoded to %q", text)
	}
}

// The ChatMessage unmarshaller feeds the full message through the same
// two-shape decode.
func TestChatMessageUnmarshalArrayContent(t *testing.T) {
	raw := `{"role":"assistant","content":[{"type":"thinking","thinking":[{"text":"hmm"}]},{"type":"text","text":"hello"}]}`
	var m ChatMessage
	if err := json.Unmarshal([]byte(raw), &m); err != nil {
		t.Fatal(err)
	}
	if m.Content != "hello" {
		t.Errorf("content=%q", m.Content)
	}
	if m.Reasoning != "hmm" {
		t.Errorf("reasoning=%q", m.Reasoning)
	}
}

// Requests must never carry reasoning_content — Mistral 422s on it
// (extra_forbidden) even though responses stream it. The recovered
// thinking lands in Reasoning when decoding; marshalling must strip it.
func TestMarshalStripsReasoning(t *testing.T) {
	m := ChatMessage{Role: "assistant", Content: "hello", Reasoning: "secret thoughts"}
	b, err := json.Marshal(m)
	if err != nil {
		t.Fatal(err)
	}
	s := string(b)
	if strings.Contains(s, "reasoning_content") || strings.Contains(s, "secret thoughts") {
		t.Errorf("request leaked reasoning: %s", s)
	}
	if !strings.Contains(s, `"content":"hello"`) {
		t.Errorf("content lost: %s", s)
	}
}
