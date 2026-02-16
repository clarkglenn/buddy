using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Server.Options;
using Server.Services;
using Xunit;

namespace IntegrationTests;

public sealed class CopilotClientIntegrationTests
{
    [Fact]
    public async Task StreamCopilotResponseAsync_returns_content()
    {
        var token = Environment.GetEnvironmentVariable("COPILOT_TEST_TOKEN");
        var model = Environment.GetEnvironmentVariable("COPILOT_TEST_MODEL");

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        var options = Options.Create(new CopilotOptions
        {
            Model = model
        });

        var sessionStore = new CopilotSessionStore(options, NullLogger<CopilotSessionStore>.Instance);
        var client = new CopilotClient(
            options,
            NullLogger<CopilotClient>.Instance,
            sessionStore,
            new TestMcpServerResolver());

        var deltas = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await client.StreamCopilotResponseAsync(
            token,
            "Reply with the single word: OK.",
            (chunk, _) =>
            {
                deltas.Add(chunk);
                return Task.CompletedTask;
            },
            cts.Token);

        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.NotEmpty(deltas);
    }

    private sealed class TestMcpServerResolver : IMcpServerResolver
    {
        public McpServerResolution Resolve()
        {
            return new McpServerResolution(new Dictionary<string, object>(), null);
        }
    }
}
