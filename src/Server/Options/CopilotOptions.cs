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
    /// Maximum time to wait for ACP initialization.
    /// </summary>
    public int AcpStartupTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// When true, use --resume to maintain CLI session context across turns (one-shot process per request).
    /// </summary>
    public bool ReuseProcessPerSession { get; init; } = true;

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
    /// AI model to use (e.g. "gpt-5.2", "claude-sonnet-4.6").
    /// When null or empty, the CLI default model is used.
    /// </summary>
    public string? Model { get; init; }
}

