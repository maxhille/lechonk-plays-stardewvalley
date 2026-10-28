package main

import "testing"

// The run must only end when the reply IS the word DONE. A substring match
// killed a run mid-plan: the agent narrated "TV done — tomorrow will be
// partly cloudy…" and the harness read DONE out of it. Status narration
// inside a longer reply is not the quit signal.
func TestIsDone(t *testing.T) {
	cases := []struct {
		in   string
		want bool
	}{
		{"DONE", true},
		{"done", true},
		{"Done.", true},
		{"DONE.", true},
		{"  DONE  ", true},
		{"DONE!", true},
		{"TV done — tomorrow will be partly cloudy", false},
		{"House exploration done. Now heading outside to the farm.", false},
		{"The chest is done", false},
		{"I am done with the TV for today", false},
		{"Nothing left to do today", false},
		{"", false},
		{"bedtime — going to sleep now", false},
	}
	for _, c := range cases {
		if got := isDone(c.in); got != c.want {
			t.Errorf("isDone(%q) = %v, want %v", c.in, got, c.want)
		}
	}
}
