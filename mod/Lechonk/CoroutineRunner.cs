using System;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace Lechonk;

/// <summary>Advances the active script on SMAPI's update tick. Stateless: one
/// script at a time, no registry, no "all active scripts" loop.</summary>
public class CoroutineRunner
{
    private readonly ScriptController _controller;
    private readonly IMonitor _monitor;

    public CoroutineRunner(ScriptController controller, IMonitor monitor)
    {
        _controller = controller;
        _monitor = monitor;
    }

    /// <summary>Runs every SMAPI update tick on the game loop thread: drains queued
    /// button taps, then advances the active script (if any). Movement uses
    /// left-stick gamepad buttons; SMAPI clears synthetic presses each tick, so
    /// skipping ticks would make held buttons stutter. <c>RunScriptBlocking</c>
    /// (on the HTTP thread) blocks on the TaskCompletionSource that
    /// <c>Advance</c> completes when the coroutine dies.</summary>
    public void OnTick(object? sender, UpdateTickedEventArgs e)
    {
        _controller.Advance();
    }

    /// <summary>Button-pressed hook (reserved). Dialogs are handled explicitly by
    /// the agent via getMenuType() + dialogue methods; this hook is a no-op.</summary>
    public void OnButtonPressed(SButton button)
    {
    }
}
