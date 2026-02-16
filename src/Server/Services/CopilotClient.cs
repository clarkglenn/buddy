using System.Collections.Generic;
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
        var requestState = new CopilotRequestState(buffer, onDelta);
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

        return buffer.ToString();
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

        var applied = TrySetSessionConfigProperty(sessionConfig, "McpServers", resolution.Servers);
        if (!applied && !string.IsNullOrWhiteSpace(resolution.ConfigDir))
        {
            applied = TrySetSessionConfigProperty(sessionConfig, "ConfigDir", resolution.ConfigDir);
        }

        if (applied)
        {
            _logger.LogInformation("Loaded {Count} MCP server(s) for Copilot CLI.", resolution.Servers.Count);
        }
        else
        {
            _logger.LogWarning("MCP servers resolved but Copilot SDK does not expose McpServers or ConfigDir.");
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
            return null;
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
            parts.Add(message);
        }

        return parts.Count == 0 ? null : string.Join(Environment.NewLine + Environment.NewLine, parts);
    }
}
