using CopilotClient = global::Server.Services.CopilotClient;
using IGitHubTokenStore = global::Server.Services.IGitHubTokenStore;

namespace Buddy.Server.Services.Messaging;

/// <summary>
/// Processes incoming messages and routes them for authentication or prompt handling
/// </summary>
public class MessageHandlerService : IMessageHandlerService
{
    private readonly IGitHubTokenStore _serverTokenStore;
    private readonly IMultiChannelTokenStore _tokenStore;
    private readonly IMessagingProviderFactory _providerFactory;
    private readonly CopilotClient _copilotClient;

    public MessageHandlerService(
        IGitHubTokenStore serverTokenStore,
        IMultiChannelTokenStore tokenStore,
        IMessagingProviderFactory providerFactory,
        CopilotClient copilotClient)
    {
        _serverTokenStore = serverTokenStore;
        _tokenStore = tokenStore;
        _providerFactory = providerFactory;
        _copilotClient = copilotClient;
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
        var status = await GetServerAuthStatusAsync(cancellationToken);
        var helpText = $@"🤖 **Buddy Copilot Help**

Commands:
• `/help` - Show this help message

Simply send any message to interact with Copilot!

Status:
{status}";

        await SendMessageAsync(message.From, helpText, cancellationToken, message.Context);
    }

    private async Task HandlePromptAsync(IncomingMessage message, CancellationToken cancellationToken)
    {
        var token = await _serverTokenStore.GetTokenAsync(cancellationToken);
        token ??= await _tokenStore.GetTokenAsync(message.From, cancellationToken);

        if (token == null)
        {
            var status = await GetServerAuthStatusAsync(cancellationToken);
            await SendMessageAsync(message.From, status, cancellationToken, message.Context);
            return;
        }

        try
        {
            var resultBuffer = new List<string>();
            const int batchSize = 5;
            var hasSentAnswerChunk = false;
            const string thinkingPrefix = "[THINKING]";
            var lastThinkingSentAt = (DateTimeOffset?)null;
            var thinkingThrottle = TimeSpan.FromSeconds(2);
            const string thinkingMessage = "thinking...";

            // Stream the response
            var response = await _copilotClient.StreamCopilotResponseAsync(
                token,
                message.Text,
                async (delta, ct) =>
                {
                    if (delta.StartsWith(thinkingPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        if (hasSentAnswerChunk)
                        {
                            return;
                        }

                        var now = DateTimeOffset.UtcNow;
                        if (lastThinkingSentAt == null || now - lastThinkingSentAt >= thinkingThrottle)
                        {
                            lastThinkingSentAt = now;
                            await SendMessageAsync(message.From, thinkingMessage, ct, message.Context);
                        }

                        return;
                    }

                    hasSentAnswerChunk = true;

                    resultBuffer.Add(delta);

                    // Send result in batches
                    if (resultBuffer.Count >= batchSize && resultBuffer.Sum(s => s.Length) > 1000)
                    {
                        var resultMsg = string.Join("", resultBuffer);
                        await SendMessageAsync(message.From, resultMsg, cancellationToken, message.Context);
                        resultBuffer.Clear();
                    }
                },
                cancellationToken,
                message.Context,
                message.From.ToString()
            );

            // Send result in chunks if needed (WhatsApp has size limit)
            if (resultBuffer.Count > 0)
            {
                var fullResult = string.Join("", resultBuffer);
                foreach (var chunk in ChunkMessage(fullResult, 2000))
                {
                    await SendMessageAsync(message.From, chunk, cancellationToken, message.Context);
                }
            }

        }
        catch (Exception ex)
        {
            await SendMessageAsync(message.From, $"❌ An error occurred: {ex.Message}", cancellationToken, message.Context);
        }
    }

    private async Task SendMessageAsync(PlatformUser user, string message, CancellationToken cancellationToken, Dictionary<string, string>? context)
    {
        var provider = _providerFactory.GetProvider(user.Platform);
        var parameters = new SendMessageParams
        {
            User = user,
            Message = message,
            Context = context
        };

        await provider.SendMessageAsync(parameters, cancellationToken);
    }

    private async Task<string> GetServerAuthStatusAsync(CancellationToken cancellationToken)
    {
        var token = await _serverTokenStore.GetTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return "⚠️ Copilot SDK is not authenticated. Ask an admin to authenticate the server at /api/auth/admin/login.";
        }

        return "✅ Copilot SDK is authenticated and ready to use.";
    }

    private static IEnumerable<string> ChunkMessage(string message, int chunkSize)
    {
        for (int i = 0; i < message.Length; i += chunkSize)
        {
            yield return message.Substring(i, Math.Min(chunkSize, message.Length - i));
        }
    }
}
