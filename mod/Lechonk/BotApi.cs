using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using MoonSharp.Interpreter;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;

namespace Lechonk;

/// <summary>Game API exposed to Lua scripts. All public methods become callable
/// as <c>bot.methodName(...)</c> in the MoonSharp sandbox.</summary>
public class BotApi
{
    private readonly IMonitor _monitor;
    private readonly IModHelper _helper;

    /// <summary>Set in Entry after construction; used for input isolation.</summary>
    public InputGuard? InputGuard { get; set; }

    /// <summary>Marker string that tells the Lua shim to yield this tick.
    /// Scripts never see it: the shim converts it to coroutine.yield().</summary>
    public const string Yield = "__lechonk_yield__";
    private readonly ConcurrentDictionary<int, ScriptResult> _scriptResults = new();
    private readonly ConcurrentQueue<int> _finishedIds = new();

    public BotApi(IModHelper helper, IMonitor monitor)
    {
        _helper = helper;
        _monitor = monitor;
    }

    // ── Input (gamepad-style buttons) ─────────────────────────────────

    /// <summary>Press a button for one tick and return the yield marker.
    /// <paramref name="buttonName"/> is any SButton name: gamepad buttons
    /// (DPadUp/DPadDown/DPadLeft/DPadRight, LeftThumbstickUp/..., A, B, X, Y,
    /// LeftShoulder, RightShoulder, ...), keyboard keys (W, E, Escape, ...),
    /// or mouse buttons (MouseLeft, MouseRight). The press goes through
    /// SMAPI's input helper — equivalent to physically holding the button for
    /// one input tick; the game's own input pipeline does the rest.
    /// The Lua shim loops this for bot.pressButton(name, ticks).</summary>
    public object? PressButtonTick(object? buttonName)
    {
        var name = buttonName?.ToString();
        if (string.IsNullOrEmpty(name) || !Enum.TryParse<SButton>(name, true, out var button))
            return $"error: unknown button '{name}'";
        _helper.Input.Press(button);
        return Yield;
    }

    /// <summary>Tap a button (single input tick, no yield). Pair with
    /// bot.wait(1) or use bot.tap() to let the game process it.</summary>
    public object? TapButton(object? buttonName)
    {
        var name = buttonName?.ToString();
        if (string.IsNullOrEmpty(name) || !Enum.TryParse<SButton>(name, true, out var button))
            return $"error: unknown button '{name}'";
        _helper.Input.Press(button);
        return null;
    }

    /// <summary>Stop any held movement immediately.</summary>
    public void Stop()
    {
        Game1.player.Halt();
    }

    /// <summary>Wake the farmer up from bed. Farmer.Update re-sets isInBed while the
    /// player stands on a tile with the "Bed" map property, so this teleports the
    /// farmer to the bed's exit spot first (the vanilla wake-up position), then
    /// clears the in-bed flag. A no-op if not in bed.</summary>
    public object? WakeUp()
    {
        var farmer = Game1.player;
        if (!farmer.isInBed.Value)
            return null;

        // Farmer.Update re-flags isInBed=true every tick while the player
        // stands on a tile with the "Bed" map property (verified in IL), so
        // the exit must be fully outside the bed footprint. Scan tiles around
        // the bed for the first passable non-bed tile: below the bed first
        // (the vanilla get-up side), then above, then left, then right.
        var location = Game1.currentLocation!;
        var bed = location.furniture.FirstOrDefault(f => f is BedFurniture) as BedFurniture;
        if (bed == null)
        {
            // No bed furniture found: just clear the flag.
            farmer.isInBed.Value = false;
            farmer.forceCanMove();
            return null;
        }

        var bedTile = bed.TileLocation;
        var candidates = new[]
        {
            new Vector2(bedTile.X, bedTile.Y + 2),  // below the foot end
            new Vector2(bedTile.X, bedTile.Y - 1),  // above the head end
            new Vector2(bedTile.X - 1, bedTile.Y),  // left side
            new Vector2(bedTile.X + 2, bedTile.Y),  // right side
        };
        Vector2? exit = null;
        foreach (var t in candidates)
        {
            if (location.isTilePassable(t)
                && location.doesTileHaveProperty((int)t.X, (int)t.Y, "Bed", "Back", false) is null)
            {
                exit = t * 64f;
                break;
            }
        }
        if (exit == null)
        {
            // Fall back to below-the-bed regardless of the Bed property check.
            exit = new Vector2(bedTile.X, bedTile.Y + 2) * 64f;
        }

        farmer.Position = exit.Value;
        farmer.isInBed.Value = false;
        farmer.forceCanMove();
        return new Dictionary<string, object> { ["x"] = farmer.Tile.X, ["y"] = farmer.Tile.Y };
    }

    /// <summary>Debug: list furniture in the current location as {name, tile, isBed}.</summary>
    public object? ListFurniture()
    {
        var result = new List<object>();
        foreach (var f in Game1.currentLocation.furniture)
            result.Add(new Dictionary<string, object?>
            {
                ["name"] = f.Name,
                ["type"] = f.GetType().Name,
                ["x"] = f.TileLocation.X,
                ["y"] = f.TileLocation.Y,
                ["isBed"] = f is BedFurniture,
            });
        return result.ToArray();
    }

    /// <summary>Get debug movement state: tile, CanMove, isMoving, and whether an emote/menu blocks.</summary>
    public object? GetMovementState()
    {
        var f = Game1.player;
        return new Dictionary<string, object?>
        {
            ["tile"] = new Dictionary<string, object> { ["x"] = f.Tile.X, ["y"] = f.Tile.Y },
            ["position"] = new Dictionary<string, object> { ["x"] = f.Position.X, ["y"] = f.Position.Y },
            ["isInBed"] = f.isInBed.Value,
            ["canMove"] = f.CanMove,
            ["frozenAnimationPause"] = f.freezePause > 0,
            ["activeMenu"] = Game1.activeClickableMenu?.GetType().Name,
            ["currentLocation"] = Game1.currentLocation?.Name,
        };
    }

    /// <summary>Wait for the given number of ticks (yields once; tick budget counts the waits).</summary>
    public object? Wait(object? ticks)
    {
        return Yield;
    }

    // ── State ─────────────────────────────────────────────────────────

    /// <summary>Map schema for the current location: name, tile dimensions, the
    /// player's tile, a walkability grid ('.' walkable, '#' blocked, one string
    /// per row), and the warp points (doors/exits with their targets). Built
    /// from the game's xTile map (GameLocation.map) and warp list.</summary>
    public object? GetMapSchema()
    {
        var loc = Game1.currentLocation;
        if (loc == null || loc.map == null)
            return null;

        int w = loc.map.Layers[0].LayerWidth;
        int h = loc.map.Layers[0].LayerHeight;

        var rows = new string[h];
        for (int y = 0; y < h; y++)
        {
            var sb = new System.Text.StringBuilder(w);
            for (int x = 0; x < w; x++)
                sb.Append(loc.isTilePassable(new Vector2(x, y)) ? '.' : '#');
            rows[y] = sb.ToString();
        }

        var warps = new List<object>();
        foreach (var warp in loc.warps)
        {
            warps.Add(new Dictionary<string, object?>
            {
                ["x"] = warp.X,
                ["y"] = warp.Y,
                ["target"] = warp.TargetName,
                ["targetX"] = warp.TargetX,
                ["targetY"] = warp.TargetY,
            });
        }

        var f = Game1.player;
        return new Dictionary<string, object?>
        {
            ["location"] = loc.Name,
            ["width"] = w,
            ["height"] = h,
            ["player"] = new Dictionary<string, object?> { ["x"] = f.Tile.X, ["y"] = f.Tile.Y },
            ["walkable"] = rows,
            ["warps"] = warps.ToArray(),
        };
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

    // ── Save / load ─────────────────────────────────────────────────

    /// <summary>List available saves (folder names in the saves directory).</summary>
    public object? ListSaves()
    {
        var savesDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StardewValley", "Saves");
        if (!Directory.Exists(savesDir))
            return Array.Empty<string>();
        return Directory.GetDirectories(savesDir)
            .Select(Path.GetFileName)
            .Where(n => n != null)
            .Cast<string>()
            .ToArray();
    }

    /// <summary>Load a save from the title screen. <paramref name="slotName"/> is the
    /// save folder name (e.g. "Max_129552567"), as returned by listSaves.</summary>
    public object? LoadGame(object? slotName)
    {
        var name = slotName?.ToString();
        if (string.IsNullOrEmpty(name))
            return "error: slotName required";
        if (Game1.hasLoadedGame)
            return "error: a game is already loaded (save and exit first)";
        if (Game1.activeClickableMenu is not TitleMenu)
            return "error: not on the title screen";

        SaveGame.Load(name);
        Game1.exitActiveMenu();
        return $"loading {name}";
    }

    /// <summary>Quit the current save back to the title screen so another save
    /// can be loaded. No-op if no game is loaded.</summary>
    public object? ExitToTitle()
    {
        if (!Game1.hasLoadedGame)
            return "error: no game loaded";
        Game1.ExitToTitle(null);
        return "exiting to title";
    }

    /// <summary>Check whether a save is loaded and the world is ready.</summary>
    public bool IsWorldReady()
    {
        return Context.IsWorldReady;
    }

    // ── Input ─────────────────────────────────────────────────────────

    /// <summary>Lock or unlock the user's physical input. While locked (or while
    /// any script is running), the game ignores the user's keyboard/mouse/gamepad
    /// input entirely — only the bot's synthetic button presses register.</summary>
    public object? SetInputLock(object? enabled)
    {
        if (InputGuard == null)
            return "error: input guard not initialised";
        InputGuard.Locked = Convert.ToBoolean(enabled);
        return InputGuard.Locked;
    }

    /// <summary>Whether the user's physical input is currently locked out.</summary>
    public object? IsInputLocked()
    {
        return InputGuard?.Locked ?? false;
    }

    /// <summary>Check whether a button is currently suppressed (debug).</summary>
    public object? IsSuppressed(object? buttonName)
    {
        return InputGuard?.IsSuppressed(buttonName) ?? false;
    }

    /// <summary>Enable or disable gamepad controls (like plugging in a
    /// controller). While enabled the game reads gamepad buttons — DPad and
    /// left stick for movement, face buttons for action/tool/menus — and
    /// ignores the keyboard movement keys. SMAPI synthesizes the thumbstick
    /// axis from LeftThumbstick* button presses, so synthetic stick input
    /// works without real hardware.</summary>
    public object? SetGamepadControls(object? enabled)
    {
        Game1.options.gamepadControls = Convert.ToBoolean(enabled);
        return Game1.options.gamepadControls;
    }

    /// <summary>Get the game's keybinds as semantic action → key name, resolved
    /// from Game1.options so they follow the player's configured bindings.</summary>
    public object? GetKeybinds()
    {
        var o = Game1.options;
        string Key(InputButton[] buttons) =>
            buttons.Length > 0 ? ((SButton)buttons[0].key).ToString() : "";
        return new Dictionary<string, object?>
        {
            ["action"] = Key(o.actionButton),
            ["useTool"] = Key(o.useToolButton),
            ["cancel"] = Key(o.cancelButton),
            ["menu"] = Key(o.menuButton),
            ["map"] = Key(o.mapButton),
            ["journal"] = Key(o.journalButton),
            ["emote"] = Key(o.emoteButton),
            ["run"] = Key(o.runButton),
            ["chat"] = Key(o.chatButton),
            ["moveUp"] = Key(o.moveUpButton),
            ["moveDown"] = Key(o.moveDownButton),
            ["moveLeft"] = Key(o.moveLeftButton),
            ["moveRight"] = Key(o.moveRightButton),
            ["gamepadControls"] = o.gamepadControls,
        };
    }

    // ── Menus ─────────────────────────────────────────────────────────

    /// <summary>Get info about the currently active menu, or null.</summary>
    public object? GetMenuType()
    {
        var menu = Game1.activeClickableMenu;
        if (menu == null) return null;

        // Return a Dictionary, not an anonymous object: MoonSharp cannot
        // convert anonymous CLR types into Lua values.
        return new Dictionary<string, object?>
        {
            ["type"] = menu.GetType().Name,
            ["buttonCount"] = menu is IClickableMenu cm ? cm.getComponentWithID(0) != null ? 1 : 0 : 0
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
                result.Add(new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["label"] = comp.label ?? "",
                    ["visible"] = comp.visible,
                    ["myID"] = comp.myID
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
                new { name = "bot.listSaves()", description = "List save folder names (e.g. Max_129552567)." },
                new { name = "bot.loadGame(slotName)", description = "From the title screen, load the given save folder. Returns 'loading <name>' or 'error: ...'." },
                new { name = "bot.exitToTitle()", description = "Quit the current save back to the title screen." },
                new { name = "bot.isWorldReady()", description = "True once a save is loaded and the world is ready." },
                new { name = "bot.pressButton(buttonName, ticks)", description = "Hold a gamepad-style button (DPadUp/DPadDown/DPadLeft/DPadRight, A, X, MouseLeft, ...) for `ticks` input ticks. Blocking; yields once per tick." },
                new { name = "bot.tap(buttonName)", description = "Tap a button and wait one tick for the game to process it." },
                new { name = "bot.move(dir, ticks)", description = "Hold a movement direction (0=down, 1=left, 2=up, 3=right) for `ticks` input ticks, using the game's configured movement keybinds. Convenience wrapper over bot.pressButton." },
                new { name = "bot.stop()", description = "Stop any held movement immediately." },
                new { name = "bot.getKeybinds()", description = "Semantic action -> key name, resolved from the game's configured bindings. Includes gamepadControls flag." },
                new { name = "bot.setGamepadControls(enabled)", description = "Enable/disable gamepad controls. Required for DPad/stick/face-button input to be read by the game." },
                new { name = "bot.setInputLock(enabled)", description = "Lock out the user's physical keyboard/mouse/gamepad input so it can't interfere with the bot. Also auto-applies while any script is running." },
                new { name = "bot.isInputLocked()", description = "Whether the user's physical input is locked out." },
                new { name = "bot.isSuppressed(buttonName)", description = "Debug: whether a button is currently suppressed." },
                new { name = "bot.wakeUp()", description = "Get out of bed (teleports to the bed exit spot). Needed after loading a save made in bed." },
                new { name = "bot.wait(ticks)", description = "Wait for the given number of ticks." },
                new { name = "bot.getMapSchema()", description = "Map schema for the current location: { location, width, height, player {x, y}, walkable (grid rows of '.'/'#'), warps [{x, y, target, targetX, targetY}] }." },
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

        // MoonSharp tables returned by the script
        if (obj is MoonSharp.Interpreter.Table table)
        {
            var dict = new Dictionary<string, object?>();
            foreach (var pair in table.Pairs)
                dict[pair.Key.Type == MoonSharp.Interpreter.DataType.String ? pair.Key.String : pair.Key.ToString()] = Sanitize(pair.Value.ToObject());
            return dict;
        }

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
