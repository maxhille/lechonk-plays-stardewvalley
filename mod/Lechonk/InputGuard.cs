using System;
using System.Linq;
using StardewModdingAPI;

namespace Lechonk;

/// <summary>Isolates the game's input from the user's physical input while the
/// bot is running. Suppressing a button forces it to "released" in the game's
/// eyes, so the user's keyboard/mouse/gamepad input can't interfere; the bot's
/// own Press() calls override the suppression per button (Press and Suppress
/// share the same override slot, and Press wins).</summary>
public class InputGuard
{
    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private SButton[]? _buttons;
    private bool _locked;
    private int _readyTicks;
    private bool _wasReady;

    public InputGuard(IModHelper helper, IMonitor monitor)
    {
        _helper = helper;
        _monitor = monitor;
    }

    /// <summary>Persistent lock: suppress user input until explicitly unlocked.
    /// The harness sets this for the whole run so input is blocked even between
    /// scripts (while the agent is thinking).</summary>
    public bool Locked
    {
        get => _locked;
        set
        {
            _locked = value;
            _monitor.Log($"Input guard: user input {(_locked ? "locked out" : "unlocked")}.", LogLevel.Info);
        }
    }

    /// <summary>Apply suppression for this tick. Must be called every game tick
    /// while active: SMAPI drops suppression for buttons that aren't physically
    /// held, so it has to be re-applied. Call before the bot's Press() calls so
    /// the bot's presses win.
    ///
    /// Only applied once the world is loaded: suppressing every button on the
    /// input state during the save-load / world-load transition hard-crashes the
    /// game, so stay quiet until a location is up.</summary>
    public void Apply(bool anyScriptActive)
    {
        try
        {
            if (!_locked && !anyScriptActive)
                return;

            // Stay out of the input state while the world is loading / not loaded
            // (title screen, save-load transition). Writing suppression into the
            // input state mid-load hard-kills the process. Even after the world
            // is ready, the render transition keeps running for a beat; only apply
            // after the world has been ready for a second. This is what was flakily
            // crashing the process.
            bool ready = Context.IsWorldReady;
            if (!ready)
            {
                _readyTicks = 0;
                _wasReady = false;
                return;
            }
            if (!_wasReady)
            {
                _readyTicks = 0;
                _wasReady = true;
            }
            _readyTicks++;
            if (_readyTicks < 60)
                return;

            _buttons ??= Enum.GetValues<SButton>().Where(b => b != SButton.None).ToArray();
            foreach (var button in _buttons)
                _helper.Input.Suppress(button);
            _helper.Input.SuppressScrollWheel();
        }
        catch (Exception ex)
        {
            _monitor.Log($"INPUTGUARD: Apply fault | {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
            _monitor.Log(ex.ToString(), LogLevel.Error);
        }
    }

    /// <summary>Check whether a button is currently suppressed (debug).</summary>
    public bool IsSuppressed(object? buttonName)
    {
        if (string.IsNullOrEmpty(buttonName?.ToString())
            || !Enum.TryParse<SButton>(buttonName.ToString(), true, out var button))
            return false;
        return _helper.Input.IsSuppressed(button);
    }
}
