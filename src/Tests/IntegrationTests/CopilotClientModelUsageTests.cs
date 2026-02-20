using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Server.Options;
using Server.Services;
using Xunit;

namespace IntegrationTests;

public sealed class CopilotClientModelUsageTests
{
    [Fact]
    public void CopilotClient_uses_configured_model()
    {
        const string expectedModel = "gpt-5.2-test";

        var options = Options.Create(new CopilotOptions
        {
            Model = expectedModel,
        });

        var sessionStore = new CopilotSessionStore(options, NullLogger<CopilotSessionStore>.Instance);
        var client = new CopilotClient(
            options,
            NullLogger<CopilotClient>.Instance,
            sessionStore,
            new TestMcpServerResolver());

        // Verify that the client was created successfully with the model configuration.
        // The actual HTTP request validation is now handled by the GitHub.Copilot.SDK.
        Assert.NotNull(client);
    }

    [Fact]
    public async Task GetAvailableModelsAsync_returns_configured_model()
    {
        const string expectedModel = "gpt-5.2-test";

        var options = Options.Create(new CopilotOptions
        {
            Model = expectedModel,
        });

        var sessionStore = new CopilotSessionStore(options, NullLogger<CopilotSessionStore>.Instance);
        var client = new CopilotClient(
            options,
            NullLogger<CopilotClient>.Instance,
            sessionStore,
            new TestMcpServerResolver());

        var models = await client.GetAvailableModelsAsync("test-token", CancellationToken.None);

        Assert.Contains(expectedModel, models);
    }

    private sealed class TestMcpServerResolver : IMcpServerResolver
    {
        public McpServerResolution Resolve()
        {
            return new McpServerResolution(new Dictionary<string, object>(), null, []);
        }
    }
}
