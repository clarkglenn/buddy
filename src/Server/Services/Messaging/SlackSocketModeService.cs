using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Buddy.Server.Options;

namespace Buddy.Server.Services.Messaging;

/// <summary>
/// Background service that maintains a Socket Mode connection to Slack for receiving events
/// </summary>
public class SlackSocketModeService : BackgroundService
{
    private readonly IOptions<MessagingOptions> _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SlackSocketModeService> _logger;
    private readonly HttpClient _httpClient;
    private ClientWebSocket? _webSocket;
    private string? _pendingReconnectUrl;
    private bool _reconnectRequested;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentInboundEventKeys = new(StringComparer.Ordinal);
    private static readonly TimeSpan InboundEventDedupeWindow = TimeSpan.FromMinutes(2);
    private const string ReplyTsContextKey = "reply_ts";

    public SlackSocketModeService(
        IOptions<MessagingOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<SlackSocketModeService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _options = options;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var slackConfig = _options.Value.Slack;
        if (string.IsNullOrEmpty(slackConfig?.AppLevelToken))
        {
            _logger.LogWarning("Slack Socket Mode is enabled but AppLevelToken is not configured. Service will not start.");
            return;
        }

        if (string.IsNullOrEmpty(slackConfig.BotToken))
        {
            _logger.LogWarning("Slack Socket Mode is enabled but BotToken is not configured. Service will not start.");
            return;
        }

        _logger.LogInformation("Starting Slack Socket Mode service...");

        var retryCount = 0;
        var baseDelay = TimeSpan.FromSeconds(5);
        var maxDelay = TimeSpan.FromMinutes(2);
        var random = new Random();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Get WebSocket URL from Slack (prefer reconnect URL from disconnect envelope)
                var reconnectUrl = _pendingReconnectUrl;
                _pendingReconnectUrl = null;
                var wsUrl = !string.IsNullOrWhiteSpace(reconnectUrl)
                    ? reconnectUrl
                    : await GetWebSocketUrlAsync(slackConfig.AppLevelToken, stoppingToken);
                if (string.IsNullOrEmpty(wsUrl))
                {
                    throw new Exception("Failed to obtain WebSocket URL from Slack");
                }

                if (!string.IsNullOrWhiteSpace(reconnectUrl))
                {
                    _logger.LogInformation("Using reconnect URL provided by Slack");
                }

                _logger.LogInformation("Connecting to Slack Socket Mode at {Url}", wsUrl);

                // Connect WebSocket
                _webSocket = new ClientWebSocket();
                await _webSocket.ConnectAsync(new Uri(wsUrl), stoppingToken);
                _logger.LogInformation("Successfully connected to Slack Socket Mode");
                _reconnectRequested = false;

                // Reset retry count on successful connection
                retryCount = 0;

                // Start receiving messages
                await ReceiveMessagesAsync(stoppingToken);

                if (_webSocket != null)
                {
                    _webSocket.Dispose();
                    _webSocket = null;
                }

                if (!stoppingToken.IsCancellationRequested)
                {
                    if (_reconnectRequested)
                    {
                        _logger.LogInformation("Slack requested reconnect. Reconnecting now...");
                        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    }
                    else
                    {
                        retryCount++;
                        var delay = CalculateReconnectDelay(baseDelay, maxDelay, retryCount, random);
                        _logger.LogWarning("Slack Socket Mode receive loop ended unexpectedly (attempt {RetryCount}). Retrying in {Delay:F1} seconds...", retryCount, delay.TotalSeconds);
                        await Task.Delay(delay, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Slack Socket Mode service is stopping...");
                break;
            }
            catch (Exception ex)
            {
                retryCount++;
                var delay = CalculateReconnectDelay(baseDelay, maxDelay, retryCount, random);
                _logger.LogError(ex, "Error in Slack Socket Mode connection (attempt {RetryCount}). Retrying in {Delay:F1} seconds...", retryCount, delay.TotalSeconds);

                if (_webSocket != null)
                {
                    _webSocket.Dispose();
                    _webSocket = null;
                }

                await Task.Delay(delay, stoppingToken);
            }
        }
    }

    private static TimeSpan CalculateReconnectDelay(TimeSpan baseDelay, TimeSpan maxDelay, int retryCount, Random random)
    {
        var exponent = Math.Min(Math.Max(retryCount - 1, 0), 6);
        var rawSeconds = baseDelay.TotalSeconds * Math.Pow(2, exponent);
        var cappedSeconds = Math.Min(rawSeconds, maxDelay.TotalSeconds);
        var jitterSeconds = random.NextDouble() * 1.5;
        return TimeSpan.FromSeconds(cappedSeconds + jitterSeconds);
    }

    private async Task<string?> GetWebSocketUrlAsync(string appToken, CancellationToken cancellationToken)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://slack.com/api/apps.connections.open");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", appToken);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean())
            {
                if (doc.RootElement.TryGetProperty("url", out var url))
                {
                    return url.GetString();
                }
            }

            var error = doc.RootElement.TryGetProperty("error", out var err) ? err.GetString() : "unknown";
            _logger.LogError("Failed to get WebSocket URL: {Error}", error);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting WebSocket URL from Slack");
            return null;
        }
    }

    private async Task ReceiveMessagesAsync(CancellationToken cancellationToken)
    {
        if (_webSocket == null)
        {
            return;
        }

        var buffer = new byte[1024 * 16];
        var messageBuffer = new StringBuilder();

        while (_webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                WebSocketReceiveResult result;
                messageBuffer.Clear();

                do
                {
                    result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                    
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInformation("Slack requested connection close");
                        await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cancellationToken);
                        return;
                    }

                    messageBuffer.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
                while (!result.EndOfMessage);

                var message = messageBuffer.ToString();
                await ProcessSlackMessageAsync(message, cancellationToken);

                if (_reconnectRequested)
                {
                    _logger.LogDebug("Reconnect requested by Slack; ending current receive loop.");
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (WebSocketException ex)
            {
                _logger.LogError(ex, "WebSocket error during receive");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error receiving message from Slack");
            }
        }
    }

    private async Task ProcessSlackMessageAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;

            // Get envelope type
            if (!root.TryGetProperty("type", out var typeElement))
            {
                return;
            }

            var type = typeElement.GetString();

            switch (type)
            {
                case "hello":
                    _logger.LogInformation("Received hello from Slack Socket Mode");
                    break;

                case "disconnect":
                    var reason = root.TryGetProperty("reason", out var reasonElement)
                        ? reasonElement.GetString()
                        : null;
                    var reconnectUrl = root.TryGetProperty("reconnect_url", out var reconnectUrlElement)
                        ? reconnectUrlElement.GetString()
                        : null;

                    if (!string.IsNullOrWhiteSpace(reconnectUrl))
                    {
                        _pendingReconnectUrl = reconnectUrl;
                    }

                    _reconnectRequested = true;
                    _logger.LogInformation(
                        "Received disconnect from Slack Socket Mode. Reason: {Reason}. ReconnectUrlProvided: {HasReconnectUrl}",
                        string.IsNullOrWhiteSpace(reason) ? "unknown" : reason,
                        !string.IsNullOrWhiteSpace(reconnectUrl));
                    break;

                case "events_api":
                    await HandleEventsApiEnvelopeAsync(root, cancellationToken);
                    break;

                case "slash_commands":
                    await HandleSlashCommandEnvelopeAsync(root, cancellationToken);
                    break;

                case "interactive":
                    // Acknowledge but don't process for now
                    await AcknowledgeEnvelopeAsync(root, cancellationToken);
                    break;

                default:
                    _logger.LogDebug("Received unknown envelope type: {Type}", type);
                    break;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse Slack message: {Message}", message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Slack message");
        }
    }

    private async Task HandleEventsApiEnvelopeAsync(JsonElement envelope, CancellationToken cancellationToken)
    {
        try
        {
            // Get envelope_id for acknowledgment
            string? envelopeId = envelope.TryGetProperty("envelope_id", out var eid) ? eid.GetString() : null;

            // Acknowledge immediately (Slack requirement)
            if (!string.IsNullOrEmpty(envelopeId))
            {
                await AcknowledgeEnvelopeAsync(envelopeId, cancellationToken);
            }

            // Get the event payload
            if (!envelope.TryGetProperty("payload", out var payload))
            {
                return;
            }

            if (!payload.TryGetProperty("event", out var eventElement))
            {
                return;
            }

            // Get event type
            if (!eventElement.TryGetProperty("type", out var eventTypeEl))
            {
                return;
            }

            var eventType = eventTypeEl.GetString();
            var eventId = payload.TryGetProperty("event_id", out var eventIdElement)
                ? eventIdElement.GetString()
                : null;

            var dedupeKey = BuildEventDedupeKey(eventId, eventType, eventElement);
            if (!TryRegisterInboundEvent(dedupeKey))
            {
                _logger.LogDebug("Skipping duplicate Slack event. EventType={EventType}, DedupeKey={DedupeKey}", eventType, dedupeKey);
                return;
            }

            // Process based on event type
            IncomingMessage? incomingMessage = null;

            switch (eventType)
            {
                case "message":
                    incomingMessage = ParseMessageEvent(eventElement);
                    break;

                case "app_mention":
                    incomingMessage = ParseAppMentionEvent(eventElement);
                    break;

                default:
                    _logger.LogDebug("Unhandled event type: {EventType}", eventType);
                    break;
            }

            // Process the message (fire-and-forget to not block)
            if (incomingMessage != null)
            {
                var replyTs = await SendImmediateOnItResponseAsync(incomingMessage, cancellationToken);
                if (!string.IsNullOrWhiteSpace(replyTs))
                {
                    var context = incomingMessage.Context == null
                        ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string>(incomingMessage.Context, StringComparer.OrdinalIgnoreCase);

                    context[ReplyTsContextKey] = replyTs!;
                    incomingMessage = incomingMessage with { Context = context };
                }

                await ProcessIncomingMessageAsync(incomingMessage, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling events_api envelope");
            
            // Acknowledgment already sent, so just log the error
        }
    }

    private async Task HandleSlashCommandEnvelopeAsync(JsonElement envelope, CancellationToken cancellationToken)
    {
        try
        {
            // Acknowledge immediately (Slack requirement for slash commands)
            string? envelopeId = envelope.TryGetProperty("envelope_id", out var eid) ? eid.GetString() : null;
            if (!string.IsNullOrEmpty(envelopeId))
            {
                await AcknowledgeEnvelopeAsync(envelopeId, cancellationToken);
            }

            // Get the payload
            if (!envelope.TryGetProperty("payload", out var payload))
            {
                return;
            }

            // Extract command information
            var userId = payload.TryGetProperty("user_id", out var uid) ? uid.GetString() : null;
            var text = payload.TryGetProperty("text", out var cmdText) ? cmdText.GetString() : null;
            var channelId = payload.TryGetProperty("channel_id", out var ch) ? ch.GetString() : null;
            var triggerId = payload.TryGetProperty("trigger_id", out var trigger) ? trigger.GetString() : null;

            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(text))
            {
                _logger.LogWarning("Slash command missing required fields");
                return;
            }

            var slashDedupeKey = $"slash:{channelId}:{userId}:{triggerId}:{text}";
            if (!TryRegisterInboundEvent(slashDedupeKey))
            {
                _logger.LogDebug("Skipping duplicate slash command. DedupeKey={DedupeKey}", slashDedupeKey);
                return;
            }

            var incomingMessage = new IncomingMessage
            {
                From = new PlatformUser
                {
                    Platform = MessagingPlatform.Slack,
                    PlatformUserId = userId
                },
                Text = text,
                ReceivedAt = DateTime.UtcNow,
                Context = new Dictionary<string, string>
                {
                    { "channel", channelId ?? "" },
                    { "is_slash_command", "true" }
                }
            };

            var replyTs = await SendImmediateOnItResponseAsync(incomingMessage, cancellationToken);
            if (!string.IsNullOrWhiteSpace(replyTs))
            {
                incomingMessage.Context[ReplyTsContextKey] = replyTs!;
            }

            // Process the message without blocking
            await ProcessIncomingMessageAsync(incomingMessage, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling slash command envelope");
            
            // Still acknowledge to prevent retries
            await AcknowledgeEnvelopeAsync(envelope, cancellationToken);
        }
    }

    private IncomingMessage? ParseMessageEvent(JsonElement eventElement)
    {
        // Skip bot messages and subtypes (except for thread replies)
        if (eventElement.TryGetProperty("bot_id", out _))
        {
            return null;
        }

        if (eventElement.TryGetProperty("subtype", out var subtype) && subtype.GetString() != null)
        {
            // Allow thread_broadcast but skip other subtypes
            if (subtype.GetString() != "thread_broadcast")
            {
                return null;
            }
        }

        var userId = eventElement.TryGetProperty("user", out var user) ? user.GetString() : null;
        var text = eventElement.TryGetProperty("text", out var txt) ? txt.GetString() : null;
        var channel = eventElement.TryGetProperty("channel", out var ch) ? ch.GetString() : null;
        var threadTs = eventElement.TryGetProperty("thread_ts", out var thread) ? thread.GetString() : null;
        var messageTs = eventElement.TryGetProperty("ts", out var ts) ? ts.GetString() : null;
        var conversationTs = !string.IsNullOrWhiteSpace(threadTs) ? threadTs : messageTs;

        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(text))
        {
            return null;
        }

        return new IncomingMessage
        {
            From = new PlatformUser
            {
                Platform = MessagingPlatform.Slack,
                PlatformUserId = userId
            },
            Text = text,
            ReceivedAt = DateTime.UtcNow,
            Context = new Dictionary<string, string>
            {
                { "channel", channel ?? "" },
                { "thread_ts", conversationTs ?? "" }
            }
        };
    }

    private IncomingMessage? ParseAppMentionEvent(JsonElement eventElement)
    {
        var userId = eventElement.TryGetProperty("user", out var user) ? user.GetString() : null;
        var text = eventElement.TryGetProperty("text", out var txt) ? txt.GetString() : null;
        var channel = eventElement.TryGetProperty("channel", out var ch) ? ch.GetString() : null;
        var threadTs = eventElement.TryGetProperty("thread_ts", out var thread) ? thread.GetString() : null;
        var messageTs = eventElement.TryGetProperty("ts", out var ts) ? ts.GetString() : null;
        var conversationTs = !string.IsNullOrWhiteSpace(threadTs) ? threadTs : messageTs;

        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(text))
        {
            return null;
        }

        // Remove bot mention from text
        var cleanText = text.Trim();
        if (cleanText.StartsWith("<@"))
        {
            var endIndex = cleanText.IndexOf('>');
            if (endIndex > 0)
            {
                cleanText = cleanText.Substring(endIndex + 1).Trim();
            }
        }

        return new IncomingMessage
        {
            From = new PlatformUser
            {
                Platform = MessagingPlatform.Slack,
                PlatformUserId = userId
            },
            Text = cleanText,
            ReceivedAt = DateTime.UtcNow,
            Context = new Dictionary<string, string>
            {
                { "channel", channel ?? "" },
                { "thread_ts", conversationTs ?? "" }
            }
        };
    }

    private async Task ProcessIncomingMessageAsync(IncomingMessage message, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var messageHandler = scope.ServiceProvider.GetRequiredService<IMessageHandlerService>();
        
        // Fire and forget - don't block the Socket Mode handler
        _ = Task.Run(async () =>
        {
            try
            {
                await messageHandler.HandleMessageAsync(message, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing message from {User}", message.From);
            }
        }, cancellationToken);
    }

    private async Task<string?> SendImmediateOnItResponseAsync(IncomingMessage message, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var providerFactory = scope.ServiceProvider.GetRequiredService<IMessagingProviderFactory>();
            var provider = providerFactory.GetProvider(MessagingPlatform.Slack);

            var sendResult = await provider.SendMessageAsync(new SendMessageParams
            {
                User = message.From,
                Message = "On it",
                Context = message.Context
            }, cancellationToken);

            if (sendResult.Success)
            {
                return sendResult.MessageTs;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending immediate Slack acknowledgment response");
            return null;
        }
    }

    private bool TryRegisterInboundEvent(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return true;
        }

        var now = DateTimeOffset.UtcNow;
        CleanupInboundEventCache(now);

        if (_recentInboundEventKeys.TryGetValue(key, out var seenAt) && now - seenAt < InboundEventDedupeWindow)
        {
            return false;
        }

        _recentInboundEventKeys[key] = now;
        return true;
    }

    private void CleanupInboundEventCache(DateTimeOffset now)
    {
        if (_recentInboundEventKeys.Count < 512)
        {
            return;
        }

        foreach (var entry in _recentInboundEventKeys)
        {
            if (now - entry.Value >= InboundEventDedupeWindow)
            {
                _recentInboundEventKeys.TryRemove(entry.Key, out _);
            }
        }
    }

    private static string? BuildEventDedupeKey(string? eventId, string? eventType, JsonElement eventElement)
    {
        if (!string.IsNullOrWhiteSpace(eventId))
        {
            return $"event:{eventId}";
        }

        var channel = eventElement.TryGetProperty("channel", out var channelElement)
            ? channelElement.GetString()
            : null;
        var ts = eventElement.TryGetProperty("ts", out var tsElement)
            ? tsElement.GetString()
            : null;
        var user = eventElement.TryGetProperty("user", out var userElement)
            ? userElement.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(ts))
        {
            return null;
        }

        return $"{eventType}:{channel}:{ts}:{user}";
    }

    private async Task AcknowledgeEnvelopeAsync(JsonElement envelope, CancellationToken cancellationToken)
    {
        if (envelope.TryGetProperty("envelope_id", out var envelopeId))
        {
            var id = envelopeId.GetString();
            if (!string.IsNullOrEmpty(id))
            {
                await AcknowledgeEnvelopeAsync(id, cancellationToken);
            }
        }
    }

    private async Task AcknowledgeEnvelopeAsync(string envelopeId, CancellationToken cancellationToken)
    {
        if (_webSocket == null || _webSocket.State != WebSocketState.Open)
        {
            return;
        }

        try
        {
            var ack = JsonSerializer.Serialize(new { envelope_id = envelopeId });
            var bytes = Encoding.UTF8.GetBytes(ack);
            await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
            _logger.LogDebug("Acknowledged envelope {EnvelopeId}", envelopeId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error acknowledging envelope {EnvelopeId}", envelopeId);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Slack Socket Mode service...");

        if (_webSocket != null)
        {
            try
            {
                if (_webSocket.State == WebSocketState.Open)
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Service stopping", cancellationToken);
                }
                _webSocket.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error closing WebSocket");
            }
        }

        _httpClient.Dispose();
        await base.StopAsync(cancellationToken);
    }
}
