using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
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

    [Fact]
    public async Task StreamCopilotResponseAsync_can_send_gmail_when_live_test_enabled()
    {
        var liveEnabled = Environment.GetEnvironmentVariable("ENABLE_LIVE_GMAIL_SEND_TEST");
        if (!string.Equals(liveEnabled, "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var token = Environment.GetEnvironmentVariable("COPILOT_TEST_TOKEN");
        Assert.False(string.IsNullOrWhiteSpace(token), "COPILOT_TEST_TOKEN is required when ENABLE_LIVE_GMAIL_SEND_TEST=true.");

        var mcpConfigPath = Environment.GetEnvironmentVariable("COPILOT_TEST_MCP_CONFIG_PATH");
        Assert.False(string.IsNullOrWhiteSpace(mcpConfigPath), "COPILOT_TEST_MCP_CONFIG_PATH is required when ENABLE_LIVE_GMAIL_SEND_TEST=true.");
        Assert.True(File.Exists(mcpConfigPath), $"COPILOT_TEST_MCP_CONFIG_PATH does not exist: {mcpConfigPath}");

        var recipient = Environment.GetEnvironmentVariable("COPILOT_TEST_GMAIL_RECIPIENT");
        if (string.IsNullOrWhiteSpace(recipient))
        {
            recipient = "glenngunnarsson@gmail.com";
        }

        var options = Options.Create(new CopilotOptions
        {
            McpDiscovery = new McpDiscoveryOptions
            {
                Enabled = true,
                IncludeWorkspaceConfig = true,
                IncludeUserConfig = true,
                UserConfigPath = mcpConfigPath
            }
        });

        var environment = new TestHostEnvironment
        {
            ContentRootPath = Directory.GetCurrentDirectory()
        };

        var resolver = new McpServerResolver(options, environment, NullLogger<McpServerResolver>.Instance);
        var resolution = resolver.Resolve();
        Assert.Contains("gmail", resolution.Servers.Keys, StringComparer.OrdinalIgnoreCase);

        Assert.True(
            TryGetGmailArgs(resolution, out var gmailArgs),
            "Gmail MCP server args could not be read from resolved configuration.");
        Assert.True(
            gmailArgs.Contains("-y", StringComparer.OrdinalIgnoreCase)
            || gmailArgs.Contains("--yes", StringComparer.OrdinalIgnoreCase),
            "Gmail MCP uses npx without -y/--yes. In non-interactive test runs this typically prevents MCP startup, so mail is never sent. Add '-y' to gmail args in mcp-config.json.");

        var sessionStore = new CopilotSessionStore(options, NullLogger<CopilotSessionStore>.Instance);
        var client = new CopilotClient(
            options,
            NullLogger<CopilotClient>.Instance,
            sessionStore,
            resolver);

        var nonce = Guid.NewGuid().ToString("N");
        var subject = $"Buddy MCP Live Test {nonce}";

        var sendPrompt = $"""
    Use the Gmail MCP tool to send an email now.
    Recipient: {recipient}
    Subject: {subject}
    Body: This is an automated Buddy integration test. Nonce: {nonce}

    After attempting the send, reply in one line:
    SEND_STATUS:<ok|failed>;MESSAGE_ID:<id-or-none>
    """;

        var deltas = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        var sendResult = await client.StreamCopilotResponseAsync(
            token,
            sendPrompt,
            (chunk, _) =>
            {
                deltas.Add(chunk);
                return Task.CompletedTask;
            },
            cts.Token);

        Assert.False(string.IsNullOrWhiteSpace(sendResult));
        Assert.NotEmpty(deltas);
        Assert.Contains("SEND_STATUS:ok", sendResult, StringComparison.OrdinalIgnoreCase);

        var verifyPrompt = $"""
Use Gmail MCP to verify that an email with subject exactly '{subject}' exists in Sent mail.
If found, return exactly:
VERIFY_STATUS:found;SUBJECT:{subject}
If not found, return exactly:
VERIFY_STATUS:not_found;SUBJECT:{subject}
""";

        var verifyResult = await client.StreamCopilotResponseAsync(
            token,
            verifyPrompt,
            static (_, _) => Task.CompletedTask,
            cts.Token);

        Assert.False(string.IsNullOrWhiteSpace(verifyResult));
        Assert.Contains($"VERIFY_STATUS:found;SUBJECT:{subject}", verifyResult, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StreamCopilotResponseAsync_diagnose_gmail_send_when_live_diag_enabled()
    {
        var diagEnabled = Environment.GetEnvironmentVariable("ENABLE_LIVE_GMAIL_DIAG_TEST");
        if (!string.Equals(diagEnabled, "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var token = Environment.GetEnvironmentVariable("COPILOT_TEST_TOKEN");
        var mcpConfigPath = Environment.GetEnvironmentVariable("COPILOT_TEST_MCP_CONFIG_PATH");
        var recipient = Environment.GetEnvironmentVariable("COPILOT_TEST_GMAIL_RECIPIENT");

        if (string.IsNullOrWhiteSpace(recipient))
        {
            recipient = "glenngunnarsson@gmail.com";
        }

        Assert.False(string.IsNullOrWhiteSpace(token), "COPILOT_TEST_TOKEN is required when ENABLE_LIVE_GMAIL_DIAG_TEST=true.");
        Assert.False(string.IsNullOrWhiteSpace(mcpConfigPath), "COPILOT_TEST_MCP_CONFIG_PATH is required when ENABLE_LIVE_GMAIL_DIAG_TEST=true.");
        Assert.True(File.Exists(mcpConfigPath), $"COPILOT_TEST_MCP_CONFIG_PATH does not exist: {mcpConfigPath}");

        var options = Options.Create(new CopilotOptions
        {
            McpDiscovery = new McpDiscoveryOptions
            {
                Enabled = true,
                IncludeWorkspaceConfig = true,
                IncludeUserConfig = true,
                UserConfigPath = mcpConfigPath
            }
        });

        var environment = new TestHostEnvironment
        {
            ContentRootPath = Directory.GetCurrentDirectory()
        };

        var resolver = new McpServerResolver(options, environment, NullLogger<McpServerResolver>.Instance);
        var resolution = resolver.Resolve();
        Assert.Contains("gmail", resolution.Servers.Keys, StringComparer.OrdinalIgnoreCase);

        Assert.True(
            TryGetGmailArgs(resolution, out var gmailArgs),
            "Gmail MCP server args could not be read from resolved configuration.");

        var diagnostics = new StringBuilder();
        diagnostics.AppendLine("Gmail MCP diagnosis");
        diagnostics.AppendLine($"ConfigPath: {mcpConfigPath}");
        diagnostics.AppendLine($"ResolvedServers: {string.Join(", ", resolution.Servers.Keys.OrderBy(static x => x, StringComparer.OrdinalIgnoreCase))}");
        diagnostics.AppendLine($"GmailArgs: {string.Join(" ", gmailArgs)}");
        diagnostics.AppendLine($"Recipient: {recipient}");

        var sessionStore = new CopilotSessionStore(options, NullLogger<CopilotSessionStore>.Instance);
        var client = new CopilotClient(
            options,
            NullLogger<CopilotClient>.Instance,
            sessionStore,
            resolver);

        var nonce = Guid.NewGuid().ToString("N");
        var subject = $"Buddy Gmail DIAG {nonce}";

        var preflightPrompt = """
Use Gmail MCP only.
Check that Gmail access works by listing labels.
Respond exactly in one line:
GMAIL_PREFLIGHT:<ok|failed>;DETAIL:<short>
""";

        string preflightResult;
        try
        {
            preflightResult = await client.StreamCopilotResponseAsync(
                token!,
                preflightPrompt,
                static (_, _) => Task.CompletedTask,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            Assert.True(false, $"{diagnostics}\nPreflight exception: {ex}");
            return;
        }

        diagnostics.AppendLine($"PreflightResult: {preflightResult}");

        var sendPrompt = $"""
Use Gmail MCP only.
Send an email now.
To: {recipient}
Subject: {subject}
Body: Gmail DIAG nonce {nonce}

Respond exactly in one line:
GMAIL_SEND:<ok|failed>;DETAIL:<short>
""";

        string sendResult;
        try
        {
            sendResult = await client.StreamCopilotResponseAsync(
                token!,
                sendPrompt,
                static (_, _) => Task.CompletedTask,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            Assert.True(false, $"{diagnostics}\nSend exception: {ex}");
            return;
        }

        diagnostics.AppendLine($"SendResult: {sendResult}");

        var verifyPrompt = $"""
Use Gmail MCP only.
Look in Sent mailbox for a message with subject exactly '{subject}'.
Respond exactly in one line:
GMAIL_VERIFY:<found|not_found|failed>;DETAIL:<short>
""";

        string verifyResult;
        try
        {
            verifyResult = await client.StreamCopilotResponseAsync(
                token!,
                verifyPrompt,
                static (_, _) => Task.CompletedTask,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            Assert.True(false, $"{diagnostics}\nVerify exception: {ex}");
            return;
        }

        diagnostics.AppendLine($"VerifyResult: {verifyResult}");

        var success = preflightResult.Contains("GMAIL_PREFLIGHT:ok", StringComparison.OrdinalIgnoreCase)
            && sendResult.Contains("GMAIL_SEND:ok", StringComparison.OrdinalIgnoreCase)
            && verifyResult.Contains("GMAIL_VERIFY:found", StringComparison.OrdinalIgnoreCase);

        Assert.True(success, diagnostics.ToString());
    }

    private static bool TryGetGmailArgs(McpServerResolution resolution, out string[] args)
    {
        args = [];
        if (!resolution.Servers.TryGetValue("gmail", out var rawServer))
        {
            return false;
        }

        if (rawServer is not JsonElement serverElement ||
            serverElement.ValueKind != JsonValueKind.Object ||
            !serverElement.TryGetProperty("args", out var argsElement) ||
            argsElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        args = argsElement
            .EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!.Trim())
            .ToArray();

        return args.Length > 0;
    }

    private sealed class TestMcpServerResolver : IMcpServerResolver
    {
        public McpServerResolution Resolve()
        {
            return new McpServerResolution(new Dictionary<string, object>(), null, []);
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";

        public string ApplicationName { get; set; } = "IntegrationTests";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
