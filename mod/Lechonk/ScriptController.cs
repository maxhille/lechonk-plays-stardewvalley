using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using StardewModdingAPI;

namespace Lechonk;

/// <summary>Manages script lifecycle: queue, tick budget, cancel, dialog interrupt.</summary>
public class ScriptController
{
    private readonly BotApi _botApi;
    private readonly IMonitor _monitor;

    private int _nextId = 1;
    private readonly ConcurrentDictionary<int, ScriptState> _activeScripts = new();

    private class ScriptState
    {
        public MoonSharp.Interpreter.Script Script { get; }
        public MoonSharp.Interpreter.Coroutine Coro { get; }
        public int TicksUsed { get; set; }
        public int TickBudget { get; }
        public CancellationTokenSource Cts { get; }
        public bool Interrupted { get; set; }

        public ScriptState(MoonSharp.Interpreter.Script script, MoonSharp.Interpreter.Coroutine coro, int tickBudget)
        {
            Script = script;
            Coro = coro;
            TickBudget = tickBudget;
            Cts = new CancellationTokenSource();
        }
    }

    public ScriptController(BotApi botApi, IMonitor monitor)
    {
        _botApi = botApi;
        _monitor = monitor;
    }

    /// <summary>Queue a script for execution. Returns a script ID immediately (non-blocking).</summary>
    public int RunScript(string luaCode, int tickBudget = 2000)
    {
        var id = _nextId++;
        var script = new MoonSharp.Interpreter.Script();

        // Expose the Bot API as a global table using Table + Func delegates
        var botTable = new MoonSharp.Interpreter.Table(script);

        // Yielding calls return the Yield marker; the Lua prelude below wraps
        // them so the marker becomes coroutine.yield() and the script ticks
        // with the game.
        botTable["print"] = (Func<object?, object?>)(msg => { _botApi.Print(msg?.ToString()); return null; });
        botTable["pressButtonTick"] = (Func<object?, object?>)(b => _botApi.PressButtonTick(b));
        botTable["tapButton"] = (Func<object?, object?>)(b => _botApi.TapButton(b));
        botTable["getKeybinds"] = (Func<object?>)(() => _botApi.GetKeybinds());
        botTable["setInputLock"] = (Func<object?, object?>)(e => _botApi.SetInputLock(e));
        botTable["isInputLocked"] = (Func<object?>)(() => _botApi.IsInputLocked());
        botTable["isSuppressed"] = (Func<object?, object?>)(b => _botApi.IsSuppressed(b));
        botTable["stop"] = (Func<object?, object?>)(_ => { _botApi.Stop(); return null; });
        botTable["wait"] = (Func<object?, object?>)(t => _botApi.Wait(t));
        botTable["getMapSchema"] = (Func<object?>)(() => _botApi.GetMapSchema());
        botTable["isWalkable"] = (Func<object?, object?, object?>)( (x, y) => _botApi.IsWalkable(x, y) );
        botTable["getTileObject"] = (Func<object?, object?, object?>)( (x, y) => _botApi.GetTileObject(x, y) );
        botTable["getInventory"] = (Func<object?>)(() => _botApi.GetInventory());
        botTable["getTime"] = (Func<object?>)(() => _botApi.GetTime());
        botTable["getDate"] = (Func<object?>)(() => _botApi.GetDate());
        botTable["listSaves"] = (Func<object?>)(() => _botApi.ListSaves());
        botTable["loadGame"] = (Func<object?, object?>)(slotName => _botApi.LoadGame(slotName));
        botTable["exitToTitle"] = (Func<object?, object?>)(_ => _botApi.ExitToTitle());
        botTable["isWorldReady"] = (Func<object?>)(() => _botApi.IsWorldReady());
        botTable["waitForWorldReadyTick"] = (Func<object?, object?>)(_ => _botApi.WaitForWorldReady());
        botTable["wakeUp"] = (Func<object?, object?>)(_ => { _botApi.WakeUp(); return null; });
        botTable["listFurniture"] = (Func<object?>)(() => _botApi.ListFurniture());
        botTable["getMovementState"] = (Func<object?>)(() => _botApi.GetMovementState());
        botTable["getMenuType"] = (Func<object?>)(() => _botApi.GetMenuType());
        botTable["getMenuButtons"] = (Func<object?>)(() => _botApi.GetMenuButtons());
        botTable["clickMenuButton"] = (Func<object?, object?>)(idx => { _botApi.ClickMenuButton(idx); return null; });
        botTable["advanceDialogue"] = (Func<object?, object?>)(_ => { _botApi.AdvanceDialogue(); return null; });
        botTable["selectDialogueOption"] = (Func<object?, object?>)(idx => { _botApi.SelectDialogueOption(idx); return null; });
        botTable["closeDialogue"] = (Func<object?, object?>)(_ => { _botApi.CloseDialogue(); return null; });

        script.Globals["bot"] = botTable;

        // Lua prelude: implement the tick-based API on top of the raw
        // per-tick primitives. `Y` is the yield marker returned by C# calls;
        // it is never passed through to scripts.
        const string prelude = @"
            local Y = '__lechonk_yield__'

            -- bot.pressButton(name, ticks): hold a button continuously for
            -- `ticks` game ticks. SMAPI's Press lasts exactly one input tick
            -- (it clears synthetic presses every tick), so the loop re-presses
            -- every tick — the game sees the button held down continuously,
            -- like a player keeping the key pressed. After the last tick the
            -- button auto-releases; there is nothing to release explicitly.
            function bot.pressButton(name, ticks)
                ticks = ticks or 1
                local ret = bot.pressButtonTick(name)
                if ret ~= Y then return ret end
                for i = 1, ticks do
                    coroutine.yield()
                    if i < ticks then bot.pressButtonTick(name) end
                end
            end

            -- bot.tap(name): tap a button, then wait one tick so the game
            -- processes the press.
            function bot.tap(name)
                local ret = bot.tapButton(name)
                if ret ~= nil then return ret end
                coroutine.yield()
            end

            -- bot.move(dir, ticks): hold a movement direction (0=down, 1=left,
            -- 2=up, 3=right) for `ticks` input ticks. Uses the game's configured
            -- movement keybinds (WASD by default) — DPad/gamepad input is only
            -- read in gamepad mode, which the game auto-disables without real
            -- hardware, so the keybinds are the reliable input path.
            -- Directional movement without the agent needing to know key
            -- names: bot.moveLeft/Right/Up/Down(ticks) hold the game's
            -- configured movement key for `ticks` ticks. (bot.move(dir,
            -- ticks) with dir 0=down, 1=left, 2=up, 3=right also works.)
            local DIRKEY = nil
            local function dirKey(dir)
                if not DIRKEY then
                    local kb = bot.getKeybinds()
                    DIRKEY = { [0] = kb.moveDown, [1] = kb.moveLeft, [2] = kb.moveUp, [3] = kb.moveRight }
                end
                return DIRKEY[dir]
            end
            function bot.move(dir, ticks)
                local b = dirKey(dir)
                if not b then return 'error: dir must be 0-3' end
                return bot.pressButton(b, ticks)
            end
            function bot.moveLeft(ticks)  return bot.move(1, ticks) end
            function bot.moveRight(ticks) return bot.move(3, ticks) end
            function bot.moveUp(ticks)    return bot.move(2, ticks) end
            function bot.moveDown(ticks)  return bot.move(0, ticks) end

            -- bot.wait(ticks): yield once per tick for `ticks` ticks.
            function bot.wait(ticks)
                ticks = ticks or 1
                for i = 1, ticks do
                    coroutine.yield()
                end
            end

            -- bot.waitForWorldReady(): block until the world is loaded and
            -- ready, yielding once per tick. Returns true when ready. Call
            -- after bot.loadGame() so the script ends the moment the world
            -- is up.
            function bot.waitForWorldReady()
                while true do
                    local ret = bot.waitForWorldReadyTick()
                    if ret ~= Y then return ret end
                    coroutine.yield()
                end
            end

        ";
        script.DoString(prelude);

        // Wrap the user code in a closure and run it as a coroutine. Never
        // DoString the raw code: that would execute the body immediately on
        // the HTTP thread, outside a coroutine, where yielding is an error
        // (and every script would run twice).
        MoonSharp.Interpreter.Coroutine coro;
        {
            var wrappedCode = $"return function() {luaCode} end";
            MoonSharp.Interpreter.DynValue wrappedResult;
            try
            {
                wrappedResult = script.DoString(wrappedCode);
            }
            catch (Exception ex)
            {
                _monitor.Log($"Script {id}: failed to compile: {ex.Message}", LogLevel.Error);
                return -1;
            }
            if (wrappedResult.Type != MoonSharp.Interpreter.DataType.Function)
            {
                _monitor.Log($"Script {id}: failed to compile (got {wrappedResult.Type})", LogLevel.Error);
                return -1;
            }
            var coroValue = script.CreateCoroutine(wrappedResult);
            coro = coroValue.Coroutine;
        }

        if (coro == null)
        {
            _monitor.Log($"Script {id}: failed to create coroutine", LogLevel.Error);
            return -1;
        }

        _activeScripts[id] = new ScriptState(script, coro, tickBudget);
        _monitor.Log($"Script {id}: queued (budget={tickBudget} ticks)", LogLevel.Trace);
        return id;
    }

    /// <summary>Advance all active coroutines by the given number of ticks.</summary>
    public void AdvanceAll(int ticks)
    {
        foreach (var kvp in _activeScripts.ToArray())
        {
            var id = kvp.Key;
            var state = kvp.Value;

            if (state.Interrupted)
            {
                _monitor.Log($"Script {id}: interrupted by dialog, removing", LogLevel.Trace);
                _botApi.RecordResult(id, "", null, "dialog_interrupted", state.TicksUsed, ScriptEvent.DialogInterrupted);
                _activeScripts.TryRemove(id, out _);
                continue;
            }

            if (state.Cts.IsCancellationRequested)
            {
                _monitor.Log($"Script {id}: cancelled, removing", LogLevel.Trace);
                _botApi.RecordResult(id, "", null, "cancelled", state.TicksUsed, ScriptEvent.Cancelled);
                _activeScripts.TryRemove(id, out _);
                continue;
            }

            MoonSharp.Interpreter.DynValue result;
            try
            {
                result = state.Coro.Resume();
            }
            catch (Exception ex)
            {
                // Script threw (runtime error). Record and remove — a dead
                // coroutine must never be resumed again.
                _monitor.Log($"Script {id}: runtime error: {ex.Message}", LogLevel.Error);
                _botApi.RecordResult(id, "", null, $"script_error: {ex.Message}", state.TicksUsed, ScriptEvent.Cancelled);
                _activeScripts.TryRemove(id, out _);
                continue;
            }
            state.TicksUsed += ticks;

            if (state.TicksUsed >= state.TickBudget)
            {
                _monitor.Log($"Script {id}: tick budget exceeded ({state.TicksUsed}/{state.TickBudget})", LogLevel.Warn);
                _botApi.RecordResult(id, "", null, "stuck", state.TicksUsed, ScriptEvent.Stuck);
                _activeScripts.TryRemove(id, out _);
                continue;
            }

            // A yielded (suspended) coroutine resumes on a later tick;
            // only a Dead coroutine is finished.
            if (state.Coro.State != MoonSharp.Interpreter.CoroutineState.Dead)
            {
                continue;
            }

            var retValue = result.ToObject();
            _botApi.RecordResult(id, "", retValue, null, state.TicksUsed, ScriptEvent.Completed);
            _activeScripts.TryRemove(id, out _);
        }
    }

    /// <summary>Check if any script is currently active.</summary>
    public bool IsScriptActive() => _activeScripts.Count > 0;

    /// <summary>Cancel the active script (called when HTTP connection closes).</summary>
    public void CancelActiveScript()
    {
        foreach (var state in _activeScripts.Values)
        {
            state.Cts.Cancel();
        }
    }

    /// <summary>Mark the active script as interrupted by a dialog.</summary>
    public void InterruptIfDialog()
    {
        foreach (var kvp in _activeScripts)
        {
            if (!kvp.Value.Interrupted)
            {
                kvp.Value.Interrupted = true;
            }
        }
    }

    /// <summary>Get the result for a finished script, or null if still running.</summary>
    public ScriptResponse? GetResult(int scriptId)
    {
        return _botApi.GetScriptResult(scriptId);
    }
}
