namespace Buddy.Server.Services.Messaging;

/// <summary>
/// Stores mappings between platform users and their GitHub tokens
/// </summary>
public interface IMultiChannelTokenStore
{
    /// <summary>
    /// Get the GitHub token for a platform user
    /// </summary>
    Task<string?> GetTokenAsync(PlatformUser user, CancellationToken cancellationToken = default);

    /// <summary>
    /// Store a mapping between platform user and GitHub token
    /// </summary>
    Task SetTokenAsync(PlatformUser user, string githubToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a platform user has a token
    /// </summary>
    Task<bool> HasTokenAsync(PlatformUser user, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove a mapping
    /// </summary>
    Task RemoveTokenAsync(PlatformUser user, CancellationToken cancellationToken = default);
}
