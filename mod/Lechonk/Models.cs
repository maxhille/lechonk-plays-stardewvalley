namespace Lechonk;

/// <summary>Request body for POST /script.</summary>
public record ScriptRequest(string Script, int? TickBudget = null);

/// <summary>Response body for POST /script (returned immediately with scriptId).</summary>
public class ScriptResponse
{
    public int? ScriptId { get; init; }
    public string Prints { get; init; } = "";
    public object? Result { get; init; }
    public string? Error { get; init; }
    public int TicksUsed { get; init; }
    public string Event { get; init; } = "completed";
}
