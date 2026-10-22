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
        botTable["print"] = (Func<object?, object?>)(msg => { _botApi.Print(msg?.ToString()); return null; });
        botTable["moveTo"] = (Func<object?, object?, object?>)( (x, y) => _botApi.MoveTo(x, y) );
        botTable["moveInDirection"] = (Func<object?, object?, object?>)( (d, t) => _botApi.MoveInDirection(d, t) );
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
        botTable["getMenuType"] = (Func<object?>)(() => _botApi.GetMenuType());
        botTable["getMenuButtons"] = (Func<object?>)(() => _botApi.GetMenuButtons());
        botTable["clickMenuButton"] = (Func<object?, object?>)(idx => { _botApi.ClickMenuButton(idx); return null; });
        botTable["advanceDialogue"] = (Func<object?, object?>)(_ => { _botApi.AdvanceDialogue(); return null; });
        botTable["selectDialogueOption"] = (Func<object?, object?>)(idx => { _botApi.SelectDialogueOption(idx); return null; });
        botTable["closeDialogue"] = (Func<object?, object?>)(_ => { _botApi.CloseDialogue(); return null; });

        script.Globals["bot"] = botTable;

        // Try executing user code directly first
        var result = script.DoString(luaCode);
        MoonSharp.Interpreter.Coroutine coro;

        if (result.Type == MoonSharp.Interpreter.DataType.Function)
        {
            var coroValue = script.CreateCoroutine(result);
            coro = coroValue.Coroutine;
        }
        else
        {
            var wrappedCode = $"return function() {luaCode} end";
            var wrappedResult = script.DoString(wrappedCode);
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

            if (result.YieldRequest != null)
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
