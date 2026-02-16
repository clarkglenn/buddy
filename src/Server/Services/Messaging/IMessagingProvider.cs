namespace Buddy.Server.Services.Messaging;

/// <summary>
/// Handles sending and receiving messages on a specific messaging platform
/// </summary>
public interface IMessagingProvider
{
    MessagingPlatform Platform { get; }

    /// <summary>
    /// Send a message to a platform user
    /// </summary>
    Task SendMessageAsync(SendMessageParams parameters, CancellationToken cancellationToken = default);
}
