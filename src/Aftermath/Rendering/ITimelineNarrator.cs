namespace Aftermath.Rendering;

using Aftermath.Correlation;

/// <summary>
/// The seam the AI plugs into later (Goal G) without this tool ever depending on one. Ship
/// exactly one implementation now — <see cref="TemplateNarrator"/>, no model, no network, no
/// nondeterminism — and it stays the default. Phase 6 gives an LLM host a different route in
/// entirely, through MCP tool results; if verdict 1 (plan.md, Scope decisions) ever needs
/// revisiting, an HttpNarrator drops in here without touching collection, correlation or
/// rendering.
/// </summary>
public interface ITimelineNarrator
{
    Task<string> NarrateAsync(Timeline timeline, CancellationToken ct);
}
