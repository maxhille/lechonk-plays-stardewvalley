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
        botTable["moveTick"] = (Func<object?, object?>)(d => _botApi.MoveTick(d));
        botTable["stop"] = (Func<object?, object?>)(_ => { _botApi.Stop(); return null; });
        botTable["wait"] = (Func<object?, object?>)(t => _botApi.Wait(t));
        botTable["getPosition"] = (Func<object?>)(() => _botApi.GetPosition());
        botTable["isWalkable"] = (Func<object?, object?, object?>)( (x, y) => _botApi.IsWalkable(x, y) );
        botTable["getTileObject"] = (Func<object?, object?, object?>)( (x, y) => _botApi.GetTileObject(x, y) );
        botTable["getInventory"] = (Func<object?>)(() => _botApi.GetInventory());
        botTable["getTime"] = (Func<object?>)(() => _botApi.GetTime());
        botTable["getDate"] = (Func<object?>)(() => _botApi.GetDate());
        botTable["listSaves"] = (Func<object?>)(() => _botApi.ListSaves());
        botTable["loadGame"] = (Func<object?, object?>)(slotName => _botApi.LoadGame(slotName));
        botTable["isWorldReady"] = (Func<object?>)(() => _botApi.IsWorldReady());
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

            -- bot.move(dir, ticks): hold a movement direction for `ticks`
            -- game ticks, yielding once per tick, then stop.
            function bot.move(dir, ticks)
                ticks = ticks or 1
                for i = 1, ticks do
                    local ret = bot.moveTick(dir)
                    if ret == Y then coroutine.yield() end
                end
                bot.stop()
            end

            -- bot.wait(ticks): yield once per tick for `ticks` ticks.
            function bot.wait(ticks)
                ticks = ticks or 1
                for i = 1, ticks do
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

            var result = state.Coro.Resume();
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
