using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using MoonSharp.Interpreter;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace Lechonk;

/// <summary>Game API exposed to Lua scripts. All public methods become callable
/// as <c>bot.methodName(...)</c> in the MoonSharp sandbox.</summary>
public class BotApi
{
    private readonly IMonitor _monitor;
    private readonly ConcurrentDictionary<int, ScriptResult> _scriptResults = new();
    private readonly ConcurrentQueue<int> _finishedIds = new();

    public BotApi(IMonitor monitor)
    {
        _monitor = monitor;
    }

    // ── Movement ──────────────────────────────────────────────────────

    /// <summary>Move the farmer to tile (x, y). Returns when arrived, yields each tick.</summary>
    public object? MoveTo(object? x, object? y)
    {
        var tx = Convert.ToInt32(x);
        var ty = Convert.ToInt32(y);
        var farmer = Game1.player;
        var location = Game1.currentLocation;

        // Already there
        if (farmer.Tile.X == tx && farmer.Tile.Y == ty)
            return null;

        // Determine direction: 0=down, 1=left, 2=up, 3=right
        int dir;
        var dx = tx - farmer.Tile.X;
        var dy = ty - farmer.Tile.Y;

        if (Math.Abs(dx) >= Math.Abs(dy))
            dir = dx > 0 ? Farmer.right : Farmer.left;
        else
            dir = dy > 0 ? Farmer.down : Farmer.up;

        // Check if the target tile is passable
        if (!location.isTilePassable(new Vector2(tx, ty)))
        {
            return new YieldRequest();
        }

        // Try to move
        farmer.tryToMoveInDirection(dir, true, 0, false);

        return new YieldRequest();
    }

    /// <summary>Move the farmer in the given direction (0=down, 1=left, 2=up, 3=right) for one step.</summary>
    public object? MoveInDirection(object? dir, object? ticks)
    {
        var d = Convert.ToInt32(dir);
        Game1.player.tryToMoveInDirection(d, true, 0, false);
        return new YieldRequest();
    }

    /// <summary>Wait for the given number of ticks (yields once; tick budget counts the waits).</summary>
    public object? Wait(object? ticks)
    {
        return new YieldRequest();
    }

    // ── State ─────────────────────────────────────────────────────────

    /// <summary>Return the farmer's current tile coordinates as {x, y}.</summary>
    public object? GetPosition()
    {
        var f = Game1.player;
        return new Dictionary<string, object> { ["x"] = f.Tile.X, ["y"] = f.Tile.Y };
    }

    /// <summary>Check if the tile at (x, y) is walkable.</summary>
    public bool IsWalkable(object? x, object? y)
    {
        var location = Game1.currentLocation;
        var tx = Convert.ToInt32(x);
        var ty = Convert.ToInt32(y);
        return location.isTilePassable(new Vector2(tx, ty));
    }

    /// <summary>Get the object on the tile at (x, y), or null.</summary>
    public object? GetTileObject(object? x, object? y)
    {
        var location = Game1.currentLocation;
        var tx = Convert.ToInt32(x);
        var ty = Convert.ToInt32(y);
        var obj = location.getObjectAt(tx, ty, false);
        if (obj == null) return null;
        return new Dictionary<string, object> { ["name"] = obj.Name, ["id"] = obj.ItemId };
    }

    /// <summary>Return the player's inventory as a list of {name, count, id}.</summary>
    public object? GetInventory()
    {
        var items = new List<object>();
        foreach (var item in Game1.player.Items)
        {
            if (item != null && item.Name != "")
            {
                items.Add(new Dictionary<string, object> { ["name"] = item.Name, ["count"] = item.Stack, ["id"] = item.ItemId });
            }
        }
        return items.ToArray();
    }

    /// <summary>Return the current in-game hour (0-24).</summary>
    public int GetTime()
    {
        return Game1.timeOfDay / 1000;
    }

    /// <summary>Return the current season and day as {season, day}.</summary>
    public object? GetDate()
    {
        return new Dictionary<string, object> { ["season"] = Game1.currentSeason, ["day"] = Game1.Date.DayOfMonth };
    }

    // ── Menus ─────────────────────────────────────────────────────────

    /// <summary>Get info about the currently active menu, or null.</summary>
    public object? GetMenuType()
    {
        var menu = Game1.activeClickableMenu;
        if (menu == null) return null;

        return new
        {
            type = menu.GetType().Name,
            buttonCount = menu is IClickableMenu cm ? cm.getComponentWithID(0) != null ? 1 : 0 : 0
        };
    }

    /// <summary>Get button info for the active menu (if it has clickable components).</summary>
    public object? GetMenuButtons()
    {
        var menu = Game1.activeClickableMenu;
        if (menu == null) return null;

        // Try to get clickable components via getComponentWithID
        // We scan IDs 0-20 to find buttons
        var result = new List<object>();
        for (int i = 0; i < 20; i++)
        {
            var comp = menu.getComponentWithID(i);
            if (comp != null)
            {
                result.Add(new
                {
                    index = i,
                    label = comp.label ?? "",
                    visible = comp.visible,
                    myID = comp.myID
                });
            }
            else
            {
                break; // IDs are sequential, stop at first missing
            }
        }
        return result.ToArray();
    }

    /// <summary>Click the button at the given index on the active menu.</summary>
    public void ClickMenuButton(object? index)
    {
        var menu = Game1.activeClickableMenu;
        if (menu == null) return;

        var idx = Convert.ToInt32(index);
        var comp = menu.getComponentWithID(idx);
        if (comp == null) return;

        // Click at the component's screen position
        var bounds = comp.bounds;
        menu.receiveLeftClick(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2, true);
    }

    // ── Dialogue ──────────────────────────────────────────────────────

    /// <summary>Advance the current dialogue (skip the current response).</summary>
    public void AdvanceDialogue()
    {
        var menu = Game1.activeClickableMenu;
        if (menu is DialogueBox db)
        {
            db.clickAway();
        }
    }

    /// <summary>Select a dialogue option by index (0-based).</summary>
    public void SelectDialogueOption(object? index)
    {
        var menu = Game1.activeClickableMenu;
        if (menu is not DialogueBox db) return;

        var idx = Convert.ToInt32(index);
        var responses = db.responses;
        if (responses == null || idx < 0 || idx >= responses.Length) return;

        // Set the selected response and click away
        db.selectedResponse = idx;
        db.clickAway();
    }

    /// <summary>Close the current dialogue.</summary>
    public void CloseDialogue()
    {
        var menu = Game1.activeClickableMenu;
        if (menu is DialogueBox db)
        {
            db.closeDialogue();
        }
    }

    // ── Utility ───────────────────────────────────────────────────────

    /// <summary>Print a message (captured in the script response).</summary>
    public void Print(object? msg)
    {
        _monitor.Log($"script: {msg}", LogLevel.Trace);
    }

    // ── Internal result tracking ──────────────────────────────────────

    internal void RecordResult(int scriptId, string prints, object? result, string? error, int ticksUsed, ScriptEvent @event)
    {
        var r = new ScriptResult(scriptId, prints, result, error, ticksUsed, @event);
        _scriptResults[scriptId] = r;
        _finishedIds.Enqueue(scriptId);
    }

    internal ScriptResponse? GetScriptResult(int id)
    {
        if (_scriptResults.TryGetValue(id, out var r))
        {
            return r.ToResponse();
        }
        return null;
    }

    /// <summary>Get the full API schema for the GET /api endpoint.</summary>
    public object GetApiSchema()
    {
        return new
        {
            methods = new[]
            {
                new { name = "bot.moveTo(x, y)", description = "Walk the farmer to tile (x, y). Yields until arrival or stuck." },
                new { name = "bot.moveInDirection(dir, ticks)", description = "Move in direction (0=down, 1=left, 2=up, 3=right) for up to ticks steps." },
                new { name = "bot.wait(ticks)", description = "Wait for the given number of ticks." },
                new { name = "bot.getPosition()", description = "Return {x, y} of the farmer's current tile." },
                new { name = "bot.isWalkable(x, y)", description = "Check if tile (x, y) is walkable." },
                new { name = "bot.getTileObject(x, y)", description = "Get object at tile (x, y) or null." },
                new { name = "bot.getInventory()", description = "List of {name, count, id} for inventory items." },
                new { name = "bot.getTime()", description = "Current in-game hour (0-24)." },
                new { name = "bot.getDate()", description = "Return {season, day}." },
                new { name = "bot.getMenuType()", description = "Info about the active menu, or null." },
                new { name = "bot.getMenuButtons()", description = "Button info for the active menu." },
                new { name = "bot.clickMenuButton(index)", description = "Click button at index on the active menu." },
                new { name = "bot.advanceDialogue()", description = "Advance the current dialogue." },
                new { name = "bot.selectDialogueOption(index)", description = "Select a dialogue option by index." },
                new { name = "bot.closeDialogue()", description = "Close the current dialogue." },
                new { name = "bot.print(msg)", description = "Print a message (captured in response)." }
            }
        };
    }
}

/// <summary>Internal result storage.</summary>
internal record ScriptResult(
    int ScriptId,
    string Prints,
    object? Result,
    string? Error,
    int TicksUsed,
    ScriptEvent Event)
{
    public ScriptResponse ToResponse() => new()
    {
        ScriptId = ScriptId,
        Prints = Prints,
        Result = Sanitize(Result),
        Error = Error,
        TicksUsed = TicksUsed,
        Event = Event.ToString().ToLower()
    };

    private static object? Sanitize(object? obj)
    {
        if (obj == null) return null;

        // Primitives
        if (obj is int or long or float or double or decimal or byte or short
            or uint or ulong or ushort or sbyte or string or bool)
            return obj;

        if (obj is System.DateTime dt)
            return dt.ToString("O");

        // Collections
        if (obj is System.Collections.IEnumerable ie && obj is not string)
        {
            var list = new List<object?>();
            foreach (var item in ie)
                list.Add(Sanitize(item));
            return list.ToArray();
        }

        if (obj is System.Collections.IDictionary dic)
        {
            var dict = new Dictionary<string, object?>();
            foreach (System.Collections.DictionaryEntry entry in dic)
            {
                var key = Sanitize(entry.Key)?.ToString() ?? "";
                dict[key] = Sanitize(entry.Value);
            }
            return dict;
        }

        // Fallback: string representation
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
