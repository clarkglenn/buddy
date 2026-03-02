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

        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        var options = Options.Create(new CopilotOptions());

        var sessionStore = new CopilotSessionStore(options, NullLogger<CopilotSessionStore>.Instance);
        var client = new CopilotClient(
            options,
            NullLogger<CopilotClient>.Instance,
            sessionStore);

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

    [Fact]
    public async Task CopilotSessionEntry_DisposeAsync_disposes_gate()
    {
        var entry = new CopilotSessionEntry();
        await entry.DisposeAsync();

        // Gate should be disposed — attempting to wait on it should throw.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => entry.Gate.WaitAsync());
    }

    [Fact]
    public async Task CopilotSessionStore_RemoveAsync_disposes_session()
    {
        var options = Options.Create(new CopilotOptions());
        var store = new CopilotSessionStore(options, NullLogger<CopilotSessionStore>.Instance);

        var entry = new CopilotSessionEntry();

        _ = await store.GetOrCreateAsync("session-key", _ => Task.FromResult(entry), CancellationToken.None);
        await store.RemoveAsync("session-key", CancellationToken.None);

        // Entry should have been disposed via RemoveAsync.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => entry.Gate.WaitAsync());
    }

    [Fact]
    public async Task ResumedSession_MultiTurn_MaintainsContext()
    {
        var token = Environment.GetEnvironmentVariable("COPILOT_TEST_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        var options = Options.Create(new CopilotOptions
        {
            Cli = new CopilotCliOptions { ReuseProcessPerSession = true }
        });

        var sessionStore = new CopilotSessionStore(options, NullLogger<CopilotSessionStore>.Instance);
        var client = new CopilotClient(
            options,
            NullLogger<CopilotClient>.Instance,
            sessionStore);

        var context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["channel"] = "test-channel",
            ["thread_ts"] = "resumed-multi-turn"
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // First request.
        var result1 = await client.StreamCopilotResponseAsync(
            token,
            "Reply with the single word: FIRST.",
            (_, _) => Task.CompletedTask,
            cts.Token,
            context: context,
            conversationUserKey: "test-user");

        Assert.False(string.IsNullOrWhiteSpace(result1));

        // Second request — uses --resume to maintain session context.
        var result2 = await client.StreamCopilotResponseAsync(
            token,
            "Reply with the single word: SECOND.",
            (_, _) => Task.CompletedTask,
            cts.Token,
            context: context,
            conversationUserKey: "test-user");

        Assert.False(string.IsNullOrWhiteSpace(result2));

        // Clean up.
        await sessionStore.RemoveAsync("slack:test-channel:resumed-multi-turn", CancellationToken.None);
    }
}
