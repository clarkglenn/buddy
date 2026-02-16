namespace Server.Services;

public interface IGitHubTokenStore
{
    ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken = default);
    ValueTask SetTokenAsync(string token, CancellationToken cancellationToken = default);
    ValueTask ClearAsync(CancellationToken cancellationToken = default);
}

public sealed class InMemoryGitHubTokenStore : IGitHubTokenStore
{
    private readonly object _gate = new();
    private string? _token;

    public ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_token);
        }
    }

    public ValueTask SetTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return ValueTask.CompletedTask;
        }

        lock (_gate)
        {
            _token = token;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _token = null;
        }

        return ValueTask.CompletedTask;
    }
}

public sealed class FileBasedGitHubTokenStore : IGitHubTokenStore
{
    private readonly string _tokenFilePath;
    private readonly object _gate = new();
    private string? _cachedToken;
    private bool _cacheLoaded;

    public FileBasedGitHubTokenStore()
    {
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Buddy");
        
        Directory.CreateDirectory(appDataDir);
        _tokenFilePath = Path.Combine(appDataDir, "github_token.txt");
    }

    public ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_cacheLoaded)
            {
                try
                {
                    if (File.Exists(_tokenFilePath))
                    {
                        _cachedToken = File.ReadAllText(_tokenFilePath).Trim();
                        _cacheLoaded = true;
                        return new ValueTask<string?>(
                            string.IsNullOrWhiteSpace(_cachedToken) ? (string?)null : _cachedToken);
                    }
                }
                catch
                {
                    // If error reading file, treat as no token
                }
                
                _cacheLoaded = true;
            }

            return new ValueTask<string?>(_cachedToken);
        }
    }

    public ValueTask SetTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return ValueTask.CompletedTask;
        }

        lock (_gate)
        {
            try
            {
                File.WriteAllText(_tokenFilePath, token);
                _cachedToken = token;
                _cacheLoaded = true;
            }
            catch
            {
                // If error writing, at least update cache
                _cachedToken = token;
                _cacheLoaded = true;
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_tokenFilePath))
                {
                    File.Delete(_tokenFilePath);
                }
            }
            catch
            {
                // Ignore errors on deletion
            }

            _cachedToken = null;
            _cacheLoaded = true;
        }

        return ValueTask.CompletedTask;
    }
}
