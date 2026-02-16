using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
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
        const int maxRetries = 10;
        var baseDelay = TimeSpan.FromSeconds(5);

        while (!stoppingToken.IsCancellationRequested && retryCount < maxRetries)
        {
            try
            {
                // Get WebSocket URL from Slack
                var wsUrl = await GetWebSocketUrlAsync(slackConfig.AppLevelToken, stoppingToken);
                if (string.IsNullOrEmpty(wsUrl))
                {
                    throw new Exception("Failed to obtain WebSocket URL from Slack");
                }

                _logger.LogInformation("Connecting to Slack Socket Mode at {Url}", wsUrl);

                // Connect WebSocket
                _webSocket = new ClientWebSocket();
                await _webSocket.ConnectAsync(new Uri(wsUrl), stoppingToken);
                _logger.LogInformation("Successfully connected to Slack Socket Mode");

                // Reset retry count on successful connection
                retryCount = 0;

                // Start receiving messages
                await ReceiveMessagesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Slack Socket Mode service is stopping...");
                break;
            }
            catch (Exception ex)
            {
                retryCount++;
                _logger.LogError(ex, "Error in Slack Socket Mode connection (attempt {RetryCount}/{MaxRetries})", retryCount, maxRetries);

                if (_webSocket != null)
                {
                    _webSocket.Dispose();
                    _webSocket = null;
                }

                if (retryCount < maxRetries)
                {
                    var delay = TimeSpan.FromSeconds(baseDelay.TotalSeconds * Math.Pow(2, Math.Min(retryCount - 1, 5)));
                    _logger.LogInformation("Retrying connection in {Delay:F1} seconds...", delay.TotalSeconds);
                    await Task.Delay(delay, stoppingToken);
                }
            }
        }

        if (retryCount >= maxRetries)
        {
            _logger.LogError("Failed to establish Slack Socket Mode connection after {MaxRetries} attempts", maxRetries);
        }
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
                    _logger.LogWarning("Received disconnect from Slack Socket Mode");
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

            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(text))
            {
                _logger.LogWarning("Slash command missing required fields");
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
