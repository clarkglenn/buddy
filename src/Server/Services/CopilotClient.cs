using System.Collections.Generic;
using System.Text.RegularExpressions;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Options;
using Server.Options;
using CopilotSDKClient = GitHub.Copilot.SDK.CopilotClient;

namespace Server.Services;

public sealed class CopilotClient
{
    private readonly CopilotOptions _options;
    private readonly ILogger<CopilotClient> _logger;
    private readonly ICopilotSessionStore _sessionStore;
    private readonly IMcpServerResolver _mcpServerResolver;

    private const string ChannelContextKey = "channel";
    private const string ThreadContextKey = "thread_ts";
    private static readonly string[] BuiltInTools =
    [
        "run_in_terminal",
        "run_task",
        "runTests",
        "read_file",
        "grep_search",
        "file_search",
        "list_dir",
        "apply_patch"
    ];

    private static readonly string[] NonTrivialKeywords =
    [
        "code", "implement", "build", "compile", "run", "test", "fix", "bug", "debug", "refactor",
        "class", "method", "function", "script", "file", "repository", "project", "deploy", "migration"
    ];

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
        if (string.IsNullOrWhiteSpace(_options.Model))
        {
            throw new InvalidOperationException("Copilot model is not configured (Copilot:Model).");
        }
        var conversationKey = BuildConversationKey(context, conversationUserKey);

        if (string.IsNullOrWhiteSpace(conversationKey))
        {
            return await StreamWithEphemeralSessionAsync(token, prompt, onDelta, cancellationToken);
        }

        var entry = await _sessionStore.GetOrCreateAsync(
            conversationKey,
            ct => CreateSessionEntryAsync(token, ct),
            cancellationToken);

        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            var response = await StreamWithSessionAsync(entry, prompt, onDelta, cancellationToken);

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

    public async Task<IReadOnlyList<string>> GetAvailableModelsAsync(string token, CancellationToken cancellationToken)
    {
        // The GitHub.Copilot.SDK does not expose a direct models endpoint.
        // Return the configured model as the available option.
        // In a real scenario, you might query a list endpoint if GitHub provides one.
        return new[] { _options.Model };
    }

    private string BuildPrompt(string prompt)
    {
        var prefix = BuildPromptPrefix();
        return string.IsNullOrWhiteSpace(prefix)
            ? prompt
            : string.Join(Environment.NewLine + Environment.NewLine, prefix, prompt);
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

    private async Task<string> StreamWithEphemeralSessionAsync(
        string token,
        string prompt,
        Func<string, CancellationToken, Task> onDelta,
        CancellationToken cancellationToken)
    {
        await using var entry = await CreateSessionEntryAsync(token, cancellationToken);
        return await StreamWithSessionAsync(entry, prompt, onDelta, cancellationToken);
    }

    private async Task<CopilotSessionEntry> CreateSessionEntryAsync(string token, CancellationToken cancellationToken)
    {
        var sdkClient = new CopilotSDKClient(new CopilotClientOptions
        {
            GithubToken = token
        });

        await sdkClient.StartAsync();

        var sessionConfig = new SessionConfig
        {
            Model = _options.Model,
            Streaming = true
        };

        var modeApplied = TrySetSessionConfigProperty(sessionConfig, "Mode", _options.DefaultMode)
            || TrySetSessionConfigProperty(sessionConfig, "ChatMode", _options.DefaultMode)
            || TrySetSessionConfigProperty(sessionConfig, "DefaultMode", _options.DefaultMode);

        if (!modeApplied)
        {
            _logger.LogDebug("Copilot SDK does not expose a session mode property; proceeding without explicit mode. Requested mode: {Mode}", _options.DefaultMode);
        }

        if (_options.ToolAccess.AutoApproveToolPermissions)
        {
            sessionConfig.Hooks = new SessionHooks
            {
                OnPreToolUse = async (input, invocation) =>
                {
                    return new PreToolUseHookOutput
                    {
                        PermissionDecision = "allow",
                        ModifiedArgs = input.ToolArgs
                    };
                }
            };
        }

        if (!_options.ToolAccess.AllowAll)
        {
            if (_options.ToolAccess.AvailableTools is { Length: > 0 })
            {
                sessionConfig.AvailableTools = _options.ToolAccess.AvailableTools.ToList();
            }

            if (_options.ToolAccess.ExcludedTools is { Length: > 0 })
            {
                sessionConfig.ExcludedTools = _options.ToolAccess.ExcludedTools.ToList();
            }
        }

        ApplyMcpServers(sessionConfig);

        var session = await sdkClient.CreateSessionAsync(sessionConfig);
        var entry = new CopilotSessionEntry(sdkClient, session);

        session.On(evt =>
        {
            var request = entry.CurrentRequest;
            if (request == null)
            {
                return;
            }

            switch (evt)
            {
                case AssistantReasoningDeltaEvent reasoningDelta:
                    {
                        var content = reasoningDelta.Data.DeltaContent ?? string.Empty;
                        _ = request.OnDelta($"[THINKING] {content}", CancellationToken.None);
                        break;
                    }

                case AssistantMessageDeltaEvent delta:
                    {
                        var content = delta.Data.DeltaContent ?? string.Empty;
                        request.Buffer.Append(content);
                        _ = request.OnDelta(content, CancellationToken.None);
                        break;
                    }

                case AssistantReasoningEvent reasoning:
                    {
                        _logger.LogDebug("Copilot reasoning: {Content}", reasoning.Data.Content);
                        break;
                    }

                case AssistantMessageEvent:
                    {
                        break;
                    }

                case SessionIdleEvent:
                    {
                        request.Done.TrySetResult(true);
                        break;
                    }

                case SessionErrorEvent error:
                    {
                        entry.MarkFaulted();
                        _logger.LogError("Copilot session error: {ErrorMessage}", error.Data.Message);
                        request.Done.TrySetException(new InvalidOperationException($"Copilot session error: {error.Data.Message}"));
                        break;
                    }

                default:
                    {
                        if (IsLikelyToolEvent(evt))
                        {
                            request.ToolUsed = true;
                        }

                        break;
                    }
            }
        });

        return entry;
    }

    private async Task<string> StreamWithSessionAsync(
        CopilotSessionEntry entry,
        string prompt,
        Func<string, CancellationToken, Task> onDelta,
        CancellationToken cancellationToken)
    {
        var buffer = new System.Text.StringBuilder();
        var requestState = new CopilotRequestState(buffer, onDelta, IsTrivialPrompt(prompt));
        entry.CurrentRequest = requestState;

        try
        {
            var enrichedPrompt = BuildPrompt(prompt);
            await entry.Session.SendAsync(new MessageOptions { Prompt = enrichedPrompt });

            var timeoutTask = Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
            var completedTask = await Task.WhenAny(requestState.Done.Task, timeoutTask);

            if (completedTask == timeoutTask)
            {
                _logger.LogWarning("Copilot response timeout after 5 minutes");
                entry.MarkFaulted();
                return buffer.ToString();
            }

            await requestState.Done.Task;

            var response = buffer.ToString();
            EnforceToolUsePolicy(prompt, requestState.IsTrivialPrompt, requestState.ToolUsed, response);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Copilot streaming");
            throw;
        }
        finally
        {
            entry.CurrentRequest = null;
            entry.Touch();
        }
    }

    private void ApplyMcpServers(SessionConfig sessionConfig)
    {
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
        _logger.LogInformation(
            "Attempting MCP setup for Copilot session. Servers={Servers}. ConfigDir={ConfigDir}",
            string.Join(", ", serverNames),
            string.IsNullOrWhiteSpace(resolution.ConfigDir) ? "<none>" : resolution.ConfigDir);

        var appliedVia = string.Empty;
        string? firstError = null;

        if (TrySetSessionConfigPropertyWithDiagnostics(sessionConfig, "McpServers", resolution.Servers, out var mcpServersError))
        {
            appliedVia = "McpServers";
        }
        else
        {
            firstError = mcpServersError;
            _logger.LogWarning(
                "Failed to bind MCP servers via SessionConfig.McpServers. Error={Error}",
                string.IsNullOrWhiteSpace(mcpServersError) ? "n/a" : mcpServersError);
        }

        if (string.IsNullOrEmpty(appliedVia) && !string.IsNullOrWhiteSpace(resolution.ConfigDir))
        {
            if (TrySetSessionConfigPropertyWithDiagnostics(sessionConfig, "ConfigDir", resolution.ConfigDir, out var configDirError))
            {
                appliedVia = "ConfigDir";
            }
            else
            {
                _logger.LogWarning(
                    "Failed to bind MCP config via SessionConfig.ConfigDir. Error={Error}",
                    string.IsNullOrWhiteSpace(configDirError) ? "n/a" : configDirError);

                if (string.IsNullOrWhiteSpace(firstError))
                {
                    firstError = configDirError;
                }
            }
        }

        if (!string.IsNullOrEmpty(appliedVia))
        {
            _logger.LogInformation(
                "Loaded {Count} MCP server(s) for Copilot CLI via SessionConfig.{AppliedVia}.",
                resolution.Servers.Count,
                appliedVia);
        }
        else
        {
            var detail = $"Resolved MCP servers ({string.Join(", ", serverNames)}) but failed to bind to Copilot SDK session config.";
            if (!string.IsNullOrWhiteSpace(firstError))
            {
                detail = $"{detail} Cause: {firstError}";
            }

            throw new McpSetupException(
                "MCP tools are temporarily unavailable for this Copilot session.",
                serverNames,
                detail);
        }
    }

    private static bool TrySetSessionConfigPropertyWithDiagnostics(
        SessionConfig sessionConfig,
        string propertyName,
        object? value,
        out string? error)
    {
        error = null;
        var property = sessionConfig.GetType().GetProperty(propertyName);
        if (property == null)
        {
            error = $"Property '{propertyName}' not found on SessionConfig.";
            return false;
        }

        if (!property.CanWrite)
        {
            error = $"Property '{propertyName}' exists but is not writable.";
            return false;
        }

        try
        {
            property.SetValue(sessionConfig, value);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TrySetSessionConfigProperty(SessionConfig sessionConfig, string propertyName, object? value)
    {
        var property = sessionConfig.GetType().GetProperty(propertyName);
        if (property == null || !property.CanWrite)
        {
            return false;
        }

        property.SetValue(sessionConfig, value);
        return true;
    }

    private string? BuildPromptPrefix()
    {
        if (!_options.ToolAccess.AdvertiseAllTools && string.IsNullOrWhiteSpace(_options.SystemMessage))
        {
            if (!_options.ToolUsePolicy.Enabled)
            {
                return null;
            }
        }

        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(_options.SystemMessage))
        {
            parts.Add(_options.SystemMessage.Trim());
        }

        if (_options.ToolAccess.AdvertiseAllTools)
        {
            var message = string.IsNullOrWhiteSpace(_options.ToolAccess.AdvertiseMessage)
                ? "You have access to MCP tools configured for this session. Only use tools that are actually available/allowed, and ensure any required executables (for example: pwsh, node, npx) are installed and on PATH."
                : _options.ToolAccess.AdvertiseMessage.Trim();

            var explicitTools = BuildExplicitToolsList();
            if (!string.IsNullOrWhiteSpace(explicitTools))
            {
                message = $"{message} Explicit tools list: {explicitTools}.";
            }

            parts.Add(message);
        }

        if (_options.ToolUsePolicy.Enabled)
        {
            var categories = _options.ToolUsePolicy.PreferredToolCategories is { Length: > 0 }
                ? string.Join(", ", _options.ToolUsePolicy.PreferredToolCategories)
                : "MCP, CLI, read-only helper tools";

            parts.Add($"Policy: For non-trivial requests, use tools instead of direct answers. Preferred categories: {categories}. Direct no-tool responses are only allowed for concise factual questions.");
        }

        return parts.Count == 0 ? null : string.Join(Environment.NewLine + Environment.NewLine, parts);
    }

    private string BuildExplicitToolsList()
    {
        var tools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tool in GetBuiltInToolNames())
        {
            tools.Add(tool);
        }

        foreach (var tool in GetMcpToolNames())
        {
            tools.Add(tool);
        }

        return tools.Count == 0
            ? string.Empty
            : string.Join(", ", tools.OrderBy(static tool => tool, StringComparer.OrdinalIgnoreCase));
    }

    private IEnumerable<string> GetBuiltInToolNames()
    {
        IEnumerable<string> tools = !_options.ToolAccess.AllowAll && _options.ToolAccess.AvailableTools is { Length: > 0 }
            ? _options.ToolAccess.AvailableTools
            : BuiltInTools;

        if (_options.ToolAccess.ExcludedTools is { Length: > 0 })
        {
            var excluded = new HashSet<string>(_options.ToolAccess.ExcludedTools, StringComparer.OrdinalIgnoreCase);
            tools = tools.Where(tool => !excluded.Contains(tool));
        }

        return tools
            .Where(tool => !string.IsNullOrWhiteSpace(tool))
            .Select(tool => tool.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private IReadOnlyList<string> GetMcpToolNames()
    {
        if (!_options.McpDiscovery.Enabled)
        {
            return [];
        }

        try
        {
            var resolution = _mcpServerResolver.Resolve();
            return resolution.ToolNames;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve MCP tools for explicit prompt list.");
            return [];
        }
    }

    private void EnforceToolUsePolicy(string prompt, bool isTrivialPrompt, bool toolUsed, string response)
    {
        if (!_options.ToolUsePolicy.Enabled)
        {
            return;
        }

        if (toolUsed)
        {
            return;
        }

        if (isTrivialPrompt && _options.ToolUsePolicy.AllowDirectResponsesForTrivialQuestions)
        {
            return;
        }

        if (!_options.ToolUsePolicy.FailImmediatelyOnViolation)
        {
            return;
        }

        _logger.LogWarning(
            "Copilot tool-use policy violation detected. PromptLength={PromptLength}, ResponseLength={ResponseLength}, TrivialPrompt={TrivialPrompt}",
            prompt.Length,
            response.Length,
            isTrivialPrompt);

        throw new InvalidOperationException(_options.ToolUsePolicy.ViolationMessage);
    }

    private bool IsTrivialPrompt(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return true;
        }

        var normalized = prompt.Trim();
        if (normalized.Length > _options.ToolUsePolicy.TrivialQuestionMaxChars)
        {
            return false;
        }

        if (normalized.Contains('\n'))
        {
            return false;
        }

        var lowered = normalized.ToLowerInvariant();
        if (NonTrivialKeywords.Any(keyword => lowered.Contains(keyword, StringComparison.Ordinal)))
        {
            return false;
        }

        var wordCount = Regex.Matches(normalized, @"\b\w+\b").Count;
        if (wordCount > 30)
        {
            return false;
        }

        return normalized.EndsWith("?", StringComparison.Ordinal)
            || lowered.StartsWith("what is ", StringComparison.Ordinal)
            || lowered.StartsWith("who is ", StringComparison.Ordinal)
            || lowered.StartsWith("when is ", StringComparison.Ordinal)
            || lowered.StartsWith("where is ", StringComparison.Ordinal)
            || lowered.StartsWith("how many ", StringComparison.Ordinal)
            || lowered.StartsWith("define ", StringComparison.Ordinal)
            || lowered.StartsWith("briefly explain ", StringComparison.Ordinal);
    }

    private static bool IsLikelyToolEvent(object evt)
    {
        var eventTypeName = evt.GetType().Name;
        return eventTypeName.Contains("Tool", StringComparison.OrdinalIgnoreCase)
            || eventTypeName.Contains("Function", StringComparison.OrdinalIgnoreCase)
            || eventTypeName.Contains("Mcp", StringComparison.OrdinalIgnoreCase)
            || eventTypeName.Contains("Command", StringComparison.OrdinalIgnoreCase);
    }
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
