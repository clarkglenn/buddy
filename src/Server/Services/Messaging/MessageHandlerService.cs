using CopilotClient = global::Server.Services.CopilotClient;
using IMcpServerResolver = global::Server.Services.IMcpServerResolver;
using McpSetupException = global::Server.Services.McpSetupException;
using System.Text;
using System.Text.RegularExpressions;

namespace Buddy.Server.Services.Messaging;

/// <summary>
/// Processes incoming messages and routes them for authentication or prompt handling
/// </summary>
public class MessageHandlerService : IMessageHandlerService
{
    private const string UnconfirmedCompletionMessage = "❌ I couldn’t confirm this request completed successfully.";

    private readonly IMessagingProviderFactory _providerFactory;
    private readonly CopilotClient _copilotClient;
    private readonly IMcpServerResolver _mcpServerResolver;
    private readonly ILogger<MessageHandlerService> _logger;

    public MessageHandlerService(
        IMessagingProviderFactory providerFactory,
        CopilotClient copilotClient,
        IMcpServerResolver mcpServerResolver,
        ILogger<MessageHandlerService> logger)
    {
        _providerFactory = providerFactory;
        _copilotClient = copilotClient;
        _mcpServerResolver = mcpServerResolver;
        _logger = logger;
    }

    public async Task HandleMessageAsync(IncomingMessage message, CancellationToken cancellationToken = default)
    {
        var text = message.Text.Trim();

        // Check for /help command
        if (text.StartsWith("/help", StringComparison.OrdinalIgnoreCase))
        {
            await HandleHelpCommandAsync(message, cancellationToken);
            return;
        }

        // Otherwise, treat as prompt
        await HandlePromptAsync(message, cancellationToken);
    }

    private async Task HandleHelpCommandAsync(IncomingMessage message, CancellationToken cancellationToken)
    {
        var helpText = $@"🤖 **Buddy Copilot Help**

Commands:
• `/help` - Show this help message

Simply send any message to interact with Copilot!

Status:
✅ Copilot CLI machine auth is in use.";

        await SendMessageAsync(message.From, helpText, cancellationToken, message.Context);
    }

    private async Task HandlePromptAsync(IncomingMessage message, CancellationToken cancellationToken)
    {
        const string token = "";
        Task thinkingTask = Task.CompletedTask;
        CancellationTokenSource? thinkingCts = null;

        if (LooksLikeEmailRequest(message.Text))
        {
            var mcpResolution = _mcpServerResolver.Resolve();
            if (!HasEmailMcpCapability(mcpResolution.ToolNames))
            {
                await SendMessageAsync(
                    message.From,
                    "❌ I couldn’t send the email because no email MCP tool is available in this session.",
                    cancellationToken,
                    message.Context);
                return;
            }
        }

        try
        {
            var resultBuffer = new StringBuilder();
            var hasSentAnswerChunk = 0;
            const string thinkingPrefix = "[THINKING]";
            var thinkingThrottle = TimeSpan.FromSeconds(1);
            var thinkingDotCount = 1;
            var lastAnswerUpdateAt = (DateTimeOffset?)null;
            var updateThrottle = TimeSpan.FromMilliseconds(900);
            var replyTs = TryGetContextValue(message.Context, MessagingContextKeys.ReplyTs);
            var lastRenderedMessage = string.Empty;

            thinkingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            thinkingTask = Task.Run(async () =>
            {
                try
                {
                    while (!thinkingCts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(thinkingThrottle, thinkingCts.Token);

                        if (Volatile.Read(ref hasSentAnswerChunk) == 1)
                        {
                            break;
                        }

                        thinkingDotCount = (thinkingDotCount % 3) + 1;
                        var thinkingMessage = new string('.', thinkingDotCount);
                        var heartbeatResult = await UpsertResponseAsync(message.From, thinkingMessage, message.Context, replyTs, MessageStyle.Thinking, thinkingCts.Token);
                        if (heartbeatResult.Success && !string.IsNullOrWhiteSpace(heartbeatResult.MessageTs))
                        {
                            replyTs = heartbeatResult.MessageTs;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
            }, thinkingCts.Token);

            // Stream the response
            var response = await _copilotClient.StreamCopilotResponseAsync(
                token,
                message.Text,
                async (delta, ct) =>
                {
                    if (string.IsNullOrWhiteSpace(delta))
                    {
                        return;
                    }

                    if (delta.StartsWith(thinkingPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    var sanitizedDelta = SanitizeDelta(delta);
                    if (string.IsNullOrWhiteSpace(sanitizedDelta))
                    {
                        return;
                    }

                    var userFacingDelta = SanitizeForUserFacingOutput(sanitizedDelta, trimOuterWhitespace: false);
                    if (string.IsNullOrWhiteSpace(userFacingDelta))
                    {
                        return;
                    }

                    if (Volatile.Read(ref hasSentAnswerChunk) == 0 && LooksLikeInternalProgress(userFacingDelta))
                    {
                        return;
                    }

                    if (Interlocked.CompareExchange(ref hasSentAnswerChunk, 1, 0) == 0)
                    {
                        thinkingCts.Cancel();
                    }

                    resultBuffer.Append(userFacingDelta);

                    var nowForAnswer = DateTimeOffset.UtcNow;
                    if (lastAnswerUpdateAt == null || nowForAnswer - lastAnswerUpdateAt >= updateThrottle)
                    {
                        var current = resultBuffer.ToString();
                        if (!string.Equals(current, lastRenderedMessage, StringComparison.Ordinal))
                        {
                            var updateResult = await UpsertResponseAsync(message.From, current, message.Context, replyTs, MessageStyle.Default, ct);
                            if (updateResult.Success && !string.IsNullOrWhiteSpace(updateResult.MessageTs))
                            {
                                replyTs = updateResult.MessageTs;
                            }

                            lastRenderedMessage = current;
                            lastAnswerUpdateAt = nowForAnswer;
                        }
                    }
                },
                cancellationToken,
                message.Context,
                message.From.ToString()
            );

            if (resultBuffer.Length == 0)
            {
                var fallback = SanitizeForUserFacingOutput(SanitizeDelta(response));
                if (!string.IsNullOrWhiteSpace(fallback) && !LooksLikeInternalProgress(fallback))
                {
                    resultBuffer.Append(fallback);
                }
            }

            if (resultBuffer.Length > 0)
            {
                var finalContent = BuildDefinitiveFinalMessage(resultBuffer.ToString());
                if (!string.Equals(finalContent, lastRenderedMessage, StringComparison.Ordinal))
                {
                    var finalResult = await UpsertResponseAsync(message.From, finalContent, message.Context, replyTs, MessageStyle.Default, cancellationToken);
                    if (finalResult.Success && !string.IsNullOrWhiteSpace(finalResult.MessageTs))
                    {
                        replyTs = finalResult.MessageTs;
                    }
                }
            }

        }
        catch (McpSetupException ex)
        {
            _logger.LogWarning(
                "MCP setup failed for user {User}. Servers={Servers}. Detail={Detail}",
                message.From,
                string.Join(", ", ex.ServerNames),
                ex.Detail);

            await SendMessageAsync(
                message.From,
                "⚠️ MCP tools are unavailable for this session right now. Please try again shortly or ask an admin to check MCP configuration.",
                cancellationToken,
                message.Context);
        }
        catch (InvalidOperationException ex) when (IsMcpPermissionDeniedError(ex))
        {
            _logger.LogWarning(ex, "MCP permission denied for user {User}", message.From);
            await SendMessageAsync(
                message.From,
                "⚠️ Gmail MCP is configured but not authorized for this runtime session. This server runs headless, so interactive consent prompts cannot be shown. Ensure the runtime Windows user has completed Gmail MCP authorization and that Copilot CLI is started with auto-approve permissions.",
                cancellationToken,
                message.Context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling prompt for {User}", message.From);
            await SendMessageAsync(message.From, "❌ Request failed and was not completed. Please try again.", cancellationToken, message.Context);
        }
        finally
        {
            if (thinkingCts != null)
            {
                try
                {
                    thinkingCts.Cancel();
                }
                catch
                {
                }

                try
                {
                    await thinkingTask;
                }
                catch (OperationCanceledException)
                {
                }

                thinkingCts.Dispose();
            }
        }
    }

    private async Task<MessageSendResult> SendMessageAsync(PlatformUser user, string message, CancellationToken cancellationToken, Dictionary<string, string>? context, MessageStyle style = MessageStyle.Default)
    {
        var provider = _providerFactory.GetProvider(user.Platform);
        var parameters = new SendMessageParams
        {
            User = user,
            Message = message,
            Context = context,
            Style = style
        };

        return await provider.SendMessageAsync(parameters, cancellationToken);
    }

    private async Task<MessageSendResult> UpsertResponseAsync(
        PlatformUser user,
        string message,
        Dictionary<string, string>? originalContext,
        string? replyTs,
        MessageStyle style,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(replyTs))
        {
            return await SendMessageAsync(user, message, cancellationToken, originalContext, style);
        }

        var updateContext = originalContext == null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(originalContext, StringComparer.OrdinalIgnoreCase);

        updateContext[MessagingContextKeys.UpdateTs] = replyTs;

        return await SendMessageAsync(user, message, cancellationToken, updateContext, style);
    }

    private static string? TryGetContextValue(Dictionary<string, string>? context, string key)
    {
        if (context == null)
        {
            return null;
        }

        return context.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static string SanitizeDelta(string delta)
    {
        if (string.IsNullOrWhiteSpace(delta))
        {
            return string.Empty;
        }

        return delta.Replace("\r", string.Empty);
    }

    private static string SanitizeForUserFacingOutput(string content)
    {
        return SanitizeForUserFacingOutput(content, trimOuterWhitespace: true);
    }

    private static string SanitizeForUserFacingOutput(string content, bool trimOuterWhitespace)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var lines = content
            .Split('\n', StringSplitOptions.None)
            .Select(static line => line.TrimEnd())
            .ToArray();

        var kept = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            if (ShouldSuppressOperationalLine(line))
            {
                continue;
            }

            kept.Add(line);
        }

        var sanitized = string.Join("\n", kept);
        sanitized = NormalizeInlineDashListFormatting(sanitized);
        return trimOuterWhitespace ? sanitized.Trim() : sanitized;
    }

    private static string NormalizeInlineDashListFormatting(string content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Contains('\n', StringComparison.Ordinal))
        {
            return content;
        }

        var dashDelimiterCount = Regex.Matches(content, "\\s-\\s").Count;
        if (dashDelimiterCount < 3)
        {
            return content;
        }

        var splitIndex = content.IndexOf(':');
        if (splitIndex < 0 || splitIndex >= content.Length - 1)
        {
            return content;
        }

        var intro = content[..(splitIndex + 1)].TrimEnd();
        var remainder = content[(splitIndex + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(remainder))
        {
            return content;
        }

        var parts = remainder
            .Split(" - ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 3)
        {
            return content;
        }

        var bullets = parts.Select(static part => $"- {part}");
        return $"{intro}\n{string.Join("\n", bullets)}";
    }

    private static bool ShouldSuppressOperationalLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.TrimStart();

        if (LooksLikeToolTraceLine(trimmed))
        {
            return true;
        }

        if (trimmed.StartsWith("❌", StringComparison.Ordinal)
            || trimmed.StartsWith("⚠️", StringComparison.Ordinal)
            || trimmed.StartsWith("✅", StringComparison.Ordinal))
        {
            return false;
        }

        if (LooksLikeInternalProgress(trimmed))
        {
            return true;
        }

        var normalized = trimmed
            .Replace('’', '\'')
            .ToLowerInvariant();
        return (normalized.Contains("pwsh", StringComparison.Ordinal)
                || normalized.Contains("powershell", StringComparison.Ordinal)
                || normalized.Contains("terminal", StringComparison.Ordinal)
                || normalized.Contains("cli", StringComparison.Ordinal))
            && (normalized.Contains("isn't available", StringComparison.Ordinal)
                || normalized.Contains("not available", StringComparison.Ordinal)
                || normalized.Contains("can't", StringComparison.Ordinal)
                || normalized.Contains("cannot", StringComparison.Ordinal)
                || normalized.Contains("couldn't", StringComparison.Ordinal)
                || normalized.Contains("failed", StringComparison.Ordinal));
    }

    private static bool LooksLikeToolTraceLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.Trim();
        var normalized = trimmed.ToLowerInvariant();

        if (trimmed.StartsWith("●", StringComparison.Ordinal)
            || trimmed.StartsWith("└", StringComparison.Ordinal)
            || trimmed.StartsWith("├", StringComparison.Ordinal)
            || trimmed.StartsWith("- ", StringComparison.Ordinal)
            || trimmed.StartsWith("• ", StringComparison.Ordinal))
        {
            if (normalized.Contains("mcp", StringComparison.Ordinal)
                || normalized.Contains("tool", StringComparison.Ordinal)
                || normalized.Contains("gmail-", StringComparison.Ordinal)
                || normalized.Contains("chatgpt", StringComparison.Ordinal)
                || normalized.Contains("id:", StringComparison.Ordinal)
                || normalized.Contains("function", StringComparison.Ordinal)
                || normalized.Contains("run_in_terminal", StringComparison.Ordinal)
                || normalized.Contains("apply_patch", StringComparison.Ordinal)
                || normalized.Contains("search_emails", StringComparison.Ordinal)
                || normalized.Contains("send_email", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeInternalProgress(string delta)
    {
        var text = delta.TrimStart();
        if (text.Length == 0)
        {
            return true;
        }

        var normalized = text.ToLowerInvariant();

        var hasExecutionIntent = text.StartsWith("I’m going to", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("I'm going to", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("I’ll", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("I'll", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Next I’ll", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Next I'll", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("PowerShell tool", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Edits failed", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Found the root cause", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Now that we", StringComparison.OrdinalIgnoreCase);

        if (!hasExecutionIntent)
        {
            return false;
        }

        var hasToolingContext = normalized.Contains("tool", StringComparison.Ordinal)
            || normalized.Contains("mcp", StringComparison.Ordinal)
            || normalized.Contains("config", StringComparison.Ordinal)
            || normalized.Contains("logs", StringComparison.Ordinal)
            || normalized.Contains("patch", StringComparison.Ordinal)
            || normalized.Contains("read-only", StringComparison.Ordinal)
            || normalized.Contains("powershell", StringComparison.Ordinal)
            || normalized.Contains("terminal", StringComparison.Ordinal)
            || normalized.Contains("npx", StringComparison.Ordinal)
            || normalized.Contains("gmail", StringComparison.Ordinal)
            || normalized.Contains("playwright", StringComparison.Ordinal)
            || normalized.Contains("startup", StringComparison.Ordinal)
            || normalized.Contains("inspect", StringComparison.Ordinal)
            || normalized.Contains("verify", StringComparison.Ordinal)
            || normalized.Contains("search", StringComparison.Ordinal)
            || normalized.Contains("file", StringComparison.Ordinal);

        return hasToolingContext;
    }

    private static bool LooksLikeEmailRequest(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return false;
        }

        var normalized = prompt.ToLowerInvariant();
        return normalized.Contains("send an email", StringComparison.Ordinal)
            || normalized.Contains("send email", StringComparison.Ordinal)
            || normalized.Contains("email to", StringComparison.Ordinal)
            || normalized.Contains("mail to", StringComparison.Ordinal)
            || normalized.Contains("gmail", StringComparison.Ordinal);
    }

    private static bool HasEmailMcpCapability(IReadOnlyList<string> toolNames)
    {
        if (toolNames == null || toolNames.Count == 0)
        {
            return false;
        }

        foreach (var toolName in toolNames)
        {
            if (string.IsNullOrWhiteSpace(toolName))
            {
                continue;
            }

            var normalized = toolName.ToLowerInvariant();
            if (normalized.Contains("gmail", StringComparison.Ordinal)
                || normalized.Contains("email", StringComparison.Ordinal)
                || normalized.Contains("mail", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildDefinitiveFinalMessage(string message)
    {
        var sanitizedMessage = SanitizeForUserFacingOutput(message);
        if (string.IsNullOrWhiteSpace(sanitizedMessage))
        {
            return "❌ Request failed and was not completed.";
        }

        var trimmed = sanitizedMessage.Trim();
        if (LooksLikeDefinitiveFailure(trimmed))
        {
            return trimmed;
        }

        if (LooksLikeDefinitiveSuccess(trimmed))
        {
            return trimmed;
        }

        if (!LooksLikeActionOrCommitment(trimmed))
        {
            return trimmed;
        }

        return $"{trimmed}{Environment.NewLine}{Environment.NewLine}{UnconfirmedCompletionMessage}";
    }

    private static bool LooksLikeActionOrCommitment(string message)
    {
        var normalized = message.ToLowerInvariant();
        var lines = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            return false;
        }

        var firstLine = lines[0].ToLowerInvariant();
        if (firstLine.StartsWith("i will", StringComparison.Ordinal)
            || firstLine.StartsWith("i'm going to", StringComparison.Ordinal)
            || firstLine.StartsWith("i am going to", StringComparison.Ordinal)
            || firstLine.StartsWith("i'll", StringComparison.Ordinal)
            || firstLine.StartsWith("working on", StringComparison.Ordinal)
            || firstLine.StartsWith("attempting", StringComparison.Ordinal)
            || firstLine.StartsWith("trying", StringComparison.Ordinal))
        {
            return true;
        }

        return normalized.Contains("sending", StringComparison.Ordinal)
            || normalized.Contains("sent", StringComparison.Ordinal)
            || normalized.Contains("execute", StringComparison.Ordinal)
            || normalized.Contains("executed", StringComparison.Ordinal)
            || normalized.Contains("run ", StringComparison.Ordinal)
            || normalized.Contains("running", StringComparison.Ordinal)
            || normalized.Contains("deploy", StringComparison.Ordinal)
            || normalized.Contains("deployed", StringComparison.Ordinal)
            || normalized.Contains("created", StringComparison.Ordinal)
            || normalized.Contains("updated", StringComparison.Ordinal)
            || normalized.Contains("deleted", StringComparison.Ordinal)
            || normalized.Contains("changed", StringComparison.Ordinal)
            || normalized.Contains("applied", StringComparison.Ordinal)
            || normalized.Contains("committed", StringComparison.Ordinal)
            || normalized.Contains("opened", StringComparison.Ordinal)
            || normalized.Contains("posted", StringComparison.Ordinal)
            || normalized.Contains("submitted", StringComparison.Ordinal);
    }

    private static bool LooksLikeDefinitiveFailure(string message)
    {
        var normalized = message.ToLowerInvariant();
        return normalized.Contains("❌", StringComparison.Ordinal)
            || normalized.Contains("failed", StringComparison.Ordinal)
            || normalized.Contains("couldn't", StringComparison.Ordinal)
            || normalized.Contains("could not", StringComparison.Ordinal)
            || normalized.Contains("unable", StringComparison.Ordinal)
            || normalized.Contains("not completed", StringComparison.Ordinal)
            || normalized.Contains("error", StringComparison.Ordinal)
            || normalized.Contains("unavailable", StringComparison.Ordinal)
            || normalized.Contains("not found", StringComparison.Ordinal);
    }

    private static bool LooksLikeDefinitiveSuccess(string message)
    {
        var normalized = message.ToLowerInvariant();
        if (normalized.Contains("❌", StringComparison.Ordinal)
            || normalized.Contains("failed", StringComparison.Ordinal)
            || normalized.Contains("unable", StringComparison.Ordinal)
            || normalized.Contains("couldn't", StringComparison.Ordinal)
            || normalized.Contains("could not", StringComparison.Ordinal)
            || normalized.Contains("error", StringComparison.Ordinal))
        {
            return false;
        }

        return normalized.Contains("✅", StringComparison.Ordinal)
            || normalized.Contains("sent", StringComparison.Ordinal)
            || normalized.Contains("completed", StringComparison.Ordinal)
            || normalized.Contains("success", StringComparison.Ordinal)
            || normalized.Contains("message id", StringComparison.Ordinal)
            || normalized.Contains("done", StringComparison.Ordinal);
    }

    private static bool IsMcpPermissionDeniedError(InvalidOperationException exception)
    {
        if (exception == null)
        {
            return false;
        }

        var message = exception.Message;
        return !string.IsNullOrWhiteSpace(message)
            && (message.Contains("MCP permission denied", StringComparison.OrdinalIgnoreCase)
                || message.Contains("could not request interactive approval", StringComparison.OrdinalIgnoreCase)
                || message.Contains("could not request permission from user", StringComparison.OrdinalIgnoreCase));
    }
}
