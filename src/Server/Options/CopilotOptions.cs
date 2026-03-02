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
    /// When true, use --resume to maintain CLI session context across turns (one-shot process per request).
    /// </summary>
    public bool ReuseProcessPerSession { get; init; } = true;

    /// <summary>
    /// Experimental. When true, keep one Copilot CLI process alive per conversation key
    /// and send prompts via stdin instead of spawning a new process each time.
    /// Requires the Copilot CLI to support interactive stdin mode.
    /// Falls back to one-shot --resume mode on failure.
    /// </summary>
    public bool PersistentProcessMode { get; init; } = false;

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

