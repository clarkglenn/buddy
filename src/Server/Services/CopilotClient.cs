using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Server.Options;

namespace Server.Services;

public sealed class CopilotClient
{
    private readonly CopilotOptions _options;
    private readonly ILogger<CopilotClient> _logger;
    private readonly ICopilotSessionStore _sessionStore;
    private readonly IMcpServerResolver _mcpServerResolver;

    private const string ChannelContextKey = "channel";
    private const string ThreadContextKey = "thread_ts";
    private const string CopilotAllowAllEnvVar = "COPILOT_ALLOW_ALL";

    public CopilotClient(
        IOptions<CopilotOptions> options,
        ILogger<CopilotClient> logger,
        ICopilotSessionStore sessionStore,
        IMcpServerResolver mcpServerResolver)
    {
        _options = options.Value;
        _logger = logger;
        _sessionStore = sessionStore;
        _mcpServerResolver = mcpServerResolver;
    }

    public async Task<string> StreamCopilotResponseAsync(
        string token,
        string prompt,
        Func<string, CancellationToken, Task> onDelta,
        CancellationToken cancellationToken,
        Dictionary<string, string>? context = null,
        string? conversationUserKey = null)
    {
        if (string.IsNullOrWhiteSpace(_options.Cli.Command))
        {
            throw new InvalidOperationException("Copilot CLI command is not configured (Copilot:Cli:Command).");
        }

        if (!string.IsNullOrWhiteSpace(token))
        {
            _logger.LogDebug("Copilot token was provided but is ignored in CLI mode; machine-level CLI auth is used.");
        }

        var conversationKey = BuildConversationKey(context, conversationUserKey);

        if (string.IsNullOrWhiteSpace(conversationKey))
        {
            var ephemeralEntry = new CopilotSessionEntry();
            return await StreamWithSessionAsync(ephemeralEntry, prompt, onDelta, cancellationToken, persistConversation: false);
        }

        var entry = await _sessionStore.GetOrCreateAsync(
            conversationKey,
            static _ => Task.FromResult(new CopilotSessionEntry()),
            cancellationToken);

        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            var response = await StreamWithSessionAsync(entry, prompt, onDelta, cancellationToken, persistConversation: true);

            if (entry.IsFaulted)
            {
                await _sessionStore.RemoveAsync(conversationKey, cancellationToken);
            }

            return response;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private string BuildPrompt(string prompt)
    {
        return prompt;
    }

    private string BuildPromptWithHistory(CopilotSessionEntry entry, string prompt)
    {
        var basePrompt = BuildPrompt(prompt);

        if (entry.ConversationHistory.Count == 0)
        {
            return basePrompt;
        }

        var history = new StringBuilder();
        history.AppendLine("Conversation context from earlier turns:");

        foreach (var turn in entry.ConversationHistory)
        {
            history.AppendLine("User:");
            history.AppendLine(turn.UserPrompt);
            history.AppendLine("Assistant:");
            history.AppendLine(turn.AssistantResponse);
            history.AppendLine();
        }

        history.AppendLine("Current user prompt:");
        history.AppendLine(basePrompt);
        return history.ToString();
    }

    private string? BuildConversationKey(Dictionary<string, string>? context, string? conversationUserKey)
    {
        if (context == null)
        {
            return null;
        }

        if (!context.TryGetValue(ChannelContextKey, out var channel) || string.IsNullOrWhiteSpace(channel))
        {
            return null;
        }

        if (context.TryGetValue(ThreadContextKey, out var threadTs) && !string.IsNullOrWhiteSpace(threadTs))
        {
            return $"slack:{channel}:{threadTs}";
        }

        if (!string.IsNullOrWhiteSpace(conversationUserKey))
        {
            return $"slack:{channel}:{conversationUserKey}";
        }

        return null;
    }

    private async Task<string> StreamWithSessionAsync(
        CopilotSessionEntry entry,
        string prompt,
        Func<string, CancellationToken, Task> onDelta,
        CancellationToken cancellationToken,
        bool persistConversation)
    {
        var buffer = new StringBuilder();
        var requestState = new CopilotRequestState(buffer, onDelta);
        entry.CurrentRequest = requestState;

        try
        {
            var enrichedPrompt = BuildPromptWithHistory(entry, prompt);
            await ExecuteCliRequestAsync(enrichedPrompt, requestState, cancellationToken);

            var response = buffer.ToString();

            if (persistConversation && !string.IsNullOrWhiteSpace(response))
            {
                entry.AddTurn(prompt, response, _options.Cli.MaxConversationTurns);
            }

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Copilot CLI streaming");
            entry.MarkFaulted();
            throw;
        }
        finally
        {
            entry.CurrentRequest = null;
            entry.Touch();
        }
    }

    private async Task ExecuteCliRequestAsync(string prompt, CopilotRequestState requestState, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.Cli.Command,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.CurrentDirectory
        };

        foreach (var arg in _options.Cli.Arguments)
        {
            if (!string.IsNullOrWhiteSpace(arg))
            {
                startInfo.ArgumentList.Add(arg);
            }
        }

        startInfo.ArgumentList.Add("--allow-all-tools");

        ApplyCliEnvironment(startInfo);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start Copilot CLI process.");
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to launch Copilot CLI command '{_options.Cli.Command}'. Ensure Copilot CLI is installed and available on PATH.", ex);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_options.Cli.ResponseTimeoutSeconds > 0)
        {
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.Cli.ResponseTimeoutSeconds));
        }

        var stdoutTask = PumpStdoutAsync(process, requestState, timeoutCts.Token);
        var stderrTask = ReadAllStderrAsync(process, timeoutCts.Token);

        await process.StandardInput.WriteAsync(prompt.AsMemory(), timeoutCts.Token);
        await process.StandardInput.WriteLineAsync();
        process.StandardInput.Close();

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync(timeoutCts.Token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            SafeKill(process);
            throw new TimeoutException($"Copilot CLI response timed out after {_options.Cli.ResponseTimeoutSeconds} seconds.");
        }
        catch (OperationCanceledException)
        {
            SafeKill(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            var stderr = await stderrTask;
            var message = BuildCliFailureMessage(process.ExitCode, stderr);
            throw new InvalidOperationException(message);
        }

        var output = requestState.Buffer.ToString();
        if (string.IsNullOrWhiteSpace(output))
        {
            var stderr = await stderrTask;
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                throw new InvalidOperationException($"Copilot CLI returned no output. STDERR: {stderr}");
            }
        }
    }

    private async Task PumpStdoutAsync(Process process, CopilotRequestState requestState, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line == null)
            {
                break;
            }

            await HandleCliOutputLineAsync(line, requestState, cancellationToken);
        }
    }

    private async Task HandleCliOutputLineAsync(string line, CopilotRequestState requestState, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var result = TryParseCliEvent(line);

        if (result.ToolUsed)
        {
            requestState.ToolUsed = true;
        }

        if (string.IsNullOrWhiteSpace(result.Content))
        {
            return;
        }

        var content = result.Content;
        if (result.IsThinking)
        {
            await requestState.OnDelta($"[THINKING] {content}", cancellationToken);
            return;
        }

        if (requestState.Buffer.Length > 0 && line[0] != '{')
        {
            requestState.Buffer.AppendLine();
        }

        requestState.Buffer.Append(content);
        await requestState.OnDelta(content, cancellationToken);
    }

    private static CliEventParseResult TryParseCliEvent(string rawLine)
    {
        var trimmed = rawLine.Trim();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                var root = document.RootElement;

                var type = GetString(root, "type") ?? GetString(root, "event") ?? string.Empty;
                var content =
                    GetString(root, "delta")
                    ?? GetString(root, "content")
                    ?? GetString(root, "text")
                    ?? GetNestedString(root, "data", "delta")
                    ?? GetNestedString(root, "data", "content")
                    ?? GetNestedString(root, "data", "text")
                    ?? GetNestedString(root, "message", "content")
                    ?? GetNestedString(root, "message", "text");

                var isThinking = type.Contains("reason", StringComparison.OrdinalIgnoreCase)
                    || type.Contains("thinking", StringComparison.OrdinalIgnoreCase);

                var toolUsed = type.Contains("tool", StringComparison.OrdinalIgnoreCase)
                    || type.Contains("mcp", StringComparison.OrdinalIgnoreCase)
                    || type.Contains("function", StringComparison.OrdinalIgnoreCase)
                    || type.Contains("command", StringComparison.OrdinalIgnoreCase);

                if (!toolUsed)
                {
                    var role = GetString(root, "role") ?? GetNestedString(root, "message", "role") ?? string.Empty;
                    toolUsed = role.Contains("tool", StringComparison.OrdinalIgnoreCase);
                }

                return new CliEventParseResult(content ?? string.Empty, isThinking, toolUsed);
            }
            catch (JsonException)
            {
                return new CliEventParseResult(trimmed, false, LooksLikeToolTrace(trimmed));
            }
        }

        return new CliEventParseResult(trimmed, false, LooksLikeToolTrace(trimmed));
    }

    private static bool LooksLikeToolTrace(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("tool", StringComparison.OrdinalIgnoreCase)
            || value.Contains("mcp:", StringComparison.OrdinalIgnoreCase)
            || value.Contains("run_in_terminal", StringComparison.OrdinalIgnoreCase)
            || value.Contains("apply_patch", StringComparison.OrdinalIgnoreCase)
            || value.Contains("runTests", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static string? GetNestedString(JsonElement element, string parentName, string childName)
    {
        if (!element.TryGetProperty(parentName, out var parent) || parent.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return GetString(parent, childName);
    }

    private void ApplyCliEnvironment(ProcessStartInfo startInfo)
    {
        startInfo.Environment[CopilotAllowAllEnvVar] = "1";

        if (!_options.McpDiscovery.Enabled)
        {
            return;
        }

        var resolution = _mcpServerResolver.Resolve();
        if (resolution.Servers.Count == 0)
        {
            _logger.LogInformation("No MCP servers found for Copilot CLI.");
            return;
        }

        var serverNames = resolution.Servers.Keys
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (string.IsNullOrWhiteSpace(resolution.ConfigDir))
        {
            throw new McpSetupException(
                "MCP tools are temporarily unavailable for this Copilot session.",
                serverNames,
                "Resolved MCP servers but merged MCP config directory is unavailable.");
        }

        if (string.IsNullOrWhiteSpace(_options.Cli.McpConfigDirEnvironmentVariable))
        {
            return;
        }

        startInfo.Environment[_options.Cli.McpConfigDirEnvironmentVariable] = resolution.ConfigDir;
        _logger.LogInformation(
            "Loaded {Count} MCP server(s) for Copilot CLI. ConfigDir={ConfigDir}",
            resolution.Servers.Count,
            resolution.ConfigDir);
    }

    private static string BuildCliFailureMessage(int exitCode, string stderr)
    {
        if (!string.IsNullOrWhiteSpace(stderr) && IsPermissionDeniedError(stderr))
        {
            return "MCP permission denied. This session could not request interactive approval. Ensure Copilot CLI runs with --allow-all-tools (or COPILOT_ALLOW_ALL=1) and that Gmail MCP is already authorized for the runtime Windows user profile.";
        }

        return string.IsNullOrWhiteSpace(stderr)
            ? $"Copilot CLI exited with code {exitCode}."
            : $"Copilot CLI exited with code {exitCode}: {stderr}";
    }

    private static bool IsPermissionDeniedError(string stderr)
    {
        var normalized = stderr.ToLowerInvariant();
        return normalized.Contains("permission denied", StringComparison.Ordinal)
            || normalized.Contains("could not request permission from user", StringComparison.Ordinal)
            || normalized.Contains("not authorized", StringComparison.Ordinal);
    }

    private static async Task<string> ReadAllStderrAsync(Process process, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        while (true)
        {
            var line = await process.StandardError.ReadLineAsync(cancellationToken);
            if (line == null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(line.Trim());
        }

        return builder.ToString();
    }

    private static void SafeKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Ignore best-effort shutdown errors.
        }
    }

    private readonly record struct CliEventParseResult(string Content, bool IsThinking, bool ToolUsed);
}

public sealed class McpSetupException : Exception
{
    public IReadOnlyList<string> ServerNames { get; }
    public string Detail { get; }

    public McpSetupException(string message, IReadOnlyList<string> serverNames, string detail)
        : base(message)
    {
        ServerNames = serverNames;
        Detail = detail;
    }
}
