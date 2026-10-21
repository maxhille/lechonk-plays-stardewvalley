using StardewModdingAPI;
using StardewModdingAPI.Events;

namespace Lechonk;

/// <summary>The main entry point for the mod.</summary>
public class ModEntry : Mod
{
    private HttpServer? _httpServer;
    private CoroutineRunner? _runner;
    private ScriptController? _controller;

    /// <inheritdoc />
    public override void Entry(IModHelper helper)
    {
        this.Monitor.Log("Lechonk mod initialising…");

        var botApi = new BotApi(this.Monitor);
        var controller = new ScriptController(botApi, this.Monitor);
        var runner = new CoroutineRunner(controller, this.Monitor);

        // Hook SMAPI events
        helper.Events.GameLoop.UpdateTicked += runner.OnTick;
        helper.Events.Input.ButtonPressed += (_, e) => runner.OnButtonPressed(e.Button);

        this._controller = controller;
        this._runner = runner;

        // Start HTTP server (listener thread starts in constructor)
        this._httpServer = new HttpServer(controller, botApi, this.Monitor);
        this._httpServer.Run();
    }
}
