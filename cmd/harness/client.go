package main

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"strings"
	"time"
)

// ModelClient is a thin OpenAI-compatible chat client.
//
// Config (env):
//
//	LECHONK_BASE_URL  e.g. https://api.mistral.ai/v1 (default)
//	LECHONK_MODEL     e.g. mistral-large-4          (default)
//	LECHONK_API_KEY   the key the endpoint requires.
type ModelClient struct {
	baseURL string
	model   string
	apiKey  string
	http    *http.Client
}

func NewModelClient() *ModelClient {
	return &ModelClient{
		baseURL: envOr("LECHONK_BASE_URL", "https://api.mistral.ai/v1"),
		model:   envOr("LECHONK_MODEL", "mistral-large-4"),
		apiKey:  os.Getenv("LECHONK_API_KEY"),
		http:    &http.Client{Timeout: 5 * time.Minute},
	}
}

func envOr(k, def string) string {
	if v := os.Getenv(k); v != "" {
		return v
	}
	return def
}

// ChatMessage is one entry in the OpenAI-compat messages array.
// Role is "system", "user", "assistant", or "tool".
// For the "tool" role, Content carries the result; ToolCallID matches the call.
// For the assistant role, ToolCalls (if any) is the list of tools it asked for.
type ChatMessage struct {
	Role       string
	Content    string
	Reasoning  string // responses only — NEVER serialised into requests (Mistral 422s)
	ToolCalls  []ToolCall
	ToolCallID string
}

// MarshalJSON serialises a message for a REQUEST: reasoning is stripped —
// Mistral rejects reasoning_content in request history (HTTP 422,
// extra_forbidden) even though it streams it in responses. UnmarshalJSON
// (the other direction) still decodes reasoning from replies.
func (m ChatMessage) MarshalJSON() ([]byte, error) {
	out := struct {
		Role       string     `json:"role"`
		Content    string     `json:"content,omitempty"`
		ToolCalls  []ToolCall `json:"tool_calls,omitempty"`
		ToolCallID string     `json:"tool_call_id,omitempty"`
	}{
		Role:       m.Role,
		Content:    m.Content,
		ToolCalls:  m.ToolCalls,
		ToolCallID: m.ToolCallID,
	}
	return json.Marshal(out)
}

// contentChunk is one typed piece of a content array. Mistral's reasoning
// models stream content as
// [{"type":"thinking","thinking":[{"text":…}]},{"type":"text","text":…}]
// rather than a plain string — both shapes must decode.
type contentChunk struct {
	Type     string `json:"type"`
	Text     string `json:"text"`
	Thinking []struct {
		Text string `json:"text"`
	} `json:"thinking"`
}

// decodeContent accepts content as a plain string or an array of typed
// chunks, and returns the visible text plus any thinking text.
func decodeContent(raw json.RawMessage) (text, thinking string) {
	raw = bytes.TrimSpace(raw)
	if len(raw) == 0 || bytes.Equal(raw, []byte("null")) {
		return "", ""
	}
	if raw[0] == '"' {
		var s string
		if err := json.Unmarshal(raw, &s); err == nil {
			return s, ""
		}
		return "", ""
	}
	var chunks []contentChunk
	if err := json.Unmarshal(raw, &chunks); err != nil {
		return "", ""
	}
	for _, c := range chunks {
		switch c.Type {
		case "text":
			text += c.Text
		case "thinking":
			for _, t := range c.Thinking {
				thinking += t.Text
			}
		}
	}
	return text, thinking
}

// UnmarshalJSON decodes a reply message whose content may be a string or an
// array of typed chunks (thinking + text). Marshalling is untouched: requests
// always send content as a plain string.
func (m *ChatMessage) UnmarshalJSON(data []byte) error {
	var aux struct {
		Role       string          `json:"role"`
		Content    json.RawMessage `json:"content"`
		Reasoning  string          `json:"reasoning_content"`
		ToolCalls  []ToolCall      `json:"tool_calls"`
		ToolCallID string          `json:"tool_call_id"`
	}
	if err := json.Unmarshal(data, &aux); err != nil {
		return err
	}
	m.Role = aux.Role
	m.Reasoning = aux.Reasoning
	m.ToolCalls = aux.ToolCalls
	m.ToolCallID = aux.ToolCallID
	text, thinking := decodeContent(aux.Content)
	m.Content = text
	if thinking != "" {
		m.Reasoning = thinking
	}
	return nil
}

// ToolCall is a tool the assistant asked to run (in a reply) or echoed back
// (when the caller sends the next turn). Arguments is a JSON string.
type ToolCall struct {
	ID       string       `json:"id"`
	Type     string       `json:"type"`
	Function ToolCallFunc `json:"function"`
}

type ToolCallFunc struct {
	Name      string `json:"name"`
	Arguments string `json:"arguments"` // JSON string; "" for no args
}

// Tool is a tool definition in the request's tools array.
type Tool struct {
	Type     string          `json:"type"` // "function"
	Function ToolFunctionDef `json:"function"`
}

type ToolFunctionDef struct {
	Name        string          `json:"name"`
	Description string          `json:"description"`
	Parameters  json.RawMessage `json:"parameters"` // JSON Schema for the args
}

// ChatResponse is a non-streaming OpenAI-compat chat response.
type ChatResponse struct {
	ID      string   `json:"id"`
	Choices []Choice `json:"choices"`
	Usage   Usage    `json:"usage,omitempty"`
}

type Choice struct {
	Message      ChatMessage `json:"message"`
	FinishReason string      `json:"finish_reason"`
}

type Usage struct {
	Prompt     int `json:"prompt_tokens"`
	Completion int `json:"completion_tokens"`
	Total      int `json:"total_tokens"`
}

// promptCacheKey is sent with every request so an endpoint that supports
// prompt caching (e.g. Mistral, where cached tokens bill at 10%) can cache
// Le Chonk's shared conversation prefix. Endpoints that don't know the
// field (like the local server) ignore it.
const promptCacheKey = "lechonk-plays-stardewvalley"

// streamDeltaKind labels what a streaming delta carries.
type streamDeltaKind string

const (
	deltaThink streamDeltaKind = "reasoning" // hidden reasoning_content
	deltaSay   streamDeltaKind = "content"   // visible words
	deltaTool  streamDeltaKind = "tool"      // a tool call starts
)

// ChatStream is one streaming completion (SSE). It accumulates the deltas
// into the same shape Chat returns, reporting progress through onDelta as it
// arrives — the kind says whether the fragment is hidden reasoning, visible
// words, or the start of a tool call. Pass nil to ignore progress. The
// response is returned when the stream ends.
func (c *ModelClient) ChatStream(ctx context.Context, messages []ChatMessage, tools []Tool,
	onDelta func(kind streamDeltaKind, text string)) (*ChatResponse, error) {

	req := struct {
		Model      string        `json:"model"`
		Messages   []ChatMessage `json:"messages"`
		Tools      []Tool        `json:"tools,omitempty"`
		Stream     bool          `json:"stream"`
		StreamOpts struct {
			IncludeUsage bool `json:"include_usage"`
		} `json:"stream_options"`
		PromptCacheKey string `json:"prompt_cache_key,omitempty"`
	}{
		Model:          c.model,
		Messages:       messages,
		Tools:          tools,
		Stream:         true,
		PromptCacheKey: promptCacheKey,
	}
	req.StreamOpts.IncludeUsage = true // usage arrives in the final chunk

	body, err := json.Marshal(req)
	if err != nil {
		return nil, fmt.Errorf("marshal request: %w", err)
	}
	h, err := http.NewRequestWithContext(ctx, http.MethodPost, c.baseURL+"/chat/completions", bytes.NewReader(body))
	if err != nil {
		return nil, err
	}
	h.Header.Set("Content-Type", "application/json")
	h.Header.Set("Accept", "text/event-stream")
	if c.apiKey != "" {
		h.Header.Set("Authorization", "Bearer "+c.apiKey)
	}
	resp, err := c.http.Do(h)
	if err != nil {
		return nil, fmt.Errorf("http: %w", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode >= 300 {
		data, _ := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
		return nil, fmt.Errorf("chat stream: http %d: %s", resp.StatusCode, strings.TrimSpace(string(data)))
	}

	var out ChatResponse
	var msg *ChatMessage             // lazily-allocated accumulated reply
	toolCalls := map[int]*ToolCall{} // delta index -> accumulated call
	scan := bufio.NewScanner(resp.Body)
	scan.Buffer(make([]byte, 0, 64*1024), 4<<20)
	for scan.Scan() {
		line := strings.TrimSpace(scan.Text())
		if line == "" || strings.HasPrefix(line, ":") {
			continue // keep-alive comment
		}
		if !strings.HasPrefix(line, "data:") {
			continue
		}
		payload := strings.TrimSpace(strings.TrimPrefix(line, "data:"))
		if payload == "[DONE]" {
			break
		}
		var chunk struct {
			Choices []struct {
				Delta struct {
					Content   json.RawMessage `json:"content"`
					Reasoning string          `json:"reasoning_content"`
					ToolCalls []struct {
						Index    int    `json:"index"`
						ID       string `json:"id"`
						Function struct {
							Name      string `json:"name"`
							Arguments string `json:"arguments"`
						} `json:"function"`
					} `json:"tool_calls"`
				} `json:"delta"`
			} `json:"choices"`
			Usage *Usage `json:"usage"`
		}
		if err := json.Unmarshal([]byte(payload), &chunk); err != nil {
			continue // tolerate a malformed keep-alive chunk
		}
		if chunk.Usage != nil {
			out.Usage = *chunk.Usage
		}
		if len(chunk.Choices) == 0 {
			continue
		}
		d := chunk.Choices[0].Delta
		if msg == nil {
			msg = &ChatMessage{Role: "assistant"}
		}
		if d.Reasoning != "" {
			msg.Reasoning += d.Reasoning
			if onDelta != nil {
				onDelta(deltaThink, d.Reasoning)
			}
		}
		// Content arrives either as a plain string or as typed chunks
		// (thinking + text) — Mistral's reasoning models use the array form.
		text, thinking := decodeContent(d.Content)
		if thinking != "" {
			msg.Reasoning += thinking
			if onDelta != nil {
				onDelta(deltaThink, thinking)
			}
		}
		if text != "" {
			msg.Content += text
			if onDelta != nil {
				onDelta(deltaSay, text)
			}
		}
		for _, tc := range d.ToolCalls {
			acc := toolCalls[tc.Index]
			if acc == nil {
				// First fragment for this call carries the ID and name.
				msg.ToolCalls = append(msg.ToolCalls, ToolCall{
					ID:       tc.ID,
					Type:     "function",
					Function: ToolCallFunc{Name: tc.Function.Name},
				})
				toolCalls[tc.Index] = &msg.ToolCalls[len(msg.ToolCalls)-1]
				acc = toolCalls[tc.Index]
				if onDelta != nil && acc.Function.Name != "" {
					onDelta(deltaTool, acc.Function.Name)
				}
			} else {
				// A later fragment re-sending ID/name (some servers do): keep it.
				if tc.ID != "" {
					acc.ID = tc.ID
				}
				if tc.Function.Name != "" {
					acc.Function.Name = tc.Function.Name
				}
			}
			acc.Function.Arguments += tc.Function.Arguments
		}
	}
	if err := scan.Err(); err != nil {
		return nil, fmt.Errorf("read stream: %w", err)
	}
	if msg == nil {
		return nil, fmt.Errorf("no choices in stream")
	}
	out.Choices = []Choice{{Message: *msg}}
	return &out, nil
}

// Chat is one non-streaming completion.
func (c *ModelClient) Chat(ctx context.Context, messages []ChatMessage, tools []Tool) (*ChatResponse, error) {
	req := struct {
		Model          string        `json:"model"`
		Messages       []ChatMessage `json:"messages"`
		Tools          []Tool        `json:"tools,omitempty"`
		Stream         bool          `json:"stream"`
		PromptCacheKey string        `json:"prompt_cache_key,omitempty"`
	}{
		Model:          c.model,
		Messages:       messages,
		Tools:          tools,
		Stream:         false,
		PromptCacheKey: promptCacheKey,
	}
	body, err := json.Marshal(req)
	if err != nil {
		return nil, fmt.Errorf("marshal request: %w", err)
	}
	h, err := http.NewRequestWithContext(ctx, http.MethodPost, c.baseURL+"/chat/completions", bytes.NewReader(body))
	if err != nil {
		return nil, err
	}
	h.Header.Set("Content-Type", "application/json")
	if c.apiKey != "" {
		h.Header.Set("Authorization", "Bearer "+c.apiKey)
	}
	resp, err := c.http.Do(h)
	if err != nil {
		return nil, fmt.Errorf("http: %w", err)
	}
	defer resp.Body.Close()
	data, err := io.ReadAll(io.LimitReader(resp.Body, 10<<20))
	if err != nil {
		return nil, fmt.Errorf("read response: %w", err)
	}
	if resp.StatusCode >= 300 {
		return nil, fmt.Errorf("chat: http %d: %s", resp.StatusCode, strings.TrimSpace(string(data)))
	}
	var out ChatResponse
	if err := json.Unmarshal(data, &out); err != nil {
		return nil, fmt.Errorf("decode response: %w (body: %.200s)", err, data)
	}
	if len(out.Choices) == 0 {
		return nil, fmt.Errorf("no choices in response")
	}
	return &out, nil
}

// SelfTest pings the endpoint once: a system prompt, a user turn, and one tool.
// It prints the reply (content and any tool calls) so a human can see the
// model talking to the live server. Trigger from main with LECHONK_MODELTEST=1.
func (c *ModelClient) SelfTest() error {
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Minute)
	defer cancel()
	messages := []ChatMessage{
		{Role: "system", Content: "You are Le Chonk, an AI playing Stardew Valley, alone on the farm. " +
			"On each turn you either reply with a short thought, or call a tool. " +
			"If no tool fits the situation, reply in plain text."},
		{Role: "user", Content: "Morning on the farm. What do you want to do first?"},
	}
	tools := []Tool{{
		Type: "function",
		Function: ToolFunctionDef{
			Name:        "get_time",
			Description: "Return the current in-game time, day, and season.",
			Parameters:  json.RawMessage(`{"type":"object","properties":{},"required":[]}`),
		},
	}}
	resp, err := c.Chat(ctx, messages, tools)
	if err != nil {
		return err
	}
	m := resp.Choices[0].Message
	fmt.Printf("model: %s\n", c.model)
	if m.Content != "" {
		fmt.Println("content:", m.Content)
	}
	if len(m.ToolCalls) == 0 {
		fmt.Println("no tool call (plain reply)")
	} else {
		for _, tc := range m.ToolCalls {
			fmt.Printf("tool_call %s -> %s(%s)\n", tc.ID, tc.Function.Name, tc.Function.Arguments)
		}
	}
	if resp.Usage.Prompt+resp.Usage.Completion > 0 {
		fmt.Printf("usage: prompt=%d completion=%d\n", resp.Usage.Prompt, resp.Usage.Completion)
	}
	return nil
}
