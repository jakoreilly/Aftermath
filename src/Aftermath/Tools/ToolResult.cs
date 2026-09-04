namespace Aftermath.Tools;

/// <summary>The single result shape every tool in this server returns. Modelled on
/// <c>Acme.ClaudeDb.Contracts.ToolResult</c>.</summary>
public sealed record ToolResult
{
    public bool Success { get; init; }

    /// <summary>Machine-readable code on failure; null on success.</summary>
    public string? Error { get; init; }

    /// <summary>Human/model-readable explanation. On failure, says what to do next.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Payload on success; null on failure.</summary>
    public object? Data { get; init; }

    public static ToolResult Fail(string error, string message) =>
        new() { Success = false, Error = error, Message = message };

    public static ToolResult Ok(object? data, string message) =>
        new() { Success = true, Message = message, Data = data };
}
