using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Buddy.Server.Options;

namespace Buddy.Server.Services.Messaging;

/// <summary>
/// Slack messaging provider
/// </summary>
public class SlackProvider : IMessagingProvider
{
    public MessagingPlatform Platform => MessagingPlatform.Slack;

    private const string ChannelContextKey = "channel";
    private const string ThreadContextKey = "thread_ts";
    private const string UpdateTsContextKey = "update_ts";

    private readonly HttpClient _httpClient;
    private readonly IOptions<MessagingOptions> _options;
    private readonly ILogger<SlackProvider> _logger;

    public SlackProvider(HttpClient httpClient, IOptions<MessagingOptions> options, ILogger<SlackProvider> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
    }

    public async Task<MessageSendResult> SendMessageAsync(SendMessageParams parameters, CancellationToken cancellationToken = default)
    {
        var slackConfig = _options.Value.Slack;
        if (string.IsNullOrEmpty(slackConfig?.BotToken))
        {
            _logger.LogWarning("Slack configuration is missing. Cannot send message.");
            return new MessageSendResult { Success = false, Error = "missing_bot_token" };
        }

        try
        {
            var channelId = ResolveChannelId(parameters);
            if (string.IsNullOrWhiteSpace(channelId))
            {
                _logger.LogDebug("No channel ID in context, attempting to open direct message with user {UserId}", parameters.User.PlatformUserId);
                channelId = await OpenDirectMessageChannelAsync(slackConfig.BotToken, parameters.User.PlatformUserId, cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(channelId))
            {
                _logger.LogError("Slack channel ID could not be resolved for user {UserId}. Unable to send message.", parameters.User.PlatformUserId);
                return new MessageSendResult { Success = false, Error = "missing_channel" };
            }

            var updateTs = ResolveUpdateTs(parameters);
            var isUpdate = !string.IsNullOrWhiteSpace(updateTs);
            _logger.LogDebug("Sending Slack {Mode} to channel {ChannelId}", isUpdate ? "update" : "message", channelId);

            var url = isUpdate
                ? "https://slack.com/api/chat.update"
                : "https://slack.com/api/chat.postMessage";

            var payload = new Dictionary<string, object>
            {
                ["channel"] = channelId,
                ["text"] = parameters.Message
            };

            if (parameters.Style == MessageStyle.Thinking)
            {
                var thinkingText = $":hourglass_flowing_sand: {parameters.Message}";
                payload["blocks"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["type"] = "context",
                        ["elements"] = new object[]
                        {
                            new Dictionary<string, object>
                            {
                                ["type"] = "mrkdwn",
                                ["text"] = thinkingText
                            }
                        }
                    }
                };
            }

            if (isUpdate)
            {
                payload["ts"] = updateTs!;
            }
            else
            {
                var threadTs = ResolveThreadTs(parameters);
                if (!string.IsNullOrWhiteSpace(threadTs))
                {
                    payload["thread_ts"] = threadTs;
                }
            }

            var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"
            );

            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = content
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", slackConfig.BotToken);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to send Slack message: HTTP {StatusCode} - {Error}", response.StatusCode, responseBody);
                return new MessageSendResult
                {
                    Success = false,
                    ChannelId = channelId,
                    Error = $"http_{(int)response.StatusCode}"
                };
            }

            // Check Slack API response
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean())
            {
                _logger.LogDebug("Successfully sent Slack message to channel {ChannelId}", channelId);
                var messageTs = TryGetMessageTimestamp(doc.RootElement);
                return new MessageSendResult
                {
                    Success = true,
                    ChannelId = channelId,
                    MessageTs = messageTs
                };
            }

            var errorMsg = doc.RootElement.TryGetProperty("error", out var errorEl) ? errorEl.GetString() : "unknown_error";
            _logger.LogError("Slack API error: {Error}", errorMsg);
            return new MessageSendResult
            {
                Success = false,
                ChannelId = channelId,
                Error = errorMsg
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending Slack message to {UserId}", parameters.User.PlatformUserId);
            return new MessageSendResult
            {
                Success = false,
                Error = ex.Message
            };
        }
    }

    private static string? ResolveChannelId(SendMessageParams parameters)
    {
        if (parameters.Context == null)
        {
            return null;
        }

        return parameters.Context.TryGetValue(ChannelContextKey, out var channelId)
            ? channelId
            : null;
    }

    private static string? ResolveThreadTs(SendMessageParams parameters)
    {
        if (parameters.Context == null)
        {
            return null;
        }

        return parameters.Context.TryGetValue(ThreadContextKey, out var threadTs) && !string.IsNullOrWhiteSpace(threadTs)
            ? threadTs
            : null;
    }

    private static string? ResolveUpdateTs(SendMessageParams parameters)
    {
        if (parameters.Context == null)
        {
            return null;
        }

        return parameters.Context.TryGetValue(UpdateTsContextKey, out var ts) && !string.IsNullOrWhiteSpace(ts)
            ? ts
            : null;
    }

    private static string? TryGetMessageTimestamp(JsonElement root)
    {
        if (root.TryGetProperty("ts", out var tsElement) && tsElement.ValueKind == JsonValueKind.String)
        {
            return tsElement.GetString();
        }

        if (root.TryGetProperty("message", out var messageElement) &&
            messageElement.ValueKind == JsonValueKind.Object &&
            messageElement.TryGetProperty("ts", out var nestedTs) &&
            nestedTs.ValueKind == JsonValueKind.String)
        {
            return nestedTs.GetString();
        }

        return null;
    }

    private async Task<string?> OpenDirectMessageChannelAsync(string botToken, string userId, CancellationToken cancellationToken)
    {
        try
        {
            var url = "https://slack.com/api/conversations.open";
            var payload = new { users = userId };

            var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"
            );

            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = content
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", botToken);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Failed to open Slack DM channel: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean())
            {
                if (doc.RootElement.TryGetProperty("channel", out var channel) &&
                    channel.TryGetProperty("id", out var channelId))
                {
                    return channelId.GetString();
                }
            }

            var error = doc.RootElement.TryGetProperty("error", out var errorEl) ? errorEl.GetString() : "unknown_error";
            _logger.LogError("Slack DM channel open failed: {Error}", error);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error opening Slack DM channel for user {UserId}", userId);
            return null;
        }
    }
}
