using System.Collections.Generic;
using System.Diagnostics;
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
    public async Task CopilotSessionEntry_DisposeAsync_kills_running_process()
    {
        var process = StartLongRunningProcess();

        var entry = new CopilotSessionEntry
        {
            CliProcess = process
        };

        await entry.DisposeAsync();

        Assert.Null(entry.CliProcess);
    }

    [Fact]
    public async Task CopilotSessionStore_RemoveAsync_disposes_session_process()
    {
        var options = Options.Create(new CopilotOptions());
        var store = new CopilotSessionStore(options, NullLogger<CopilotSessionStore>.Instance);

        var process = StartLongRunningProcess();

        var entry = new CopilotSessionEntry
        {
            CliProcess = process
        };

        _ = await store.GetOrCreateAsync("session-with-process", _ => Task.FromResult(entry), CancellationToken.None);
        await store.RemoveAsync("session-with-process", CancellationToken.None);

        Assert.Null(entry.CliProcess);
    }

    private static Process StartLongRunningProcess()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = "-NoProfile -Command Start-Sleep -Seconds 30",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        var started = process.Start();
        Assert.True(started);
        return process;
    }
}
