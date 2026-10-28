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
	"time"
)

// ModelClient is a thin OpenAI-compatible chat client.
//
// Config (env):
//
//	LECHONK_BASE_URL  e.g. http://saturn:9933/v1   (default)
//	LECHONK_MODEL     e.g. Qwen3.8-27B            (default)
//	LECHONK_API_KEY   optional; the local ninfer server needs none.
type ModelClient struct {
	baseURL string
	model   string
	apiKey  string
	http    *http.Client
}

func NewModelClient() *ModelClient {
	return &ModelClient{
		baseURL: envOr("LECHONK_BASE_URL", "http://saturn:9933/v1"),
		model:   envOr("LECHONK_MODEL", "Qwen3.8-27B"),
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
	Role       string     `json:"role"`
	Content    string     `json:"content,omitempty"`
	Reasoning  string     `json:"reasoning_content,omitempty"`
	ToolCalls  []ToolCall `json:"tool_calls,omitempty"`
	ToolCallID string     `json:"tool_call_id,omitempty"`
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
