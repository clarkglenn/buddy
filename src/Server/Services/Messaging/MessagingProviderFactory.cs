namespace Buddy.Server.Services.Messaging;

/// <summary>
/// Factory for getting messaging providers
/// </summary>
public class MessagingProviderFactory : IMessagingProviderFactory
{
    private readonly Dictionary<MessagingPlatform, IMessagingProvider> _providers;

    public MessagingProviderFactory(IEnumerable<IMessagingProvider> providers)
    {
        _providers = providers.ToDictionary(p => p.Platform);
    }

    public IMessagingProvider GetProvider(MessagingPlatform platform)
    {
        if (_providers.TryGetValue(platform, out var provider))
        {
            return provider;
        }

        throw new InvalidOperationException($"Messaging provider for {platform} is not registered");
    }

    public IEnumerable<IMessagingProvider> GetAllProviders()
    {
        return _providers.Values;
    }
}
