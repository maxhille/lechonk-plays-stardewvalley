using System;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace Lechonk;

/// <summary>Processes queued scripts on SMAPI's update tick.</summary>
public class CoroutineRunner
{
    private readonly ScriptController _controller;
    private readonly InputGuard _inputGuard;
    private readonly IMonitor _monitor;
    private int _tickCount;

    public CoroutineRunner(ScriptController controller, InputGuard inputGuard, IMonitor monitor)
    {
        _controller = controller;
        _inputGuard = inputGuard;
        _monitor = monitor;
    }

    /// <summary>Called every SMAPI update tick. Advances all active coroutines
    /// every tick — button holds re-press every game tick (SMAPI clears
    /// synthetic presses each tick), so skipping ticks would make held buttons
    /// stutter.</summary>
    public void OnTick(object? sender, UpdateTickedEventArgs e)
    {
        // Movement is keyboard-only (WASD / configured keybinds). The gamepad is
        // never enabled — gamepadControls stays at its save-default (false) and
        // nothing in the mod ever writes it, so WASD is always the live path and
        // there's no "Gamepad disconnect/reconnect" flapping. (An attempted write
        // into Game1.options during the save-load transition hard-crashed the
        // game, so the mod leaves it alone.)

        // Block the user's physical input while the bot is running. Runs
        // before AdvanceAll so the bot's own Press() calls win over the
        // suppression.
        // Do absolutely nothing when there's genuinely no work. Work only exists
        // when a script is running (needs its coroutines advanced) or the input
        // guard is locked (needs to re-apply suppression every tick). Otherwise
        // touching the input state / script dict at all was flakily crashing the
        // process — title screen, between scripts, and the post-load idle all hit
        // it. Zero work when idle is the fix.
        var anyScript = _controller.IsScriptActive();
        if (!anyScript && !_inputGuard.Locked)
            return;

        _inputGuard.Apply(anyScript);

        _tickCount++;
        if (_controller.IsScriptActive() && _tickCount % 20 == 0)
        {
            var active = _controller.IsScriptActive() ? "yes" : "no";
            _monitor.Log($"Runner: tick={_tickCount}, active scripts={active}", LogLevel.Trace);
        }
        _controller.AdvanceAll(1);
    }

    /// <summary>Called when any game button is pressed — used for dialog interrupt detection.</summary>
    public void OnButtonPressed(SButton button)
    {
        try
        {
            if (_controller.IsScriptActive())
            {
                var menu = Game1.activeClickableMenu;
                if (menu is DialogueBox)
                {
                    _controller.InterruptIfDialog();
                }
            }
        }
        catch (Exception ex)
        {
            _monitor.Log($"RUNNER: button={button} fault | {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
            _monitor.Log(ex.ToString(), LogLevel.Error);
        }
    }
}
