using Microsoft.Extensions.Options;
using Buddy.Server.Options;
using Server.Services;

namespace Buddy.Server.Services.Messaging;

public sealed class McpAvailabilityAnnouncementService : IHostedService
{
    private const string StartupUserId = "buddy-startup";
    private readonly IMcpServerResolver _mcpServerResolver;
    private readonly IMessagingProviderFactory _providerFactory;
    private readonly IOptions<MessagingOptions> _options;
    private readonly ILogger<McpAvailabilityAnnouncementService> _logger;

    public McpAvailabilityAnnouncementService(
        IMcpServerResolver mcpServerResolver,
        IMessagingProviderFactory providerFactory,
        IOptions<MessagingOptions> options,
        ILogger<McpAvailabilityAnnouncementService> logger)
    {
        _mcpServerResolver = mcpServerResolver;
        _providerFactory = providerFactory;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var slackOptions = _options.Value.Slack;
            if (slackOptions == null || string.IsNullOrWhiteSpace(slackOptions.BotToken))
            {
                _logger.LogInformation("Skipping MCP availability announcement: Slack bot token is not configured.");
                return;
            }

            var channel = string.IsNullOrWhiteSpace(slackOptions.StartupAnnouncementChannel)
                ? "all-buddy"
                : slackOptions.StartupAnnouncementChannel;

            var resolution = _mcpServerResolver.Resolve();
            var serverNames = resolution.Servers.Keys
                .OrderBy(static server => server, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var message = serverNames.Length == 0
                ? "Buddy startup: no MCP servers resolved for Copilot sessions."
                : $"Buddy startup: MCP servers available ({serverNames.Length}): {string.Join(", ", serverNames)}";

            var provider = _providerFactory.GetProvider(MessagingPlatform.Slack);
            await provider.SendMessageAsync(new SendMessageParams
            {
                User = new PlatformUser
                {
                    Platform = MessagingPlatform.Slack,
                    PlatformUserId = StartupUserId
                },
                Message = message,
                Context = new Dictionary<string, string>
                {
                    ["channel"] = channel
                }
            }, cancellationToken);

            _logger.LogInformation("Posted MCP availability announcement to Slack channel {Channel}.", channel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to post MCP availability announcement on startup.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
