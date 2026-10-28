package main

// The game runs in gamepad mode and the controller is the sole input source:
// no keyboard, no mouse, anywhere. Controller buttons cannot be reassigned, so
// the vocabulary is fixed — no need to read the player's keyboard keybinds.
const gamepadBlock = `

Gamepad controls (you play on a gamepad — bot.tap takes these names):
- A      action / select / confirm (interact, use cursor, advance or confirm dialogue)
- X      use tool
- B      close/exit a menu
- Start  open the menu
- Y      crafting menu
- Back   journal
- LT/RT  cycle the hotbar item
- LB/RB  shift the hotbar
- left stick   move: bot.moveUp/Down/Left/Right(ticks)
- right stick  cursor: bot.cursorUp/Down/Left/Right(ticks) — also scrolls in menus.

Keep cursor moves short: holding the right stick opens the emote wheel. If
bot.getMenuType() reports the emote wheel, dismiss it before anything else.
Never probe unknown button names — an unplanned press can open an overlay
that eats your movement.
`

func init() {
	systemPrompt += gamepadBlock
}
