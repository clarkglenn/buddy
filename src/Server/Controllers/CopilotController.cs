using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Server.Hubs;
using Server.Services;
using Shared.Models;

namespace Server.Controllers;

[ApiController]
[Route("api/copilot")]
public sealed class CopilotController : ControllerBase
{
    private readonly IGitHubTokenStore _tokenStore;
    private readonly CopilotClient _copilotClient;
    private readonly IHubContext<CopilotHub> _hubContext;
    private readonly ILogger<CopilotController> _logger;

    public CopilotController(
        IGitHubTokenStore tokenStore,
        CopilotClient copilotClient,
        IHubContext<CopilotHub> hubContext,
        ILogger<CopilotController> logger)
    {
        _tokenStore = tokenStore;
        _copilotClient = copilotClient;
        _hubContext = hubContext;
        _logger = logger;
    }

    [HttpGet("models")]
    public async Task<ActionResult<IReadOnlyList<string>>> GetModels(CancellationToken cancellationToken)
    {
        var token = await _tokenStore.GetTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return Unauthorized();
        }

        var models = await _copilotClient.GetAvailableModelsAsync(token, cancellationToken);
        return Ok(models);
    }

    [HttpPost("run")]
    public async Task<ActionResult<CopilotRunResponse>> Run(
        [FromBody] CopilotRunRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            return BadRequest("Prompt is required.");
        }

        var token = await _tokenStore.GetTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return Unauthorized();
        }

        var runId = string.IsNullOrWhiteSpace(request.RunId)
            ? Guid.NewGuid().ToString("N")
            : request.RunId;
        _ = Task.Run(() => StreamRunAsync(runId, request.Prompt, token), CancellationToken.None);

        return Ok(new CopilotRunResponse(runId));
    }

    private async Task StreamRunAsync(string runId, string prompt, string token)
    {
        try
        {
            var startedEvent = new CopilotStreamEvent(
                runId,
                "thinking",
                "Starting Copilot request...",
                DateTimeOffset.UtcNow);

            await _hubContext.Clients.Group(runId).SendAsync("thinking", startedEvent);

            var result = await _copilotClient.StreamCopilotResponseAsync(
                token,
                prompt,
                async (chunk, cancellationToken) =>
                {
                    // Separate thinking chunks (prefixed with [THINKING]) from result chunks
                    if (chunk.StartsWith("[THINKING] "))
                    {
                        return;
                    }

                    var resultEvent = new CopilotStreamEvent(
                        runId,
                        "thinking",  // Use "thinking" to indicate intermediate output
                        chunk,
                        DateTimeOffset.UtcNow);

                    await _hubContext.Clients.Group(runId).SendAsync("thinking", resultEvent, cancellationToken);
                },
                CancellationToken.None,
                null,
                null);

            var resultEvent = new CopilotStreamEvent(
                runId,
                "result",
                result,
                DateTimeOffset.UtcNow);

            await _hubContext.Clients.Group(runId).SendAsync("result", resultEvent);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Copilot run failed for {RunId}", runId);

            var errorEvent = new CopilotStreamEvent(
                runId,
                "error",
                exception.Message,
                DateTimeOffset.UtcNow);

            await _hubContext.Clients.Group(runId).SendAsync("error", errorEvent);
        }
    }
}
