using System.Text;
using Microsoft.Extensions.Options;
using Server.Options;

namespace Server.Services;

public interface ICopilotSessionStore
{
    Task<CopilotSessionEntry> GetOrCreateAsync(
        string key,
        Func<CancellationToken, Task<CopilotSessionEntry>> factory,
        CancellationToken cancellationToken);

    Task RemoveAsync(string key, CancellationToken cancellationToken);
}

public sealed class CopilotSessionStore : ICopilotSessionStore, IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, CopilotSessionEntry> _sessions = new(StringComparer.Ordinal);
    private readonly TimeSpan _ttl;
    private readonly ILogger<CopilotSessionStore> _logger;

    public CopilotSessionStore(IOptions<CopilotOptions> options, ILogger<CopilotSessionStore> logger)
    {
        var ttlMinutes = Math.Max(1, options.Value.SessionTtlMinutes);
        _ttl = TimeSpan.FromMinutes(ttlMinutes);
        _logger = logger;
    }

    public async Task<CopilotSessionEntry> GetOrCreateAsync(
        string key,
        Func<CancellationToken, Task<CopilotSessionEntry>> factory,
        CancellationToken cancellationToken)
    {
        CopilotSessionEntry? expired = null;
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_sessions.TryGetValue(key, out var existing))
            {
                if (!existing.IsExpired(_ttl))
                {
                    existing.Touch();
                    return existing;
                }

                expired = existing;
                _sessions.Remove(key);
            }
        }
        finally
        {
            _lock.Release();
        }

        if (expired != null)
        {
            await DisposeEntryAsync(expired);
        }

        var created = await factory(cancellationToken);
        created.Touch();

        await _lock.WaitAsync(cancellationToken);
        try
        {
            _sessions[key] = created;
        }
        finally
        {
            _lock.Release();
        }

        return created;
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        CopilotSessionEntry? removed = null;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_sessions.TryGetValue(key, out var entry))
            {
                removed = entry;
                _sessions.Remove(key);
            }
        }
        finally
        {
            _lock.Release();
        }

        if (removed != null)
        {
            await DisposeEntryAsync(removed);
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<CopilotSessionEntry> entries;

        await _lock.WaitAsync();
        try
        {
            entries = _sessions.Values.ToList();
            _sessions.Clear();
        }
        finally
        {
            _lock.Release();
        }

        foreach (var entry in entries)
        {
            await DisposeEntryAsync(entry);
        }
    }

    private async Task DisposeEntryAsync(CopilotSessionEntry entry)
    {
        try
        {
            await entry.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to dispose Copilot session.");
        }
    }
}

public sealed class CopilotSessionEntry : IAsyncDisposable
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public DateTime LastUsedUtc { get; private set; }
    public bool IsFaulted { get; private set; }
    public CopilotRequestState? CurrentRequest { get; set; }
    public IReadOnlyList<CopilotConversationTurn> ConversationHistory => _conversationHistory;

    private readonly List<CopilotConversationTurn> _conversationHistory = [];

    public CopilotSessionEntry()
    {
        LastUsedUtc = DateTime.UtcNow;
    }

    public void Touch()
    {
        LastUsedUtc = DateTime.UtcNow;
    }

    public bool IsExpired(TimeSpan ttl)
    {
        return DateTime.UtcNow - LastUsedUtc > ttl;
    }

    public void MarkFaulted()
    {
        IsFaulted = true;
    }

    public void AddTurn(string userPrompt, string assistantResponse, int maxConversationTurns)
    {
        _conversationHistory.Add(new CopilotConversationTurn(userPrompt, assistantResponse));

        var maxTurns = Math.Max(1, maxConversationTurns);
        var overflow = _conversationHistory.Count - maxTurns;
        if (overflow <= 0)
        {
            return;
        }

        _conversationHistory.RemoveRange(0, overflow);
    }

    public async ValueTask DisposeAsync()
    {
        await Task.CompletedTask;
    }
}

public sealed record CopilotConversationTurn(string UserPrompt, string AssistantResponse);

public sealed class CopilotRequestState
{
    public StringBuilder Buffer { get; }
    public Func<string, CancellationToken, Task> OnDelta { get; }
    public bool ToolUsed { get; set; }

    public CopilotRequestState(StringBuilder buffer, Func<string, CancellationToken, Task> onDelta)
    {
        Buffer = buffer;
        OnDelta = onDelta;
    }
}
