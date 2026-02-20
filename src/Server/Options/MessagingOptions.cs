namespace Buddy.Server.Options;

/// <summary>
/// Configuration for all messaging providers
/// </summary>
public class MessagingOptions
{
    public const string SectionName = "Messaging";

    public SlackOptions? Slack { get; set; }
}

/// <summary>
/// Slack configuration
/// </summary>
public class SlackOptions
{
    public string? BotToken { get; set; }
    public string? SigningSecret { get; set; }
    public string? AppLevelToken { get; set; }
    public bool UseSocketMode { get; set; }
    public string StartupAnnouncementChannel { get; set; } = "all-buddy";
}
