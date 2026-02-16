namespace Buddy.Server.Services.Messaging;

/// <summary>
/// Handles incoming messages from any platform
/// </summary>
public interface IMessageHandlerService
{
    /// <summary>
    /// Process an incoming message (could be a link command or a prompt)
    /// </summary>
    Task HandleMessageAsync(IncomingMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// Service for routing messages to appropriate messaging providers
/// </summary>
public interface IMessagingProviderFactory
{
    IMessagingProvider GetProvider(MessagingPlatform platform);
    IEnumerable<IMessagingProvider> GetAllProviders();
}
