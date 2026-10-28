using System;
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

    /// <summary>Marker string that tells the Lua shim to yield this tick.
    /// Scripts never see it: the shim converts it to coroutine.yield().</summary>
    public const string Yield = "__lechonk_yield__";

    public BotApi(IModHelper helper, IMonitor monitor)
    {
        _helper = helper;
        _monitor = monitor;
    }

    // ── Input (gamepad-style buttons) ─────────────────────────────────

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

    /// <summary>Debug: list furniture in the current location as {name, tile, isBed}.</summary>
    public object? ListFurniture()
    {
        try
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
        catch (Exception ex)
        {
            _monitor.Log($"listFurniture failed: {ex}", LogLevel.Warn);
            return "error: " + ex.Message;
        }
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
        try
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
        catch (Exception ex)
        {
            _monitor.Log($"getMapSchema failed: {ex}", LogLevel.Warn);
            return "error: " + ex.Message;
        }
    }

    /// <summary>Player's current tile position {x, y} (0-indexed).</summary>
    public object? GetPosition()
    {
        var f = Game1.player;
        if (f == null) return null;
        return new Dictionary<string, object?> { ["x"] = f.Tile.X, ["y"] = f.Tile.Y };
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
        try
        {
            var location = Game1.currentLocation;
            var tx = Convert.ToInt32(x);
            var ty = Convert.ToInt32(y);
            // 1.6: getObjectAt takes PIXEL coords; getObjectAtTile takes tiles.
            var obj = location.getObjectAtTile(tx, ty, false);
            if (obj == null) return null;
            return new Dictionary<string, object> { ["name"] = obj.Name, ["id"] = obj.ItemId };
        }
        catch (Exception ex)
        {
            _monitor.Log($"getTileObject failed: {ex}", LogLevel.Warn);
            return "error: " + ex.Message;
        }
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

    /// <summary>Return the current in-game time as {hour, minute}.</summary>
    /// <remarks>Game1.timeOfDay is minutes since midnight; the clock runs
    /// 06:00 (360) to 26:00 (1560), so early morning hours can be 24 or 25.</remarks>
    public object GetTime()
    {
        int m = Game1.timeOfDay;
        return new Dictionary<string, int> { ["hour"] = m / 60, ["minute"] = m % 60 };
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

    /// <summary>Block the script until the world is loaded and ready, yielding
    /// once per tick. Returns true when ready. Use after bot.loadGame(). The script
    /// terminates as soon as the world is up, so the next script can start playing
    /// immediately.</summary>
    public object? WaitForWorldReady()
    {
        if (Context.IsWorldReady)
            return true;
        return Yield;
    }

    // ── Input ─────────────────────────────────────────────────────────

    // ── Menus ─────────────────────────────────────────────────────────

    /// <summary>Get info about the currently active menu, or null.</summary>
    public object? GetMenuType()
    {
        var menu = Game1.activeClickableMenu;
        if (menu == null)
        {
            // The emote wheel is not a ClickableMenu but it blocks movement,
            // so surface it here too.
            if (Game1.emoteMenu != null)
                return new Dictionary<string, object?> { ["type"] = "EmoteMenu" };
            return null;
        }

        // Return a Dictionary, not an anonymous object: MoonSharp cannot
        // convert anonymous CLR types into Lua values.
        return new Dictionary<string, object?>
        {
            ["type"] = menu.GetType().Name,
            ["buttonCount"] = menu is IClickableMenu cm ? cm.getComponentWithID(0) != null ? 1 : 0 : 0,
            ["text"] = menu is DialogueBox db ? db.getCurrentString() : null
        };
    }

    /// <summary>Get button info for the active menu (if it has clickable components).
    /// When the menu is a DialogueBox, the response text is exposed as the label of
    /// each response component, and an additional <c>responseText</c> array is included.</summary>
    public object? GetMenuButtons()
    {
        var menu = Game1.activeClickableMenu;
        if (menu == null) return null;

        // Extract dialogue response labels so the agent can read what each option says.
        string[] responseText = null;
        if (menu is DialogueBox db)
        {
            if (db.responses != null)
            {
                responseText = new string[db.responses.Length];
                for (int i = 0; i < db.responses.Length; i++)
                {
                    var resp = db.responses[i];
                    // Response exposes its display string via Label (Stardew's Response class).
                    responseText[i] = GetResponseLabel(resp);
                }
            }
        }

        var result = new List<object>();
        for (int i = 0; i < 32; i++)
        {
            var comp = menu.getComponentWithID(i);
            if (comp == null) break; // IDs are sequential, stop at first missing

            var bounds = new Dictionary<string, object?>
            {
                ["x"] = comp.bounds.X,
                ["y"] = comp.bounds.Y,
                ["w"] = comp.bounds.Width,
                ["h"] = comp.bounds.Height
            };

            // Prefer the component's own label; fall back to the response text when the
            // component's label is empty (DialogueBox responses keep their text on Response,
            // not on the button UIElement).
            var label = comp.label ?? "";
            if (responseText != null && i < responseText.Length && string.IsNullOrEmpty(label))
            {
                label = responseText[i];
            }

            result.Add(new Dictionary<string, object?>
            {
                ["index"] = i,
                ["type"] = comp.GetType().Name,
                ["label"] = label,
                ["visible"] = comp.visible,
                ["myID"] = comp.myID,
                ["bounds"] = bounds
            });
        }

        if (responseText != null)
        {
            result.Add(new Dictionary<string, object?>
            {
                ["_responseText"] = responseText.ToArray()
            });
        }

        return result.ToArray();
    }

    /// <summary>Extract the display text of a DialogueBox response.
    /// Responses carry their display string in a string property (name varies by
    /// game version); take the first public string-valued instance property.
    /// </summary>
    private static string GetResponseLabel(object response)
    {
        if (response == null) return "";
        var type = response.GetType();
        // Prefer known field names (responseText is the real display-text field in SV 1.5/1.6).
        foreach (var fname in new[] { "responseText", "Label", "Text" })
        {
            var field = type.GetField(fname, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (field != null)
            {
                var val = field.GetValue(response);
                if (val != null) return val.ToString();
            }
        }
        foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (prop.PropertyType == typeof(string))
            {
                var val = prop.GetValue(response);
                if (val != null) return val.ToString();
            }
        }
        return response.ToString() ?? "";
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
                new { name = "bot.waitForWorldReady()", description = "Block until the world is loaded and ready (yields once per tick), then return true. Use after bot.loadGame()." },
                new { name = "bot.tap(buttonName)", description = "Tap a button and wait one tick for the game to process it." },
                new { name = "bot.moveLeft(ticks) / bot.moveRight(ticks) / bot.moveUp(ticks) / bot.moveDown(ticks)", description = "Hold the LEFT thumbstick in a direction for `ticks` input ticks. Movement stops automatically when the script ends." },
                new { name = "bot.wait(ticks)", description = "Wait for the given number of ticks." },
                new { name = "bot.getPosition()", description = "Your current tile position {x, y} (0-indexed)." },
                new { name = "bot.getMapSchema()", description = "Map schema for the current location: { location, width, height, player {x, y}, walkable (array of row strings; row i+1 is tile y=i; '.' walkable, '#' blocked), warps [{x, y, target, targetX, targetY}] }. All coordinates are 0-indexed tiles." },
                new { name = "bot.isWalkable(x, y)", description = "Check if tile (x, y) is walkable." },
                new { name = "bot.getTileObject(x, y)", description = "Get object at tile (x, y) or null." },
                new { name = "bot.getInventory()", description = "List of {name, count, id} for inventory items." },
                new { name = "bot.listFurniture()", description = "All furniture in the current location: {name, x, y}." },
                new { name = "bot.getTime()", description = "Current in-game time {hour, minute}. The clock runs 06:00-26:00, so early morning hours can be 24 or 25." },
                new { name = "bot.getDate()", description = "Return {season, day}." },
                new { name = "bot.getMenuType()", description = "Info about the active menu or the emote wheel (type \"EmoteMenu\"), or null." },
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

