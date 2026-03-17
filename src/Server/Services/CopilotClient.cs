using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Buddy.Server.Services.Messaging;
using Microsoft.Extensions.Options;
using Server.Options;

namespace Server.Services;

public sealed class CopilotClient
{
    private readonly CopilotOptions _options;
    private readonly ILogger<CopilotClient> _logger;
    private readonly ICopilotSessionStore _sessionStore;
    private readonly ICopilotAcpHost _acpHost;

    public CopilotClient(
        IOptions<CopilotOptions> options,
        ILogger<CopilotClient> logger,
        ICopilotSessionStore sessionStore,
        ICopilotAcpHost acpHost)
    {
        _options = options.Value;
        _logger = logger;
        _sessionStore = sessionStore;
        _acpHost = acpHost;
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
            _logger.LogInformation(
                "Copilot request started. SessionId={SessionId}, Mode=ephemeral, Transport=acp",
                ephemeralEntry.CliSessionId);

            return await StreamWithSessionAsync(ephemeralEntry, prompt, onDelta, cancellationToken);
        }

        var entry = await _sessionStore.GetOrCreateAsync(
            conversationKey,
            static _ => Task.FromResult(new CopilotSessionEntry()),
            cancellationToken);

        _logger.LogInformation(
            "Copilot request queued. SessionId={SessionId}, ConversationKey={ConversationKey}, Transport=acp",
            entry.CliSessionId,
            conversationKey);

        var gateWaitSw = Stopwatch.StartNew();
        await entry.Gate.WaitAsync(cancellationToken);
        gateWaitSw.Stop();

        _logger.LogInformation(
            "Copilot request acquired session gate. SessionId={SessionId}, WaitMs={WaitMs}",
            entry.CliSessionId,
            gateWaitSw.ElapsedMilliseconds);

        var requestSw = Stopwatch.StartNew();
        try
        {
            var response = await StreamWithSessionAsync(entry, prompt, onDelta, cancellationToken);

            requestSw.Stop();
            _logger.LogInformation(
                "Copilot request completed. SessionId={SessionId}, DurationMs={DurationMs}, ResponseChars={ResponseChars}",
                entry.CliSessionId,
                requestSw.ElapsedMilliseconds,
                response.Length);

            if (entry.IsFaulted)
            {
                _logger.LogWarning(
                    "Copilot session fault detected; removing session. SessionId={SessionId}, ConversationKey={ConversationKey}",
                    entry.CliSessionId,
                    conversationKey);

                await _sessionStore.RemoveAsync(conversationKey, cancellationToken);
            }

            return response;
        }
        catch (Exception ex)
        {
            requestSw.Stop();
            _logger.LogError(
                ex,
                "Copilot request failed. SessionId={SessionId}, DurationMs={DurationMs}, ConversationKey={ConversationKey}",
                entry.CliSessionId,
                requestSw.ElapsedMilliseconds,
                conversationKey);
            throw;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private string BuildPrompt(string prompt)
    {
        return $"""
Formatting requirements for your final answer:
- Use plain text.
- Use line breaks between sections.
- When summarizing items (emails, messages, files, results), use a bullet list with one item per line.
- Do not include tool traces, internal logs, or execution metadata.

User request:
{prompt}
""";
    }

    private string? BuildConversationKey(Dictionary<string, string>? context, string? conversationUserKey)
    {
        if (context == null)
        {
            return null;
        }

        if (!context.TryGetValue(MessagingContextKeys.Channel, out var channel) || string.IsNullOrWhiteSpace(channel))
        {
            return null;
        }

        if (context.TryGetValue(MessagingContextKeys.ThreadTs, out var threadTs) && !string.IsNullOrWhiteSpace(threadTs))
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
        CancellationToken cancellationToken)
    {
        var buffer = new StringBuilder();
        var requestState = new CopilotRequestState(buffer, onDelta);

        try
        {
            var enrichedPrompt = BuildPrompt(prompt);

            using var promptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (_options.Cli.ResponseTimeoutSeconds > 0)
            {
                promptCts.CancelAfter(TimeSpan.FromSeconds(_options.Cli.ResponseTimeoutSeconds));
            }

            var acpSessionId = await EnsureAcpSessionAsync(entry, promptCts.Token);
            var promptResult = await _acpHost.PromptSessionAsync(
                acpSessionId,
                enrichedPrompt,
                (update, ct) => HandleAcpUpdateAsync(update, requestState, ct),
                promptCts.Token);

            if (!string.Equals(promptResult.StopReason, "end_turn", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "Copilot ACP prompt ended with stop reason {StopReason}. SessionId={SessionId}",
                    promptResult.StopReason,
                    acpSessionId);
            }

            return buffer.ToString();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Copilot response timed out after {_options.Cli.ResponseTimeoutSeconds} seconds.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Copilot CLI streaming");
            entry.MarkFaulted();
            throw;
        }
        finally
        {
            entry.Touch();
        }
    }

    private async Task<string> EnsureAcpSessionAsync(CopilotSessionEntry entry, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(entry.AcpSessionId) && entry.AcpGeneration == _acpHost.Generation)
        {
            return entry.AcpSessionId;
        }

        var sessionId = await _acpHost.CreateSessionAsync(Environment.CurrentDirectory, cancellationToken);
        entry.AcpSessionId = sessionId;
        entry.AcpGeneration = _acpHost.Generation;
        return sessionId;
    }

    private async Task HandleAcpUpdateAsync(
        CopilotAcpUpdate update,
        CopilotRequestState requestState,
        CancellationToken cancellationToken)
    {
        if (update.ToolUsed)
        {
            requestState.ToolUsed = true;
        }

        if (string.IsNullOrWhiteSpace(update.Content))
        {
            return;
        }

        if (update.IsThinking)
        {
            await requestState.OnDelta($"[THINKING] {update.Content}", cancellationToken);
            return;
        }
        requestState.Buffer.Append(update.Content);
        await requestState.OnDelta(update.Content, cancellationToken);
    }
}
