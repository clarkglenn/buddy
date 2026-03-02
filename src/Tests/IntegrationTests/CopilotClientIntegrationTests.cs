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

    [Fact]
    public async Task PersistentProcess_MultiTurn_ReusesProcess()
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
            ["thread_ts"] = "persistent-multi-turn"
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // First request — should spawn a new persistent process.
        var result1 = await client.StreamCopilotResponseAsync(
            token,
            "Reply with the single word: FIRST.",
            (_, _) => Task.CompletedTask,
            cts.Token,
            context: context,
            conversationUserKey: "test-user");

        Assert.False(string.IsNullOrWhiteSpace(result1));

        // Grab the session entry and capture the PID.
        var entry = await sessionStore.GetOrCreateAsync(
            "slack:test-channel:persistent-multi-turn",
            _ => Task.FromResult(new CopilotSessionEntry()),
            CancellationToken.None);

        Assert.NotNull(entry.CliProcess);
        Assert.True(entry.IsPersistent);
        var firstPid = entry.CliProcess!.Id;

        // Second request — should reuse the same process.
        entry.Gate.Release(); // Release gate acquired by GetOrCreateAsync indirectly; re-acquire via client call.

        var result2 = await client.StreamCopilotResponseAsync(
            token,
            "Reply with the single word: SECOND.",
            (_, _) => Task.CompletedTask,
            cts.Token,
            context: context,
            conversationUserKey: "test-user");

        Assert.False(string.IsNullOrWhiteSpace(result2));

        // Verify same PID — process was reused, not restarted.
        Assert.NotNull(entry.CliProcess);
        Assert.Equal(firstPid, entry.CliProcess!.Id);

        // Clean up.
        await sessionStore.RemoveAsync("slack:test-channel:persistent-multi-turn", CancellationToken.None);
    }

    [Fact]
    public async Task PersistentProcess_CrashRecovery_FallsBackToOneShot()
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
            ["thread_ts"] = "persistent-crash-recovery"
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // First request — spawns a persistent process.
        var result1 = await client.StreamCopilotResponseAsync(
            token,
            "Reply with the single word: ALIVE.",
            (_, _) => Task.CompletedTask,
            cts.Token,
            context: context,
            conversationUserKey: "test-user");

        Assert.False(string.IsNullOrWhiteSpace(result1));

        // Grab the entry and kill the process to simulate a crash.
        var entry = await sessionStore.GetOrCreateAsync(
            "slack:test-channel:persistent-crash-recovery",
            _ => Task.FromResult(new CopilotSessionEntry()),
            CancellationToken.None);

        Assert.NotNull(entry.CliProcess);
        entry.CliProcess!.Kill(entireProcessTree: true);
        await entry.CliProcess.WaitForExitAsync();

        entry.Gate.Release(); // Release gate so next request can proceed.

        // Second request — persistent process is dead, should fall back to one-shot with --resume.
        var result2 = await client.StreamCopilotResponseAsync(
            token,
            "Reply with the single word: RECOVERED.",
            (_, _) => Task.CompletedTask,
            cts.Token,
            context: context,
            conversationUserKey: "test-user");

        Assert.False(string.IsNullOrWhiteSpace(result2));

        // Clean up.
        await sessionStore.RemoveAsync("slack:test-channel:persistent-crash-recovery", CancellationToken.None);
    }

    [Fact]
    public async Task PersistentProcess_DisposeAsync_CleansUpAllResources()
    {
        var process = StartLongRunningProcess();

        var entry = new CopilotSessionEntry
        {
            CliProcess = process,
            StdinWriter = process.StandardInput,
            IsPersistent = true,
            StderrPumpTask = Task.Run(async () =>
            {
                try { while (await process.StandardError.ReadLineAsync() != null) { } }
                catch { }
            })
        };

        await entry.DisposeAsync();

        Assert.Null(entry.CliProcess);
        Assert.Null(entry.StdinWriter);
        Assert.Null(entry.StderrPumpTask);
        Assert.False(entry.IsPersistent);
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
