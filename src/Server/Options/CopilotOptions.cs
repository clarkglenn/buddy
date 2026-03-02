namespace Server.Options;

public sealed class CopilotOptions
{
    public const string SectionName = "Copilot";

    /// <summary>
    /// Time-to-live for in-memory Copilot sessions (minutes).
    /// </summary>
    public int SessionTtlMinutes { get; init; } = 60;

    public CopilotCliOptions Cli { get; init; } = new();
}

public sealed class CopilotCliOptions
{
    /// <summary>
    /// When true, keep one Copilot CLI process alive per conversation key and reuse it for subsequent turns.
    /// </summary>
    public bool ReuseProcessPerSession { get; init; } = true;

    /// <summary>
    /// When true, run warmup Copilot CLI requests during server startup.
    /// </summary>
    public bool WarmupOnStartup { get; init; } = true;

    /// <summary>
    /// Number of warmup sessions to initialize during startup.
    /// </summary>
    public int WarmupSessionCount { get; init; } = 1;

    /// <summary>
    /// Timeout for each warmup request.
    /// </summary>
    public int WarmupTimeoutSeconds { get; init; } = 30;

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
}

