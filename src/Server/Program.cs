using Server.Options;
using Server.Services;
using Buddy.Server.Services.Messaging;
using Buddy.Server.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog(config => config
    .ReadFrom.Configuration(builder.Configuration));

if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecrets<Program>(optional: true);
}

builder.Services.AddOptions<CopilotOptions>()
    .BindConfiguration(CopilotOptions.SectionName)
    .Validate(options => !string.IsNullOrWhiteSpace(options.Cli.Command), "Copilot:Cli:Command is required.")
    .ValidateOnStart();

builder.Services.AddOptions<MessagingOptions>()
    .BindConfiguration(MessagingOptions.SectionName);

// Check if Slack Socket Mode should be enabled
var messagingConfig = builder.Configuration.GetSection(MessagingOptions.SectionName).Get<MessagingOptions>();
if (messagingConfig?.Slack?.UseSocketMode == true)
{
    builder.Services.AddHostedService<SlackSocketModeService>();
}

builder.Services.AddSingleton<ICopilotAcpHost, CopilotAcpHost>();
builder.Services.AddHostedService(sp => (CopilotAcpHost)sp.GetRequiredService<ICopilotAcpHost>());
builder.Services.AddSingleton<ICopilotSessionStore, CopilotSessionStore>();
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
StartupPreflight.LogRuntimeContext(startupLogger);

await app.RunAsync();

public partial class Program { }
