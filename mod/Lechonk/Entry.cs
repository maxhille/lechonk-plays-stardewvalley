using System;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

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
            var controller = new ScriptController(botApi, this.Monitor);
            var runner = new CoroutineRunner(controller, this.Monitor);

            // Hook SMAPI events
            helper.Events.GameLoop.UpdateTicked += runner.OnTick;
            helper.Events.Input.ButtonPressed += (_, e) => runner.OnButtonPressed(e.Button);

            // The game flaps its gamepad "connected" flag between ticks: SMAPI rebuilds
            // the pad from the *real* (absent) device when no override is active, which is
            // IsConnected == false, then a fresh override re-connects it. To keep the pad
            // stably connected (and stop the "gamepad connected/disconnected" toasts),
            // enqueue a controller-only seed button every tick. A trigger has no keyboard
            // binding and no in-game side effect, so it only forces the builder to re-seed
            // the state (IsConnected == true) with zero input side effects.
            helper.Events.GameLoop.UpdateTicked += (_, _) =>
                helper.Input.Press(SButton.RightTrigger);

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
                    // Enable gamepad controls ONCE, at the title screen, so the
                    // game reads the controller state as the input source. The
                    // Bot API then drives movement (left stick) and the cursor
                    // (right stick) + action (A). Setting it here, on the title
                    // screen, avoids the fault of toggling it mid save-load.
                    if (!Game1.options.gamepadControls)
                    {
                        Game1.options.gamepadControls = true;
                        this.Monitor.Log("Lechonk: gamepad controls enabled (left stick = move, right stick = cursor, A = action).", LogLevel.Info);
                    }

                    this._httpServer?.Start();
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
