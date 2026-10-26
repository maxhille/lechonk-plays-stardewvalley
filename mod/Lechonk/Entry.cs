using System;
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
        try
        {
            this.Monitor.Log("Lechonk mod initialising…");

            // Never let a managed fault from this mod silently kill the process.
            // Capture the full stack trace to the log so a crash shows *what* faulted,
            // and keep the process alive where SMAPI can recover.
            try
            {
                AppDomain.CurrentDomain.UnhandledException += (s, a) =>
                {
                    var ex = a.ExceptionObject as Exception;
                    var typeName = ex?.GetType().Name ?? a.ExceptionObject?.ToString() ?? "unknown";
                    this.Monitor.Log($"UNHANDLED-APP: type={typeName} | msg={ex?.Message}", LogLevel.Error);
                    this.Monitor.Log(ex?.ToString(), LogLevel.Error);
                };
                // Fallback for faults that escape the per-hook try/catch. The
                // AppDomain handler fires on any thread; it captures the full stack
                // trace so a crash shows exactly what faulted instead of killing
                // the process silently.
            }
            catch (Exception he)
            {
                this.Monitor.Log($"LECHONK: exception handlers failed to install: {he}", LogLevel.Error);
            }

            var botApi = new BotApi(helper, this.Monitor);
            var inputGuard = new InputGuard(helper, this.Monitor);
            botApi.InputGuard = inputGuard;
            var controller = new ScriptController(botApi, this.Monitor);
            var runner = new CoroutineRunner(controller, inputGuard, this.Monitor);

            // Hook SMAPI events
            helper.Events.GameLoop.UpdateTicked += runner.OnTick;
            helper.Events.Input.ButtonPressed += (_, e) => runner.OnButtonPressed(e.Button);

            this._controller = controller;
            this._runner = runner;

            // Defer HTTP server startup until after the game has finished
            // initialising graphics. Starting a background TcpListener thread
            // during Entry() races with SDL2/MonoGame native init on .NET 6
            // Linux and crashes with PAL_SEHException (JIT compiler bug).
            this._httpServer = new HttpServer(controller, botApi, this.Monitor);
            helper.Events.GameLoop.GameLaunched += (_, _) =>
            {
                try
                {
                    this._httpServer?.Run();
                    this.Monitor.Log("Lechonk HTTP server started after game launch.", LogLevel.Info);
                }
                catch (Exception ex)
                {
                    this.Monitor.Log($"Lechonk HTTP server failed to start: {ex}", LogLevel.Error);
                }
            };

            this.Monitor.Log("Lechonk mod initialised successfully.", LogLevel.Info);
        }
        catch (Exception ex)
        {
            this.Monitor.Log($"Lechonk mod initialisation failed: {ex}", LogLevel.Error);
        }
    }
}
