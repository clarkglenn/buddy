namespace Server.Options;

public sealed class GitHubOptions
{
    public const string SectionName = "GitHub";

    public string ClientId { get; init; } = string.Empty;
    public string ClientSecret { get; init; } = string.Empty;
    public string RedirectUri { get; init; } = string.Empty;
    public string Scopes { get; init; } = "copilot";
}
