using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewValley;
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

    // Admin pause (POST /pause): while true, Advance() never resumes the
    // script coroutine, so the bot is frozen together with the game.
    private bool _paused;

    // Pending pause toggle: queued from the HTTP thread, resolved on the next
    // game tick by Advance().
    private TaskCompletionSource<PauseResponse>? _pauseTcs;

    public ScriptController(BotApi botApi, IMonitor monitor)
    {
        _botApi = botApi;
        _monitor = monitor;
    }

    /// <summary>Blocking: queues a pause toggle to run on the next game tick
    /// and waits for the result. The toggle opens/closes the game's in-game
    /// menu (GameMenu) directly — no input involved, so it works even when
    /// the game window has no focus.</summary>
    public PauseResponse TogglePauseBlocking()
    {
        lock (Gate)
        {
            if (_pauseTcs != null) throw new InvalidOperationException("A pause toggle is already pending.");
            _pauseTcs = new TaskCompletionSource<PauseResponse>();
        }
        var t = _pauseTcs.Task;
        t.Wait();
        return t.Result;
    }

    /// <summary>Open or close the in-game menu (GameMenu) directly, on the
    /// game thread. State-driven: closes it if it is open, opens it if no
    /// menu is open, and refuses when the game won't allow it (event, dialog,
    /// or another menu up). Works with or without window focus.</summary>
    private PauseResponse DoPauseToggle()
    {
        var menu = Game1.activeClickableMenu;
        if (menu is StardewValley.Menus.GameMenu)
        {
            Game1.exitActiveMenu();
            _paused = false;
            _monitor.Log("[Lechonk] resumed (in-game menu closed)", LogLevel.Info);
            return new PauseResponse { Ok = true, Paused = false };
        }
        if (menu != null)
        {
            return new PauseResponse { Ok = false, Error = $"another menu is open ({menu.GetType().Name})" };
        }
        if (!Game1.CanShowPauseMenu())
        {
            return new PauseResponse { Ok = false, Error = "cannot pause now — the game is busy (event or dialog)" };
        }
        Game1.PushUIMode();
        Game1.activeClickableMenu = new StardewValley.Menus.GameMenu();
        Game1.PopUIMode();
        _paused = true;
        _monitor.Log("[Lechonk] paused (in-game menu open, script frozen)", LogLevel.Info);
        return new PauseResponse { Ok = true, Paused = true };
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
            botTable["wait"] = (Func<object?, object?>)(t => _botApi.Wait(t));
            botTable["isTilePassable"] = (Func<object?, object?, object?>)((x, y) => _botApi.IsTilePassable(x, y));
            botTable["getTileObject"] = (Func<object?, object?, object?>)((x, y) => _botApi.GetTileObject(x, y));
            botTable["listObjects"] = (Func<object?>)(() => _botApi.ListObjects());
            botTable["listTerrainFeatures"] = (Func<object?>)(() => _botApi.ListTerrainFeatures());
            botTable["getWarps"] = (Func<object?>)(() => _botApi.GetWarps());
            botTable["listNpcs"] = (Func<object?>)(() => _botApi.ListNpcs());
            botTable["listAnimals"] = (Func<object?>)(() => _botApi.ListAnimals());
            botTable["getEnergy"] = (Func<object?>)(() => _botApi.GetEnergy());
            botTable["getPosition"] = (Func<object?>)(() => _botApi.GetPosition());
            botTable["getInventory"] = (Func<object?>)(() => _botApi.GetInventory());
            botTable["selectTool"] = (Func<object?, object?>)(name => _botApi.SelectTool(name));
            botTable["getTime"] = (Func<object?>)(() => _botApi.GetTime());
            botTable["getDate"] = (Func<object?>)(() => _botApi.GetDate());
            botTable["listSaves"] = (Func<object?>)(() => _botApi.ListSaves());
            botTable["loadGame"] = (Func<object?, object?>)(slotName => _botApi.LoadGame(slotName));
            botTable["exitToTitle"] = (Func<object?, object?>)(_ => _botApi.ExitToTitle());
            botTable["waitForWorldReadyTick"] = (Func<object?, object?>)(_ => _botApi.WaitForWorldReady());
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

                function bot.tap(name)
                    local ret = bot.tapButton(name)
                    if ret ~= nil then return ret end
                    coroutine.yield()
                end

                local function holdButton(btnName, ticks)
                    local ret = bot.tapButton(btnName)
                    if ret ~= nil then return ret end
                    for i = 1, ticks do
                        coroutine.yield()
                        if i < ticks then bot.tapButton(btnName) end
                    end
                end
                function bot.moveUp(ticks)    return holdButton('LeftThumbstickUp', ticks) end
                function bot.moveRight(ticks) return holdButton('LeftThumbstickRight', ticks) end
                function bot.moveDown(ticks)  return holdButton('LeftThumbstickDown', ticks) end
                function bot.moveLeft(ticks)  return holdButton('LeftThumbstickLeft', ticks) end

                -- Right thumbstick drives the cursor (also scrolls in menus).
                function bot.cursorUp(ticks)    return holdButton('RightThumbstickUp', ticks) end
                function bot.cursorRight(ticks) return holdButton('RightThumbstickRight', ticks) end
                function bot.cursorDown(ticks)  return holdButton('RightThumbstickDown', ticks) end
                function bot.cursorLeft(ticks)  return holdButton('RightThumbstickLeft', ticks) end

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

            try
            {
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
            }
            catch (Exception ex)
            {
                compileError = ErrWithLine(ex); // e.g. a Lua syntax error
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

    /// <summary>Format a script exception — MoonSharp's DecoratedMessage carries the line and column.</summary>
    private static string ErrWithLine(Exception ex)
    {
        if (ex is MoonSharp.Interpreter.InterpreterException mx)
        {
            var dec = mx.DecoratedMessage;
            if (!string.IsNullOrEmpty(dec))
                return "script_error: " + dec.Replace("chunk_1:", "");
        }
        return "script_error: " + ex.Message;
    }

    /// <summary>Advance the script by one game tick. Called from the game loop.</summary>
    public void Advance()
    {
        if (_pauseTcs != null)
        {
            _pauseTcs.TrySetResult(DoPauseToggle());
            _pauseTcs = null;
        }
        if (_paused || _coro == null)
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
                Error = ErrWithLine(ex),
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
        // Multi-value returns arrive as DynValue tuples; unwrap DynValues so
        // tables don't fall through to ToString() ("(Table)").
        if (obj is MoonSharp.Interpreter.DynValue dv)
            obj = dv.ToObject();
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

/// <summary>Result of an admin pause toggle.</summary>
public class PauseResponse
{
    public bool Ok { get; set; }
    public bool Paused { get; set; }
    public string? Error { get; set; }
}
