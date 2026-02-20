using System.Text.Json;
using Microsoft.Extensions.Options;
using Server.Options;

namespace Server.Services;

public interface IMcpServerResolver
{
    McpServerResolution Resolve();
}

public sealed record McpServerResolution(
    Dictionary<string, object> Servers,
    string? ConfigDir,
    IReadOnlyList<string> ToolNames);

public sealed class McpServerResolver : IMcpServerResolver
{
    private const string McpServersPropertyName = "mcpServers";
    private const string LegacyMcpServersPropertyName = "servers";
    private const string DefaultUserConfigFileName = "mcp-config.json";
    private const string DefaultWorkspaceConfigRelativePath = ".vscode\\mcp.json";

    private readonly CopilotOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<McpServerResolver> _logger;

    public McpServerResolver(
        IOptions<CopilotOptions> options,
        IHostEnvironment environment,
        ILogger<McpServerResolver> logger)
    {
        _options = options.Value;
        _environment = environment;
        _logger = logger;
    }

    public McpServerResolution Resolve()
    {
        var servers = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var serverTools = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);

        if (_options.McpDiscovery.Enabled)
        {
            if (_options.McpDiscovery.IncludeWorkspaceConfig)
            {
                MergeFromFile(GetWorkspaceConfigPath(), servers, serverTools);
            }

            if (_options.McpDiscovery.IncludeUserConfig)
            {
                MergeFromFile(GetUserConfigPath(), servers, serverTools);
            }
        }

        var configDir = servers.Count > 0 ? WriteMergedConfig(servers) : null;
        var toolNames = serverTools
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .SelectMany(pair => pair.Value
                .OrderBy(tool => tool, StringComparer.OrdinalIgnoreCase)
                .Select(tool => $"mcp:{pair.Key}.{tool}"))
            .ToArray();

        return new McpServerResolution(servers, configDir, toolNames);
    }

    private string GetWorkspaceConfigPath()
    {
        if (!string.IsNullOrWhiteSpace(_options.McpDiscovery.WorkspaceConfigPath))
        {
            return _options.McpDiscovery.WorkspaceConfigPath;
        }

        return Path.Combine(_environment.ContentRootPath, DefaultWorkspaceConfigRelativePath);
    }

    private string GetUserConfigPath()
    {
        if (!string.IsNullOrWhiteSpace(_options.McpDiscovery.UserConfigPath))
        {
            return _options.McpDiscovery.UserConfigPath;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            userProfile = _environment.ContentRootPath;
        }

        return Path.Combine(userProfile, ".copilot", DefaultUserConfigFileName);
    }

    private void MergeFromFile(
        string path,
        Dictionary<string, object> servers,
        Dictionary<string, IReadOnlyCollection<string>> serverTools)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);

            if (!TryGetMcpServersElement(document.RootElement, out var mcpServersElement) ||
                mcpServersElement.ValueKind != JsonValueKind.Object)
            {
                _logger.LogWarning("MCP config {Path} does not contain a valid mcpServers/servers object.", path);
                return;
            }

            foreach (var server in mcpServersElement.EnumerateObject())
            {
                if (!TryNormalizeServer(server.Name, server.Value, out var normalizedServer, out var warning))
                {
                    if (!string.IsNullOrWhiteSpace(warning))
                    {
                        _logger.LogWarning("Skipping MCP server {ServerName} from {Path}: {Warning}", server.Name, path, warning);
                    }

                    continue;
                }

                if (!string.IsNullOrWhiteSpace(warning))
                {
                    _logger.LogInformation("MCP server {ServerName} from {Path}: {Warning}", server.Name, path, warning);
                }

                servers[server.Name] = normalizedServer!;
                serverTools[server.Name] = ExtractServerToolNames(server.Value);
            }

            _logger.LogInformation("Resolved {Count} MCP server(s) from {Path}.", servers.Count, path);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse MCP config {Path}.", path);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to read MCP config {Path}.", path);
        }
    }

    private static bool TryNormalizeServer(
        string serverName,
        JsonElement serverElement,
        out object? normalizedServer,
        out string? warning)
    {
        normalizedServer = null;
        warning = null;

        if (serverElement.ValueKind != JsonValueKind.Object)
        {
            warning = "Server definition must be an object.";
            return false;
        }

        var command = serverElement.TryGetProperty("command", out var commandElement)
            ? commandElement.GetString()?.Trim()
            : null;
        if (string.IsNullOrWhiteSpace(command))
        {
            warning = "Missing required 'command'.";
            return false;
        }

        command = NormalizeCommandForHost(command, IsCommandAvailable, out var commandWarning);
        if (!string.IsNullOrWhiteSpace(commandWarning))
        {
            warning = AppendWarning(warning, commandWarning);
        }

        if (!serverElement.TryGetProperty("args", out var argsElement) || argsElement.ValueKind != JsonValueKind.Array)
        {
            warning = "Missing required 'args' array.";
            return false;
        }

        var args = argsElement
            .EnumerateArray()
            .Where(arg => arg.ValueKind == JsonValueKind.String)
            .Select(arg => arg.GetString()?.Trim())
            .Where(arg => !string.IsNullOrWhiteSpace(arg))
            .ToArray();

        if (args.Length == 0)
        {
            warning = "'args' must contain at least one non-empty string.";
            return false;
        }

        if (string.Equals(command, "npx", StringComparison.OrdinalIgnoreCase) &&
            !args.Contains("-y", StringComparer.OrdinalIgnoreCase) &&
            !args.Contains("--yes", StringComparer.OrdinalIgnoreCase))
        {
            args = ["-y", .. args];
            warning = AppendWarning(warning, "Missing non-interactive npx flag; inserted '-y'.");
        }

        var type = serverElement.TryGetProperty("type", out var typeElement)
            ? typeElement.GetString()?.Trim()
            : null;

        if (string.IsNullOrWhiteSpace(type))
        {
            type = "stdio";
            warning = "Missing 'type'; defaulted to 'stdio'.";
        }

        var normalized = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["type"] = type,
            ["command"] = command,
            ["args"] = args
        };

        var normalizedTools = ExtractNormalizedTools(serverElement, out var toolsWarning);
        if (!string.IsNullOrWhiteSpace(toolsWarning))
        {
            warning = AppendWarning(warning, toolsWarning);
        }

        normalized["tools"] = normalizedTools;

        normalizedServer = JsonSerializer.SerializeToElement(normalized);
        return true;
    }

    private static string[] ExtractNormalizedTools(JsonElement serverElement, out string? warning)
    {
        warning = null;

        if (!serverElement.TryGetProperty("tools", out var toolsElement))
        {
            warning = "Missing 'tools'; defaulted to ['*'].";
            return ["*"];
        }

        if (toolsElement.ValueKind != JsonValueKind.Array)
        {
            warning = "Invalid 'tools' value; defaulted to ['*'].";
            return ["*"];
        }

        var names = toolsElement
            .EnumerateArray()
            .Where(static tool => tool.ValueKind == JsonValueKind.String)
            .Select(static tool => tool.GetString()?.Trim())
            .Where(static tool => !string.IsNullOrWhiteSpace(tool))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (names.Length == 0)
        {
            warning = "Empty 'tools' array; defaulted to ['*'].";
            return ["*"];
        }

        return names!;
    }

    private static string AppendWarning(string? existing, string addition)
    {
        if (string.IsNullOrWhiteSpace(existing))
        {
            return addition;
        }

        return $"{existing} {addition}";
    }

    private static string NormalizeCommandForHost(string command, Func<string, bool> isCommandAvailable, out string? warning)
    {
        warning = null;

        if (string.Equals(command, "pwsh", StringComparison.OrdinalIgnoreCase)
            || string.Equals(command, "pwsh.exe", StringComparison.OrdinalIgnoreCase))
        {
            if (!isCommandAvailable("pwsh"))
            {
                if (isCommandAvailable("powershell") || isCommandAvailable("powershell.exe"))
                {
                    warning = "Command 'pwsh' not found on PATH; falling back to 'powershell'.";
                    return "powershell";
                }

                warning = "Command 'pwsh' not found on PATH and no 'powershell' fallback was found.";
            }
        }

        return command;
    }

    private static bool IsCommandAvailable(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        if (Path.IsPathRooted(command))
        {
            return File.Exists(command);
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVariable))
        {
            return false;
        }

        var pathExtVariable = Environment.GetEnvironmentVariable("PATHEXT");
        var extensions = (pathExtVariable ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var hasExtension = Path.HasExtension(command);
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            if (hasExtension)
            {
                var candidateWithExtension = Path.Combine(directory, command);
                if (File.Exists(candidateWithExtension))
                {
                    return true;
                }

                continue;
            }

            var extensionCandidates = new[] { string.Empty }.Concat(extensions);
            foreach (var extension in extensionCandidates)
            {
                var candidate = Path.Combine(directory, command + extension);
                if (File.Exists(candidate))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IReadOnlyCollection<string> ExtractServerToolNames(JsonElement serverElement)
    {
        if (serverElement.ValueKind != JsonValueKind.Object)
        {
            return ["*"];
        }

        if (!serverElement.TryGetProperty("tools", out var toolsElement) ||
            toolsElement.ValueKind != JsonValueKind.Array)
        {
            return ["*"];
        }

        var names = new List<string>();
        foreach (var toolElement in toolsElement.EnumerateArray())
        {
            if (toolElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var name = toolElement.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            names.Add(name);
        }

        if (names.Count == 0)
        {
            return ["*"];
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool TryGetMcpServersElement(JsonElement rootElement, out JsonElement mcpServersElement)
    {
        if (rootElement.TryGetProperty(McpServersPropertyName, out mcpServersElement))
        {
            return true;
        }

        if (rootElement.TryGetProperty(LegacyMcpServersPropertyName, out mcpServersElement))
        {
            return true;
        }

        return false;
    }

    private string WriteMergedConfig(Dictionary<string, object> servers)
    {
        var configDir = Path.Combine(Path.GetTempPath(), "buddy", "copilot-mcp");
        Directory.CreateDirectory(configDir);

        var configPath = Path.Combine(configDir, DefaultUserConfigFileName);
        var payload = new Dictionary<string, object>
        {
            [McpServersPropertyName] = servers
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(configPath, json);
        return configDir;
    }
}
