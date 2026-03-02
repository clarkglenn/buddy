using Buddy.Server.Services.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Server.Options;

namespace Server.Services;

public sealed class CopilotWarmupService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<CopilotOptions> _options;
    private readonly ILogger<CopilotWarmupService> _logger;

    private const string WarmupChannel = "__startup_warmup__";

    public CopilotWarmupService(
        IServiceScopeFactory scopeFactory,
        IOptions<CopilotOptions> options,
        ILogger<CopilotWarmupService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var cliOptions = _options.Value.Cli;
        if (!cliOptions.WarmupOnStartup)
        {
            _logger.LogInformation("Copilot startup warmup is disabled.");
            return;
        }

        var warmupCount = Math.Max(1, cliOptions.WarmupSessionCount);
        var timeoutSeconds = Math.Max(5, cliOptions.WarmupTimeoutSeconds);

        _logger.LogInformation(
            "Starting Copilot CLI warmup. SessionCount={SessionCount}, TimeoutSeconds={TimeoutSeconds}, ReusePerSession={ReusePerSession}",
            warmupCount,
            timeoutSeconds,
            cliOptions.ReuseProcessPerSession);

        using var scope = _scopeFactory.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<CopilotClient>();
        var sessionStore = scope.ServiceProvider.GetRequiredService<ICopilotSessionStore>();

        for (var i = 0; i < warmupCount; i++)
        {
            var warmupThread = $"startup-{i}";
            var conversationKey = $"slack:{WarmupChannel}:{warmupThread}";

            var context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [MessagingContextKeys.Channel] = WarmupChannel,
                [MessagingContextKeys.ThreadTs] = warmupThread
            };

            using var warmupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            warmupCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            try
            {
                var response = await client.StreamCopilotResponseAsync(
                    token: string.Empty,
                    prompt: "Reply with exactly: WARMUP_OK",
                    onDelta: static (_, _) => Task.CompletedTask,
                    cancellationToken: warmupCts.Token,
                    context: context,
                    conversationUserKey: "startup-warmup");

                _logger.LogInformation(
                    "Copilot warmup session {Index}/{Total} completed. ResponseLength={Length}",
                    i + 1,
                    warmupCount,
                    response?.Length ?? 0);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Copilot warmup session {Index}/{Total} failed.", i + 1, warmupCount);
            }
            finally
            {
                await sessionStore.RemoveAsync(conversationKey, cancellationToken);
            }
        }

        _logger.LogInformation("Copilot CLI warmup finished.");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
