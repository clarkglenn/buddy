using System.Text.Json;

namespace Buddy.Server.Services.Messaging;

/// <summary>
/// File-based storage for platform user to GitHub token mappings
/// </summary>
public class FileBasedMultiChannelTokenStore : IMultiChannelTokenStore
{
    private readonly string _storagePath;
    private readonly object _lock = new();
    private Dictionary<string, Dictionary<string, string>>? _cachedTokens;

    public FileBasedMultiChannelTokenStore()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _storagePath = Path.Combine(appDataPath, "Buddy", "platform_tokens.json");
        
        // Ensure directory exists
        var directory = Path.GetDirectoryName(_storagePath);
        if (directory != null && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public Task<string?> GetTokenAsync(PlatformUser user, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var tokens = LoadTokens();
            var platformKey = user.Platform.ToString();

            if (tokens.TryGetValue(platformKey, out var platformUsers) &&
                platformUsers.TryGetValue(user.PlatformUserId, out var token))
            {
                return Task.FromResult<string?>(token);
            }

            return Task.FromResult<string?>(null);
        }
    }

    public Task SetTokenAsync(PlatformUser user, string githubToken, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var tokens = LoadTokens();
            var platformKey = user.Platform.ToString();

            if (!tokens.ContainsKey(platformKey))
            {
                tokens[platformKey] = new();
            }

            tokens[platformKey][user.PlatformUserId] = githubToken;
            SaveTokens(tokens);
            _cachedTokens = null; // Invalidate cache
        }

        return Task.CompletedTask;
    }

    public Task<bool> HasTokenAsync(PlatformUser user, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var tokens = LoadTokens();
            var platformKey = user.Platform.ToString();

            return Task.FromResult(
                tokens.TryGetValue(platformKey, out var platformUsers) &&
                platformUsers.ContainsKey(user.PlatformUserId)
            );
        }
    }

    public Task RemoveTokenAsync(PlatformUser user, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var tokens = LoadTokens();
            var platformKey = user.Platform.ToString();

            if (tokens.TryGetValue(platformKey, out var platformUsers))
            {
                platformUsers.Remove(user.PlatformUserId);
                SaveTokens(tokens);
                _cachedTokens = null; // Invalidate cache
            }
        }

        return Task.CompletedTask;
    }

    private Dictionary<string, Dictionary<string, string>> LoadTokens()
    {
        if (_cachedTokens != null)
        {
            return _cachedTokens;
        }

        try
        {
            if (!File.Exists(_storagePath))
            {
                return new();
            }

            var json = File.ReadAllText(_storagePath);
            _cachedTokens = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json) 
                            ?? new();
            return _cachedTokens;
        }
        catch (Exception)
        {
            // If file is corrupted, return empty and let SaveTokens rebuild it
            return new();
        }
    }

    private void SaveTokens(Dictionary<string, Dictionary<string, string>> tokens)
    {
        try
        {
            var json = JsonSerializer.Serialize(tokens, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch (Exception ex)
        {
            // Log error but don't throw - this might be called during request handling
            Console.Error.WriteLine($"Failed to save tokens to {_storagePath}: {ex.Message}");
        }
    }
}
