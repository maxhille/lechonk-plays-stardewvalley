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

    /// <summary>Called every SMAPI update tick. Advances all active coroutines every 4 ticks.</summary>
    public void OnTick(object? sender, UpdateTickedEventArgs e)
    {
        // Block the user's physical input while the bot is running. Runs
        // before AdvanceAll so the bot's own Press() calls win over the
        // suppression.
        _inputGuard.Apply(_controller.IsScriptActive());

        _tickCount++;
        if (_controller.IsScriptActive() && _tickCount % 20 == 0)
        {
            var active = _controller.IsScriptActive() ? "yes" : "no";
            _monitor.Log($"Runner: tick={_tickCount}, active scripts={active}", LogLevel.Trace);
        }
        if (e.IsMultipleOf(4))
        {
            _controller.AdvanceAll(1);
        }
    }

    /// <summary>Called when any game button is pressed — used for dialog interrupt detection.</summary>
    public void OnButtonPressed(SButton button)
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
}
