using Server.Options;
using Server.Services;
using Buddy.Server.Services.Messaging;
using Buddy.Server.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecrets<Program>(optional: true);
}

builder.Services.AddOptions<CopilotOptions>()
    .BindConfiguration(CopilotOptions.SectionName)
    .Validate(options => !string.IsNullOrWhiteSpace(options.Cli.Command), "Copilot:Cli:Command is required.")
    .Validate(
        options => string.IsNullOrWhiteSpace(options.Cli.StreamMode)
            || options.Cli.StreamMode.Equals("plain-text", StringComparison.OrdinalIgnoreCase)
            || options.Cli.StreamMode.Equals("json-stream", StringComparison.OrdinalIgnoreCase)
            || options.Cli.StreamMode.Equals("json", StringComparison.OrdinalIgnoreCase)
            || options.Cli.StreamMode.Equals("ndjson", StringComparison.OrdinalIgnoreCase),
        "Copilot:Cli:StreamMode must be one of: plain-text, json-stream, json, ndjson.")
    .ValidateOnStart();

builder.Services.AddOptions<MessagingOptions>()
    .BindConfiguration(MessagingOptions.SectionName);

// Check if Slack Socket Mode should be enabled
var messagingConfig = builder.Configuration.GetSection(MessagingOptions.SectionName).Get<MessagingOptions>();
if (messagingConfig?.Slack?.UseSocketMode == true)
{
    builder.Services.AddHostedService<SlackSocketModeService>();
}

builder.Services.AddHostedService<McpAvailabilityAnnouncementService>();
builder.Services.AddHostedService<CopilotWarmupService>();

builder.Services.AddSingleton<ICopilotSessionStore, CopilotSessionStore>();
builder.Services.AddSingleton<IMcpServerResolver, McpServerResolver>();
builder.Services.AddScoped<IMessageHandlerService, MessageHandlerService>();

// Register messaging providers
builder.Services.AddHttpClient<SlackProvider>();

// Register messaging provider factory
builder.Services.AddSingleton<IMessagingProviderFactory>(sp =>
{
    var providers = new List<IMessagingProvider>
    {
        sp.GetRequiredService<SlackProvider>()
    };
    return new MessagingProviderFactory(providers);
});

builder.Services.AddScoped<CopilotClient>();

var app = builder.Build();

var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("StartupPreflight");
StartupPreflight.LogCommandAvailability(startupLogger, "copilot", "pwsh", "powershell", "node", "npx");
var copilotOptions = app.Services.GetRequiredService<IOptions<CopilotOptions>>().Value;
StartupPreflight.LogRuntimeContext(startupLogger, copilotOptions.McpDiscovery.UserConfigPath);

await app.RunAsync();

public partial class Program { }
