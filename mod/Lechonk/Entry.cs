using StardewModdingAPI;

namespace Lechonk;

/// <summary>The main entry point for the mod.</summary>
public class ModEntry : Mod
{
    /// <summary>The mod entry point, called after the mod is first loaded.</summary>
    /// <param name="helper">Provides simplified APIs for writing mods.</param>
    public override void Entry(IModHelper helper)
    {
        this.Monitor.Log("Lechonk mod loaded.");
    }
}
