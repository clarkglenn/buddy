namespace Server.Options;

public sealed class CopilotOptions
{
    public const string SectionName = "Copilot";

    /// <summary>
    /// The AI model to use for chat completions.
    /// Examples: "gpt-5", "claude-sonnet-4.5", "copilot-model"
    /// </summary>
    public string Model { get; init; } = "gpt-5";

    /// <summary>
    /// Optional system message appended to every session.
    /// </summary>
    public string? SystemMessage { get; init; }

    /// <summary>
    /// Time-to-live for in-memory Copilot sessions (minutes).
    /// </summary>
    public int SessionTtlMinutes { get; init; } = 60;

    /// <summary>
    /// Default Copilot mode.
    /// Common values: "agent", "ask", "auto", or "code" (depending on SDK/model support).
    /// </summary>
    public string DefaultMode { get; init; } = "agent";

    public ToolAccessOptions ToolAccess { get; init; } = new();

    public McpDiscoveryOptions McpDiscovery { get; init; } = new();

    public ToolUsePolicyOptions ToolUsePolicy { get; init; } = new();
}

public sealed class ToolAccessOptions
{
    /// <summary>
    /// When true, do not apply any tool allow/deny filtering.
    /// </summary>
    public bool AllowAll { get; init; } = true;

    /// <summary>
    /// When true, automatically approve all tool permission checks during SDK execution.
    /// </summary>
    public bool AutoApproveToolPermissions { get; init; } = true;

    /// <summary>
    /// Allowlist of tool names to enable when AllowAll is false.
    /// </summary>
    public string[]? AvailableTools { get; init; }

    /// <summary>
    /// Denylist of tool names to disable when AllowAll is false.
    /// </summary>
    public string[]? ExcludedTools { get; init; }

    /// <summary>
    /// When true, add a system message advertising full tool access.
    /// </summary>
    public bool AdvertiseAllTools { get; init; } = true;

    /// <summary>
    /// System message used when advertising full tool access.
    /// </summary>
    public string AdvertiseMessage { get; init; } = "You have access to MCP tools configured for this session. Only use tools that are actually available/allowed, and ensure any required executables (for example: pwsh, node, npx) are installed and on PATH.";
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

public sealed class ToolUsePolicyOptions
{
    /// <summary>
    /// Enables policy checks that require tool usage for non-trivial prompts.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// When true, short/simple questions may be answered directly without tools.
    /// </summary>
    public bool AllowDirectResponsesForTrivialQuestions { get; init; } = true;

    /// <summary>
    /// Maximum prompt length (characters) considered for trivial-question classification.
    /// </summary>
    public int TrivialQuestionMaxChars { get; init; } = 220;

    /// <summary>
    /// When true, requests that violate policy fail immediately without retries.
    /// </summary>
    public bool FailImmediatelyOnViolation { get; init; } = true;

    /// <summary>
    /// User-facing message returned when a non-trivial response does not use tools.
    /// </summary>
    public string ViolationMessage { get; init; } = "Policy requires using MCP/CLI tools for non-trivial requests. Ask a concise factual question for direct Q&A, or rephrase the request to run through tools.";

    /// <summary>
    /// Advisory categories for prompt guidance.
    /// </summary>
    public string[] PreferredToolCategories { get; init; } = ["MCP", "CLI", "ReadOnlyHelpers"];
}
