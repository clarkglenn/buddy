using Server.Hubs;
using Server.Options;
using Server.Services;
using Buddy.Server.Services.Messaging;
using Buddy.Server.Options;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecrets<Program>(optional: true);
}

builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddCors(options =>
{
    var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(origins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

builder.Services.AddOptions<GitHubOptions>()
    .BindConfiguration(GitHubOptions.SectionName)
    .Validate(o => !string.IsNullOrWhiteSpace(o.ClientId), "GitHub:ClientId is required.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.ClientSecret), "GitHub:ClientSecret is required.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.RedirectUri), "GitHub:RedirectUri is required.")
    .ValidateOnStart();

builder.Services.AddOptions<CopilotOptions>()
    .BindConfiguration(CopilotOptions.SectionName)
    .Validate(options => !string.IsNullOrWhiteSpace(options.Model), "Copilot:Model is required.")
    .ValidateOnStart();

builder.Services.AddOptions<MessagingOptions>()
    .BindConfiguration(MessagingOptions.SectionName);

// Check if Slack Socket Mode should be enabled
var messagingConfig = builder.Configuration.GetSection(MessagingOptions.SectionName).Get<MessagingOptions>();
if (messagingConfig?.Slack?.UseSocketMode == true)
{
    builder.Services.AddHostedService<SlackSocketModeService>();
}

builder.Services.AddSingleton<IGitHubTokenStore, FileBasedGitHubTokenStore>();
builder.Services.AddSingleton<IMultiChannelTokenStore, FileBasedMultiChannelTokenStore>();
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

builder.Services.AddHttpClient<GitHubAuthService>();
builder.Services.AddScoped<CopilotClient>();

var app = builder.Build();

app.UseCors();

app.MapControllers();
app.MapHub<CopilotHub>("/hubs/copilot");

app.Run();

public partial class Program { }
