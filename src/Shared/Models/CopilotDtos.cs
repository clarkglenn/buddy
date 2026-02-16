namespace Shared.Models;

public sealed record CopilotRunRequest(string Prompt, string? RunId = null);

public sealed record CopilotRunResponse(string RunId);

public sealed record CopilotStreamEvent(
    string RunId,
    string Type,
    string Content,
    DateTimeOffset Timestamp);
