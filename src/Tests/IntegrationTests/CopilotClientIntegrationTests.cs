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
        var options = Options.Create(new CopilotOptions());
        var acpHost = new FakeCopilotAcpHost();

        var sessionStore = new CopilotSessionStore(options, NullLogger<CopilotSessionStore>.Instance);
        var client = new CopilotClient(
            options,
            NullLogger<CopilotClient>.Instance,
            sessionStore,
            acpHost);

        var deltas = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await client.StreamCopilotResponseAsync(
            string.Empty,
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
    public async Task SameThread_reuses_single_acp_session()
    {
        var options = Options.Create(new CopilotOptions());
        var acpHost = new FakeCopilotAcpHost();

        var sessionStore = new CopilotSessionStore(options, NullLogger<CopilotSessionStore>.Instance);
        var client = new CopilotClient(
            options,
            NullLogger<CopilotClient>.Instance,
            sessionStore,
            acpHost);

        var context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["channel"] = "test-channel",
            ["thread_ts"] = "thread-1"
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result1 = await client.StreamCopilotResponseAsync(
            string.Empty,
            "Reply with the single word: FIRST.",
            (_, _) => Task.CompletedTask,
            cts.Token,
            context: context,
            conversationUserKey: "test-user");

        Assert.False(string.IsNullOrWhiteSpace(result1));

        var result2 = await client.StreamCopilotResponseAsync(
            string.Empty,
            "Reply with the single word: SECOND.",
            (_, _) => Task.CompletedTask,
            cts.Token,
            context: context,
            conversationUserKey: "test-user");

        Assert.False(string.IsNullOrWhiteSpace(result2));
        Assert.Equal(1, acpHost.CreateSessionCount);
        Assert.Equal(2, acpHost.PromptCount);
    }

    [Fact]
    public async Task NewGeneration_creates_new_acp_session()
    {
        var options = Options.Create(new CopilotOptions());
        var acpHost = new FakeCopilotAcpHost();
        var sessionStore = new CopilotSessionStore(options, NullLogger<CopilotSessionStore>.Instance);
        var client = new CopilotClient(
            options,
            NullLogger<CopilotClient>.Instance,
            sessionStore,
            acpHost);

        var context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["channel"] = "test-channel",
            ["thread_ts"] = "thread-2"
        };

        await client.StreamCopilotResponseAsync(
            string.Empty,
            "First prompt",
            (_, _) => Task.CompletedTask,
            CancellationToken.None,
            context,
            "test-user");

        acpHost.AdvanceGeneration();

        await client.StreamCopilotResponseAsync(
            string.Empty,
            "Second prompt",
            (_, _) => Task.CompletedTask,
            CancellationToken.None,
            context,
            "test-user");

        Assert.Equal(2, acpHost.CreateSessionCount);
    }

    private sealed class FakeCopilotAcpHost : ICopilotAcpHost
    {
        private int _sessionCounter;

        public long Generation { get; private set; } = 1;

        public int CreateSessionCount { get; private set; }

        public int PromptCount { get; private set; }

        public Task<string> CreateSessionAsync(string workingDirectory, CancellationToken cancellationToken)
        {
            CreateSessionCount++;
            _sessionCounter++;
            return Task.FromResult($"session-{_sessionCounter}");
        }

        public async Task<CopilotAcpPromptResult> PromptSessionAsync(
            string sessionId,
            string prompt,
            Func<CopilotAcpUpdate, CancellationToken, Task> onUpdate,
            CancellationToken cancellationToken)
        {
            PromptCount++;
            await onUpdate(new CopilotAcpUpdate($"response-from-{sessionId}", false, false), cancellationToken);
            return new CopilotAcpPromptResult("end_turn");
        }

        public void AdvanceGeneration()
        {
            Generation++;
        }
    }
}
