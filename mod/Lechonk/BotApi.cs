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

    /// <summary>Gamepad button names accepted by <see cref="TapButton"/>, mapped to
    /// the controller <see cref="SButton"/> values. Plain A/B/X/Y/Back are keyboard
    /// keys in SMAPI's unified enum, so they must be resolved to the controller
    /// explicitly. Stick directions have no keyboard collision and fall through
    /// to a raw <see cref="SButton"/> parse.</summary>
    private static readonly Dictionary<string, SButton> GamepadButtons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = SButton.ControllerA,
        ["B"] = SButton.ControllerB,
        ["X"] = SButton.ControllerX,
        ["Y"] = SButton.ControllerY,
        ["Start"] = SButton.ControllerStart,
        ["Back"] = SButton.ControllerBack,
        ["LT"] = SButton.LeftTrigger,
        ["RT"] = SButton.RightTrigger,
        ["LB"] = SButton.LeftShoulder,
        ["RB"] = SButton.RightShoulder,
    };

    /// <summary>Tap a gamepad button (single input tick, no yield). Pair with
    /// bot.wait(1) or use bot.tap() to let the game process it.</summary>
    public object? TapButton(object? buttonName)
    {
        var name = buttonName?.ToString();
        if (string.IsNullOrEmpty(name))
            return "error: no button name given";

        // Friendly gamepad names first (disambiguated from keyboard keys), then
        // raw SButton names (stick directions used by move*/cursor*). Gamepad only:
        // keyboard and mouse buttons are deliberately not exposed.
        if (GamepadButtons.TryGetValue(name, out var button))
        {
            _helper.Input.Press(button);
            return null;
        }
        if (Enum.TryParse<SButton>(name, true, out var raw) && raw >= SButton.ControllerA)
        {
            _helper.Input.Press(raw);
            return null;
        }
        return $"error: unknown button '{name}' (gamepad only: A, B, X, Y, Start, Back, LT, RT, LB, RB, LeftThumbstick*, RightThumbstick*)";
    }

    /// <summary>Select the hotbar slot holding the item or tool with the given
    /// name (case-insensitive) — the direct equivalent of cycling with LT/RT.
    /// Selects a slot you already hold; it grants nothing.</summary>
    public object? SelectTool(object? name)
    {
        var want = name?.ToString();
        if (string.IsNullOrEmpty(want))
            return "error: no item name given";

        var items = Game1.player.Items;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item == null) continue;
            if (string.Equals(item.Name, want, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.DisplayName, want, StringComparison.OrdinalIgnoreCase))
            {
                Game1.player.CurrentToolIndex = i;
                return $"selected {item.DisplayName}";
            }
        }
        return $"error: '{want}' is not in your inventory (see bot.getInventory)";
    }

    /// <summary>Debug: list furniture in the current location as {name, tile, isBed}.</summary>
    public object? ListFurniture()
    {
        try
        {
            var result = new List<object>();
            foreach (var f in Game1.currentLocation.furniture)
            {
                var box = f.GetBoundingBoxAt((int)f.TileLocation.X, (int)f.TileLocation.Y);
                result.Add(new Dictionary<string, object?>
                {
                    ["name"] = f.Name,
                    ["type"] = f.GetType().Name,
                    ["x"] = f.TileLocation.X,
                    ["y"] = f.TileLocation.Y,
                    ["w"] = Math.Max(1, box.Width / 64),
                    ["h"] = Math.Max(1, box.Height / 64),
                    ["isBed"] = f is BedFurniture,
                });
            }
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

    /// <summary>Player's current tile position {x, y} (0-indexed).</summary>
    public object? GetPosition()
    {
        var f = Game1.player;
        if (f == null) return null;
        // Movement is continuous (float pixels). x/y are the tile of the
        // bounding-box CENTER (the game's own Tile property) — the 48x32 box
        // can straddle two tiles, and a stopped player can sit anywhere in
        // between. fx/fy expose the exact float position so the agent can
        // see "a quarter into the field" and correct with short bursts.
        var pos = f.Position;
        return new Dictionary<string, object?>
        {
            ["x"] = f.Tile.X,
            ["y"] = f.Tile.Y,
            ["fx"] = Math.Round(pos.X / 64.0, 2),
            ["fy"] = Math.Round(pos.Y / 64.0, 2),
        };
    }

    /// <summary>Can the player stand on this tile? Checks the game's map-layer
    /// passability PLUS runtime buildings (their footprints are not on the
    /// map layers, so the raw check alone would call a farmhouse "passable").
    /// Still does not account for trees, objects or furniture — those block
    /// movement too.</summary>
    public bool IsTilePassable(object? x, object? y)
    {
        var location = Game1.currentLocation;
        var tx = Convert.ToInt32(x);
        var ty = Convert.ToInt32(y);
        if (!location.isTilePassable(new Vector2(tx, ty)))
            return false;
        // Runtime-placed buildings (farmhouse, sheds, coops…) are colliders
        // the map layers don't know about — mirror the player's real collision.
        var tile = new Vector2(tx, ty);
        foreach (var b in location.buildings)
        {
            if (b.occupiesTile(tile) && !b.isTilePassable(tile))
                return false;
        }
        return true;
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
            if (obj != null) return new Dictionary<string, object> { ["name"] = obj.Name, ["id"] = obj.ItemId };
            // Trees, hoe-dirt, bushes, grass and friends live in terrainFeatures,
            // not objects — check that too.
            if (location.terrainFeatures.TryGetValue(new Vector2(tx, ty), out var tf) && tf != null)
            {
                string name = tf.GetType().Name; // Tree, FruitTree, HoeDirt, Bush, Grass, ...
                return new Dictionary<string, object> { ["name"] = name, ["type"] = name };
            }
            return null;
        }
        catch (Exception ex)
        {
            _monitor.Log($"getTileObject failed: {ex}", LogLevel.Warn);
            return "error: " + ex.Message;
        }
    }

    /// <summary>List every object on the floor of the current location: {name, x, y}.</summary>
    public object? ListObjects()
    {
        var loc = Game1.currentLocation;
        var items = new List<object>();
        foreach (var kv in loc.objects.Pairs)
        {
            if (kv.Value == null) continue;
            items.Add(new Dictionary<string, object>
            {
                ["name"] = kv.Value.Name,
                ["x"] = (int)kv.Key.X,
                ["y"] = (int)kv.Key.Y,
            });
        }
        return items;
    }

    /// <summary>List every terrain feature (trees, weeds, stones, hoe-dirt/crops,
    /// bushes, grass): {name, x, y}. HoeDirt also reports watered and, when a crop
    /// is planted, crop (name) and ready.</summary>
    public object? ListTerrainFeatures()
    {
        var loc = Game1.currentLocation;
        var items = new List<object>();
        foreach (var kv in loc.terrainFeatures.Pairs)
        {
            var tf = kv.Value;
            if (tf == null) continue;
            var entry = new Dictionary<string, object>
            {
                ["name"] = tf.GetType().Name,
                ["x"] = (int)kv.Key.X,
                ["y"] = (int)kv.Key.Y,
            };
            if (tf is StardewValley.TerrainFeatures.HoeDirt dirt)
            {
                entry["watered"] = dirt.isWatered();
                if (dirt.crop != null)
                {
                    entry["crop"] = CropName(dirt.crop);
                    entry["ready"] = dirt.readyForHarvest();
                }
            }
            items.Add(entry);
        }
        return items;
    }

    private static string CropName(Crop crop)
    {
        try { return ItemRegistry.GetData(crop.indexOfHarvest.Value).DisplayName; }
        catch { return crop.indexOfHarvest.Value; }
    }

    /// <summary>List every warp point (doors/exits) in the current location:
    /// {x, y, target, targetX, targetY}.</summary>
    public object? GetWarps()
    {
        var loc = Game1.currentLocation;
        var items = new List<object>();
        foreach (var warp in loc.warps)
        {
            items.Add(new Dictionary<string, object>
            {
                ["x"] = warp.X,
                ["y"] = warp.Y,
                ["target"] = warp.TargetName,
                ["targetX"] = warp.TargetX,
                ["targetY"] = warp.TargetY,
            });
        }
        return items;
    }

    /// <summary>List every NPC currently in the location: {name, x, y}.</summary>
    public object? ListNpcs()
    {
        var loc = Game1.currentLocation;
        var items = new List<object>();
        foreach (var npc in loc.characters)
        {
            if (npc == null) continue;
            items.Add(new Dictionary<string, object>
            {
                ["name"] = npc.Name,
                ["x"] = (int)npc.Position.X,
                ["y"] = (int)npc.Position.Y,
            });
        }
        return items;
    }

    /// <summary>List every farm animal in the location: {name, x, y}.</summary>
    public object? ListAnimals()
    {
        var loc = Game1.currentLocation;
        var items = new List<object>();
        foreach (var animal in loc.animals.Values)
        {
            if (animal == null) continue;
            items.Add(new Dictionary<string, object>
            {
                ["name"] = animal.Name,
                ["x"] = (int)animal.Position.X,
                ["y"] = (int)animal.Position.Y,
            });
        }
        return items;
    }

    /// <summary>Your stamina: {current, max}.</summary>
    public object? GetEnergy()
    {
        var f = Game1.player;
        return new Dictionary<string, object>
        {
            ["current"] = (int)f.stamina,
            ["max"] = (int)f.maxStamina.Value,
        };
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

    /// <summary>Is unread mail waiting in the farmhouse mailbox? True when the
    /// flag on the box is up (Game1.mailbox has letters). The mailbox is a map
    /// action tile on the farmhouse, not an object — no tile probe can see it,
    /// so the response bundle carries this flag instead.</summary>
    public bool HasNewMail()
    {
        return Context.IsWorldReady && Game1.mailbox.Count > 0;
    }

    /// <summary>The farmhouse mailbox: where it is and whether mail waits.
    /// Mirrors the game's own position resolution (farmhouse action tile, else
    /// map default). Interact by standing adjacent, facing it, and tapping A.</summary>
    public object? GetMailbox()
    {
        var p = Game1.player.getMailboxPosition();
        return new Dictionary<string, object?>
        {
            ["x"] = p.X,
            ["y"] = p.Y,
            ["mailWaiting"] = HasNewMail(),
        };
    }

    /// <summary>Take a reference snapshot of the player's inventory, for
    /// <see cref="InventoryDiff"/>. Held items are compared by item id and
    /// stack count.</summary>
    public List<Item> SnapshotInventory()
    {
        return Game1.player.Items.Where(i => i != null && i.Name != "").ToList();
    }

    /// <summary>Items that entered (or grew in) the player's inventory since
    /// <paramref name="before"/>: [{name, count}] of the new/gained amount, or
    /// null when nothing changed. Attached to every script response so the
    /// agent sees what a script gained without re-checking getInventory.</summary>
    public object? InventoryDiff(List<Item>? before)
    {
        var now = SnapshotInventory();
        if (before == null) return null;

        var beforeCounts = new Dictionary<string, int>();
        foreach (var item in before)
            beforeCounts[item.ItemId] = beforeCounts.GetValueOrDefault(item.ItemId) + item.Stack;

        var gained = new List<object>();
        foreach (var group in now.GroupBy(i => i.ItemId))
        {
            var have = group.Sum(i => i.Stack);
            var had = beforeCounts.GetValueOrDefault(group.Key);
            if (have > had)
                gained.Add(new Dictionary<string, object?> { ["name"] = group.First().DisplayName, ["count"] = have - had });
        }
        return gained.Count > 0 ? gained.ToArray() : null;
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

    /// <summary>Which way the player is facing: "up", "right", "down" or
    /// "left" (game values 0-3). Interaction targets the faced tile, so check
    /// this after positioning, not just the position.</summary>
    public object? GetFacing()
    {
        return Game1.player.FacingDirection switch
        {
            0 => "up",
            1 => "right",
            2 => "down",
            3 => "left",
            var d => d.ToString(),
        };
    }

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
        var info = new Dictionary<string, object?>
        {
            ["type"] = menu.GetType().Name,
            ["text"] = menu is DialogueBox db ? db.getCurrentString() : null
        };

        // Letters: the text lives in mailMessage (one string per page), plus
        // the mail title and any attached quest/recipe.
        if (menu is StardewValley.Menus.LetterViewerMenu letter)
        {
            info["text"] = string.Join("\n", letter.mailMessage);
            info["title"] = letter.mailTitle;
            if (!string.IsNullOrEmpty(letter.learnedRecipe))
                info["learnedRecipe"] = letter.learnedRecipe;
            if (!string.IsNullOrEmpty(letter.questID))
                info["quest"] = letter.questID;
        }

        // The tabbed GameMenu (Start button): name the active tab and list them
        // all — LT/RT cycle tabs, B closes the menu.
        if (menu is StardewValley.Menus.GameMenu gm)
        {
            info["tab"] = gm.tabs[gm.currentTab].name;
            info["tabIndex"] = gm.currentTab;
            info["tabs"] = gm.tabs.Select(t => t.name).ToArray();
        }
        return info;
    }

    /// <summary>Get button info for the active menu (if it has clickable components).
    /// When the menu is a DialogueBox, the response text is exposed as the label of
    /// each response component, and an additional <c>responseText</c> array is included.</summary>
    public object? GetMenuButtons()
    {
        var menu = Game1.activeClickableMenu;
        if (menu == null) return null;
        var slotItems = BuildSlotItemMap(menu);

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

        // Enumerate the menu's real component list. The GameMenu (Start button) is
        // a shell: its clickable components live on the active page, so look there
        // — its own list is only the tab bar (IDs 12340+). changeTab() re-populates
        // the page's list on every tab switch, so this stays current.
        List<StardewValley.Menus.ClickableComponent>? comps = null;
        if (menu is StardewValley.Menus.GameMenu gm)
        {
            comps = gm.pages.Count > 0 && gm.currentTab < gm.pages.Count
                ? gm.pages[gm.currentTab].allClickableComponents?.ToList()
                : null;
        }
        else if (menu is StardewValley.Menus.IClickableMenu cm)
        {
            comps = cm.allClickableComponents?.ToList();
        }
        if (comps == null || comps.Count == 0)
        {
            // Old path for menus without a populated component list: probe the
            // low sequential IDs.
            comps = new List<StardewValley.Menus.ClickableComponent>();
            for (int i = 0; i < 32; i++)
            {
                var c = menu.getComponentWithID(i);
                if (c == null) break;
                comps.Add(c);
            }
        }

        var result = new List<object>();
        var seen = new HashSet<int>();
        for (int i = 0; i < comps.Count; i++)
        {
            var comp = comps[i];
            if (comp == null || !seen.Add(comp.myID)) continue;

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

            var entry = new Dictionary<string, object?>
            {
                ["index"] = i,
                ["type"] = comp.GetType().Name,
                ["label"] = label,
                ["visible"] = comp.visible,
                ["myID"] = comp.myID,
                ["bounds"] = bounds
            };

            // Inventory slots: label the component with the item it holds so the
            // inventory screen is readable — a bare "0".."35" slot ID is noise.
            if (slotItems.TryGetValue(comp, out var slot))
            {
                entry["item"] = slot.Item != null
                    ? $"{slot.Item.DisplayName} x{slot.Item.Stack}"
                    : "(empty)";
                entry["whose"] = slot.Whose;
            }

            result.Add(entry);
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

    /// <summary>Map each inventory-slot component to the item it holds, by reference.
    /// Menus can hold two grids with colliding slot IDs (ItemGrabMenu: your inventory
    /// + the chest's contents), so IDs alone can't identify the item — the component
    /// itself can. The player inventory and the grabbed menu (chest) are both
    /// labelled; the inventory grid shows what you hold, the grab menu what the
    /// container holds.</summary>
    private static Dictionary<StardewValley.Menus.ClickableComponent, (Item? Item, string Whose)> BuildSlotItemMap(
        StardewValley.Menus.IClickableMenu menu)
    {
        var map = new Dictionary<StardewValley.Menus.ClickableComponent, (Item? Item, string Whose)>();
        foreach (var (inv, whose) in EnumerateInventoryMenus(menu))
        {
            if (inv.inventory == null || inv.actualInventory == null) continue;
            for (int i = 0; i < inv.inventory.Count && i < inv.actualInventory.Count; i++)
                map[inv.inventory[i]] = (inv.actualInventory[i], whose);
        }
        return map;
    }

    /// <summary>Every InventoryMenu in this menu, tagged with whose items it shows.
    /// MenuWithInventory.inventory is the player's bag; ItemGrabMenu.ItemsToGrabMenu
    /// is the opened container's contents.</summary>
    private static IEnumerable<(StardewValley.Menus.InventoryMenu inv, string whose)>
        EnumerateInventoryMenus(StardewValley.Menus.IClickableMenu menu)
    {
        if (menu is StardewValley.Menus.MenuWithInventory mwi && mwi.inventory != null)
            yield return (mwi.inventory, "player");
        if (menu is StardewValley.Menus.ItemGrabMenu igm && igm.ItemsToGrabMenu != null)
            yield return (igm.ItemsToGrabMenu, "container");
    }

    /// <summary>Is a menu, dialogue or the emote wheel open? Loading and title
    /// screens do not count — those are the save-load path (loadGame waits for
    /// the world through them), not an interactive menu.</summary>
    public bool MenuIsOpen()
    {
        var menu = Game1.activeClickableMenu;
        if (menu == null)
            return Game1.emoteMenu != null;
        return menu is not TitleMenu;
    }

    /// <summary>A readable snapshot of the currently open menu: type, text, tab
    /// info (GameMenu), and every clickable button/slot with its label and item.
    /// Returned when a script ends because a menu opened, so the agent sees the
    /// screen it just caused, without a follow-up probe.</summary>
    public object? MenuSnapshot()
    {
        var t = GetMenuType();
        var b = GetMenuButtons();
        return new Dictionary<string, object?>
        {
            ["menu"] = t,
            ["buttons"] = b,
        };
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
            // 1.6: dialogue advances on a click inside the box; clickAway() is a no-op.
            db.receiveLeftClick(db.xPositionOnScreen + db.width / 2, db.yPositionOnScreen + db.height / 2);
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

        // Set the selected response and click the box (clickAway is a no-op in 1.6).
        db.selectedResponse = idx;
        db.receiveLeftClick(db.xPositionOnScreen + db.width / 2, db.yPositionOnScreen + db.height / 2);
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
                new { name = "bot.tap(name)", description = "Tap a gamepad button and wait one tick. Names: A (action/select/confirm), X (use tool), B (close menu), Start (menu), Y (craft), Back (journal), LT/RT (cycle hotbar item), LB/RB (shift hotbar). While a menu is open, A clicks at the cursor inside that menu, not the faced tile — check getMenuType() before tapping." },
                new { name = "bot.moveLeft(ticks) / bot.moveRight(ticks) / bot.moveUp(ticks) / bot.moveDown(ticks)", description = "Hold the LEFT thumbstick in a direction for `ticks` input ticks. Movement stops automatically when the script ends. Roughly 10-16 ticks per tile." },
                new { name = "bot.cursorLeft(ticks) / bot.cursorRight(ticks) / bot.cursorUp(ticks) / bot.cursorDown(ticks)", description = "Hold the RIGHT thumbstick (the cursor) in a direction for `ticks` ticks. In menus the cursor also scrolls. Keep it short: holding the right stick opens the emote wheel." },
                new { name = "bot.wait(ticks)", description = "Wait for the given number of ticks." },
                new { name = "bot.getPosition()", description = "Your position: x/y are the tile under you (0-indexed); fx/fy are the exact float tile position — movement is continuous, so you can stop between tiles. Aim for integer fx/fy when exact positioning matters." },
                new { name = "bot.isTilePassable(x, y)", description = "Can the player stand on this tile? False for walls and building footprints (incl. runtime buildings like the farmhouse — the mailbox sits on its wall). True does NOT mean walkable: trees, weeds, stones, twigs and furniture also block (check getTileObject / listObjects). Most blockers are removable with a tool (pickaxe: stone, axe: twigs/trees, scythe: weeds): selectTool, face the blocker, tap X." },
                new { name = "bot.getTileObject(x, y)", description = "Get the object or terrain feature at tile (x, y) — placed objects, plus trees, hoe-dirt, bushes and grass — or null." },
                new { name = "bot.listObjects()", description = "List every placed object on the floor of the current location as {name, x, y} — chests, machines, items, gifts, plus ground debris: Stone, Weeds and Twig. (Trees, grass, bushes and hoe-dirt/crops are terrain features — use listTerrainFeatures.)" },
                new { name = "bot.listTerrainFeatures()", description = "List every terrain feature in the current location as {name, x, y}: trees (Tree/FruitTree), grass, bushes, hoe-dirt (planted soil). HoeDirt entries also carry watered, and when a crop is planted: crop (name) and ready (can be harvested). (Stone, Weeds and Twig are objects — use listObjects.)" },
                new { name = "bot.getEnergy()", description = "Your stamina as {current, max}. When it runs low you tire: eat a meal or go to bed." },
                new { name = "bot.getInventory()", description = "List of {name, count, id} for inventory items." },
                new { name = "bot.selectTool(name)", description = "Select the hotbar slot holding the item/tool with this name (case-insensitive), e.g. 'Hoe' or 'Watering Can'. Direct equivalent of LT/RT cycling. Returns 'selected <name>' or an error if not held." },
                new { name = "bot.listFurniture()", description = "All furniture in the current location: {name, type, x, y, w, h, isBed}. w/h are its size in tiles, so you know how many tiles it blocks (a 2x2 TV blocks four tiles)." },
                new { name = "bot.getWarps()", description = "All warp points (doors/exits) in the current location: {x, y, target, targetX, targetY}. Walk onto a warp tile to travel there." },
                new { name = "bot.getMailbox()", description = "The farmhouse mailbox {x, y, mailWaiting}. It is a building tile — invisible to getTileObject — so use this to find it. Stand adjacent, face it, tap A to read mail (opens as a menu)." },
                new { name = "bot.listNpcs()", description = "Every NPC currently in the location: {name, x, y}." },
                new { name = "bot.listAnimals()", description = "Every farm animal in the location: {name, x, y}." },
                new { name = "bot.getTime()", description = "Current in-game time {hour, minute}. The clock runs 06:00-26:00, so early morning hours can be 24 or 25." },
                new { name = "bot.getFacing()", description = "Direction the player faces: \"up\", \"right\", \"down\" or \"left\". Interaction targets the faced tile — check it after positioning." },
                new { name = "bot.getFacing()", description = "Direction the player faces: \"up\", \"right\", \"down\" or \"left\". Interaction targets the faced tile — check it after positioning." },
                new { name = "bot.getDate()", description = "Return {season, day}." },
                new { name = "bot.getMenuType()", description = "Info about the active menu or the emote wheel (type \"EmoteMenu\"), or null. GameMenu (Start) info names the active tab and lists all tabs (LT/RT switch them, B closes)." },
                new { name = "bot.getMenuButtons()", description = "All clickable buttons/slots of the open menu, with label, myID, bounds, and — for inventory slots — the item they hold and whose grid it is (player bag vs container contents)." },
                new { name = "bot.clickMenuButton(index)", description = "Click button at index on the active menu." },
                new { name = "bot.advanceDialogue()", description = "Advance the current dialogue." },
                new { name = "bot.selectDialogueOption(index)", description = "Select a dialogue option by index." },
                new { name = "bot.closeDialogue()", description = "Close the current dialogue." },
                new { name = "bot.print(msg)", description = "Print a message (captured in response)." }
            }
        };
    }
}

