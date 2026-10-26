using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StardewModdingAPI;
using MoonSharp.Interpreter;

namespace Lechonk;

/// <summary>Manages a single script at a time. <c>RunScriptBlocking</c> on the HTTP thread
/// creates a coroutine; the game loop resumes it each tick via <c>Advance</c>; when the
/// coroutine dies, the game loop completes the <c>TaskCompletionSource</c> and the HTTP
/// thread unblocks with the result. No script IDs, no registry, no status endpoint.</summary>
public class ScriptController
{
    private readonly BotApi _botApi;
    private readonly IMonitor _monitor;

    private TaskCompletionSource<ScriptResponse>? _tcs;
    private MoonSharp.Interpreter.Coroutine? _coro;
    private int _ticksUsed;
    private int _tickBudget;

    private static readonly object Gate = new();

    public ScriptController(BotApi botApi, IMonitor monitor)
    {
        _botApi = botApi;
        _monitor = monitor;
    }

    /// <summary>Blocking: runs <paramref name="luaCode"></paramref> as a coroutine over
    /// game ticks and returns the result once it finishes.</summary>
    public ScriptResponse RunScriptBlocking(string luaCode, int tickBudget = 20000)
    {
        lock (Gate)
        {
            if (_coro != null) throw new InvalidOperationException("A script is already running.");
            _ticksUsed = 0;
            _tickBudget = tickBudget;
            _coro = null;

            var script = new MoonSharp.Interpreter.Script();
            var botTable = new MoonSharp.Interpreter.Table(script);
            botTable["print"] = (Func<object?, object?>)(msg => { _botApi.Print(msg?.ToString()); return null; });
            botTable["tapButton"] = (Func<object?, object?>)(b => _botApi.TapButton(b));
            botTable["stop"] = (Func<object?, object?>)(_ => { _botApi.Stop(); return null; });
            botTable["wait"] = (Func<object?, object?>)(t => _botApi.Wait(t));
            botTable["getMapSchema"] = (Func<object?>)(() => _botApi.GetMapSchema());
            botTable["isWalkable"] = (Func<object?, object?, object?>)((x, y) => _botApi.IsWalkable(x, y));
            botTable["getTileObject"] = (Func<object?, object?, object?>)((x, y) => _botApi.GetTileObject(x, y));
            botTable["getInventory"] = (Func<object?>)(() => _botApi.GetInventory());
            botTable["getTime"] = (Func<object?>)(() => _botApi.GetTime());
            botTable["getDate"] = (Func<object?>)(() => _botApi.GetDate());
            botTable["listSaves"] = (Func<object?>)(() => _botApi.ListSaves());
            botTable["loadGame"] = (Func<object?, object?>)(slotName => _botApi.LoadGame(slotName));
            botTable["exitToTitle"] = (Func<object?, object?>)(_ => _botApi.ExitToTitle());
            botTable["waitForWorldReadyTick"] = (Func<object?, object?>)(_ => _botApi.WaitForWorldReady());
            botTable["wakeUp"] = (Func<object?, object?>)(_ => { _botApi.WakeUp(); return null; });
            botTable["listFurniture"] = (Func<object?>)(() => _botApi.ListFurniture());
            botTable["getMenuType"] = (Func<object?>)(() => _botApi.GetMenuType());
            botTable["getMenuButtons"] = (Func<object?>)(() => _botApi.GetMenuButtons());
            botTable["clickMenuButton"] = (Func<object?, object?>)(idx => { _botApi.ClickMenuButton(idx); return null; });
            botTable["advanceDialogue"] = (Func<object?, object?>)(_ => { _botApi.AdvanceDialogue(); return null; });
            botTable["selectDialogueOption"] = (Func<object?, object?>)(idx => { _botApi.SelectDialogueOption(idx); return null; });
            botTable["closeDialogue"] = (Func<object?, object?>)(_ => { _botApi.CloseDialogue(); return null; });

            script.Globals["bot"] = botTable;

            const string prelude = @"
                local Y = '__lechonk_yield__'

                function bot.pressButton(name, ticks)
                    ticks = ticks or 1
                    local ret = bot.tapButton(name)
                    if ret ~= nil then return ret end
                    for i = 1, ticks do
                        coroutine.yield()
                        if i < ticks then bot.tapButton(name) end
                    end
                end

                function bot.tap(name)
                    local ret = bot.tapButton(name)
                    if ret ~= nil then return ret end
                    coroutine.yield()
                end

                local DIRKEY = nil
                local function dirKey(dir)
                    if not DIRKEY then
                        DIRKEY = {
                            [0] = 'LeftThumbstickDown',
                            [1] = 'LeftThumbstickLeft',
                            [2] = 'LeftThumbstickUp',
                            [3] = 'LeftThumbstickRight',
                        }
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

                function bot.wait(ticks)
                    ticks = ticks or 1
                    for i = 1, ticks do
                        coroutine.yield()
                    end
                end

                function bot.waitForWorldReady()
                    while true do
                        local ret = bot.waitForWorldReadyTick()
                        if ret ~= Y then return ret end
                        coroutine.yield()
                    end
                end
            ";
            script.DoString(prelude);

            string? compileError = null;
            MoonSharp.Interpreter.Coroutine? coro = null;

            var wrappedResultVal = script.DoString($"return function() {luaCode} end");
            if (wrappedResultVal.Type != MoonSharp.Interpreter.DataType.Function)
            {
                compileError = $"failed to compile (got {wrappedResultVal.Type})";
            }
            else
            {
                var coroValue = script.CreateCoroutine(wrappedResultVal);
                coro = coroValue.Coroutine;
            }

            if (compileError != null || coro == null)
            {
                string err;
                if (compileError != null) err = "script_error: " + compileError;
                else err = "script_error: failed to create coroutine";
                var resp = new ScriptResponse
                {
                    Prints = "",
                    Result = null,
                    Error = err,
                    TicksUsed = 0,
                    Event = ScriptEvent.Cancelled
                };
                _tcs?.TrySetResult(resp);
                _tcs = null;
                _coro = null;
                return resp;
            }

            _coro = coro;
            _tcs = new TaskCompletionSource<ScriptResponse>();
            _ticksUsed = 0;

            // The game loop drives all Resume() calls via Advance().
            // A script that finishes in the first tick will have a dead coroutine;
            // Advance() handles the dead-coroutine case gracefully.
        }

        // Block HTTP thread until the game loop finishes the script
        var t = _tcs.Task;
        t.Wait();
        return t.Result;
    }

    /// <summary>Advance the script by one game tick. Called from the game loop.</summary>
    public void Advance()
    {
        if (_coro == null)
            return;

        // If the coroutine is already dead (script returned without yielding),
        // complete the result immediately without calling Resume().
        if (_coro.State == MoonSharp.Interpreter.CoroutineState.Dead)
        {
            var resp = new ScriptResponse
            {
                Prints = "",
                Result = null,
                Error = null,
                TicksUsed = _ticksUsed,
                Event = ScriptEvent.Completed
            };
            _coro = null;
            _tcs?.TrySetResult(resp);
            _tcs = null;
            lock (Gate) { }
            return;
        }

        _ticksUsed++;

        if (_ticksUsed >= _tickBudget)
        {
            _coro = null;
            var resp = new ScriptResponse
            {
                Prints = "",
                Result = null,
                Error = $"stuck: tick budget exceeded ({_ticksUsed}/{_tickBudget})",
                TicksUsed = _ticksUsed,
                Event = ScriptEvent.Stuck
            };
            _tcs?.TrySetResult(resp);
            _tcs = null;
            lock (Gate) { }
            return;
        }

        MoonSharp.Interpreter.DynValue result;
        try
        {
            result = _coro.Resume();
        }
        catch (Exception ex)
        {
            _coro = null;
            var resp = new ScriptResponse
            {
                Prints = "",
                Result = null,
                Error = $"script_error: {ex.Message}",
                TicksUsed = _ticksUsed,
                Event = ScriptEvent.Cancelled
            };
            _tcs?.TrySetResult(resp);
            _tcs = null;
            lock (Gate) { }
            return;
        }

        // Only a Dead coroutine is finished.
        if (_coro.State == MoonSharp.Interpreter.CoroutineState.Dead)
        {
            var resp = new ScriptResponse
            {
                Prints = "",
                Result = Sanitize(result.ToObject()),
                Error = null,
                TicksUsed = _ticksUsed,
                Event = ScriptEvent.Completed
            };
            _coro = null;
            _tcs?.TrySetResult(resp);
            _tcs = null;
            lock (Gate) { }
        }
    }

    public bool IsScriptActive() => _coro != null;

    private static object? Sanitize(object? obj)
    {
        if (obj == null) return null;
        if (obj is MoonSharp.Interpreter.Table table)
        {
            var dict = new System.Collections.Generic.Dictionary<string, object?>();
            foreach (var pair in table.Pairs)
                dict[pair.Key.Type == MoonSharp.Interpreter.DataType.String ? pair.Key.String : pair.Key.ToString()] = Sanitize(pair.Value.ToObject());
            return dict;
        }
        if (obj is int or long or float or double or decimal or byte or short
            or uint or ulong or ushort or sbyte or string or bool)
            return obj;
        if (obj is System.DateTime dt) return dt.ToString("O");
        if (obj is System.Collections.IEnumerable ie && obj is not string)
        {
            var list = new System.Collections.Generic.List<object?>();
            foreach (var item in (IEnumerable)ie) list.Add(Sanitize(item));
            return list.ToArray();
        }
        if (obj is System.Collections.IDictionary dic)
        {
            var dict = new System.Collections.Generic.Dictionary<string, object?>();
            foreach (var kv in (IEnumerable<System.Collections.DictionaryEntry>)(IEnumerable)dic)
                dict[(Sanitize(kv.Key)?.ToString()) ?? ""] = Sanitize(kv.Value);
            return dict;
        }
        return obj.ToString() ?? "";
    }
}

/// <summary>How a script finished.</summary>
public enum ScriptEvent
{
    Completed,
    Stuck,
    DialogInterrupted,
    Cancelled
}

/// <summary>Result of a finished script.</summary>
public class ScriptResponse
{
    public string Prints { get; set; } = "";
    public object? Result { get; set; }
    public string? Error { get; set; }
    public int TicksUsed { get; set; }
    public ScriptEvent Event { get; set; } = ScriptEvent.Completed;
    public string EventName => Event.ToString().ToLower();
}
