namespace Server.Options;

public sealed class CopilotOptions
{
    public const string SectionName = "Copilot";

    /// <summary>
    /// Time-to-live for in-memory Copilot sessions (minutes).
    /// </summary>
    public int SessionTtlMinutes { get; init; } = 60;

    public CopilotCliOptions Cli { get; init; } = new();

    public McpDiscoveryOptions McpDiscovery { get; init; } = new();
}

public sealed class CopilotCliOptions
{
    /// <summary>
    /// Copilot CLI executable name or absolute path.
    /// </summary>
    public string Command { get; init; } = "copilot";

    /// <summary>
    /// Additional CLI arguments passed to the Copilot process.
    /// </summary>
    public string[] Arguments { get; init; } = [];

    /// <summary>
    /// Stream output mode expected from Copilot CLI.
    /// Supported values: "plain-text" and "json-stream".
    /// </summary>
    public string StreamMode { get; init; } = "plain-text";

    /// <summary>
    /// Maximum time to wait for a single CLI response.
    /// </summary>
    public int ResponseTimeoutSeconds { get; init; } = 300;

    /// <summary>
    /// Number of conversation turns retained per Slack thread/session.
    /// </summary>
    public int MaxConversationTurns { get; init; } = 12;

    /// <summary>
    /// Environment variable used to pass the resolved merged MCP config directory to the CLI process.
    /// </summary>
    public string McpConfigDirEnvironmentVariable { get; init; } = "COPILOT_CONFIG_DIR";

}

public sealed class McpDiscoveryOptions
{
    /// <summary>
    /// When true, resolve MCP servers from local config files.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// When true, include MCP servers defined in the workspace config.
    /// </summary>
    public bool IncludeWorkspaceConfig { get; init; } = true;

    /// <summary>
    /// When true, include MCP servers defined in the user-level config.
    /// </summary>
    public bool IncludeUserConfig { get; init; } = true;

    /// <summary>
    /// Optional override for the workspace MCP config path.
    /// </summary>
    public string? WorkspaceConfigPath { get; init; }

    /// <summary>
    /// Optional override for the user MCP config path.
    /// </summary>
    public string? UserConfigPath { get; init; }
}

