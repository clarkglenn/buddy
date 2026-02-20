namespace Buddy.Server.Services.Messaging;

/// <summary>
/// Uniquely identifies a user on a specific platform
/// </summary>
public record PlatformUser
{
    public required MessagingPlatform Platform { get; init; }
    public required string PlatformUserId { get; init; }

    public override string ToString() => $"{Platform}:{PlatformUserId}";
}

/// <summary>
/// Parameters for sending a message to a platform user
/// </summary>
public record SendMessageParams
{
    public required PlatformUser User { get; init; }
    public required string Message { get; init; }
    /// <summary>
    /// Optional context, e.g., thread ID, reaction type
    /// </summary>
    public Dictionary<string, string>? Context { get; init; }
}

/// <summary>
/// Result metadata for message send/update operations
/// </summary>
public record MessageSendResult
{
    public bool Success { get; init; }
    public string? ChannelId { get; init; }
    public string? MessageTs { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Represents an incoming message from a platform
/// </summary>
public record IncomingMessage
{
    public required PlatformUser From { get; init; }
    public required string Text { get; init; }
    public DateTime ReceivedAt { get; init; } = DateTime.UtcNow;
    /// <summary>
    /// Optional context, e.g., thread ID, channel ID
    /// </summary>
    public Dictionary<string, string>? Context { get; init; }
}

