using System.Text.Json;
using Microsoft.Extensions.Options;
using Server.Options;

namespace Server.Services;

public interface IMcpServerResolver
{
    McpServerResolution Resolve();
}

public sealed record McpServerResolution(Dictionary<string, object> Servers, string? ConfigDir);

public sealed class McpServerResolver : IMcpServerResolver
{
    private const string McpServersPropertyName = "mcpServers";
    private const string LegacyMcpServersPropertyName = "servers";
    private const string DefaultUserConfigFileName = "mcp-config.json";

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

        if (_options.McpDiscovery.Enabled)
        {
            MergeFromFile(GetUserConfigPath(), servers);
        }

        var configDir = servers.Count > 0 ? WriteMergedConfig(servers) : null;
        return new McpServerResolution(servers, configDir);
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

    private void MergeFromFile(string path, Dictionary<string, object> servers)
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
                servers[server.Name] = server.Value.Clone();
            }
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
