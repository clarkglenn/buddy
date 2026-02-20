using System.Text.Json;
using System.Reflection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Server.Options;
using Server.Services;
using Xunit;

namespace IntegrationTests;

public sealed class McpServerResolverTests
{
    [Fact]
    public void Resolve_parses_mcpServers_root_from_user_config()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "buddy-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var configPath = Path.Combine(tempDirectory, "mcp-config.json");
            File.WriteAllText(configPath, """
            {
              "mcpServers": {
                "obsidian": {
                  "command": "npx",
                  "args": ["--yes", "@mauricio.wolff/mcp-obsidian@latest"]
                }
              }
            }
            """);

            var resolver = CreateResolver(configPath);
            var resolution = resolver.Resolve();

            Assert.Single(resolution.Servers);
            Assert.Contains("obsidian", resolution.Servers.Keys, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void Resolve_parses_legacy_servers_root_for_compatibility()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "buddy-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var configPath = Path.Combine(tempDirectory, "mcp-config.json");
            File.WriteAllText(configPath, """
            {
              "servers": {
                "playwright": {
                  "command": "npx",
                  "args": ["--yes", "@playwright/mcp@latest"]
                }
              }
            }
            """);

            var resolver = CreateResolver(configPath);
            var resolution = resolver.Resolve();

            Assert.Single(resolution.Servers);
            Assert.Contains("playwright", resolution.Servers.Keys, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

        [Fact]
        public void Resolve_defaults_tools_to_wildcard_when_missing()
        {
                var tempDirectory = Path.Combine(Path.GetTempPath(), "buddy-tests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDirectory);

                try
                {
                        var configPath = Path.Combine(tempDirectory, "mcp-config.json");
                        File.WriteAllText(configPath, """
                        {
                            "mcpServers": {
                                "gmail": {
                                    "command": "npx",
                                    "args": ["--yes", "@modelcontextprotocol/server-gmail@latest"]
                                }
                            }
                        }
                        """);

                        var resolver = CreateResolver(configPath);
                        var resolution = resolver.Resolve();

                        Assert.True(resolution.Servers.TryGetValue("gmail", out var rawServer));
                        Assert.IsType<JsonElement>(rawServer);

                        var gmailServer = (JsonElement)rawServer;
                        Assert.True(gmailServer.TryGetProperty("tools", out var toolsElement));
                        Assert.Equal(JsonValueKind.Array, toolsElement.ValueKind);

                        var tools = toolsElement
                                .EnumerateArray()
                                .Where(static item => item.ValueKind == JsonValueKind.String)
                                .Select(static item => item.GetString())
                                .Where(static value => !string.IsNullOrWhiteSpace(value))
                                .Select(static value => value!.Trim())
                                .ToArray();

                        Assert.Single(tools);
                        Assert.Equal("*", tools[0]);
                }
                finally
                {
                        if (Directory.Exists(tempDirectory))
                        {
                                Directory.Delete(tempDirectory, recursive: true);
                        }
                }
        }

        [Fact]
        public void Resolve_inserts_non_interactive_npx_flag_when_missing()
        {
                var tempDirectory = Path.Combine(Path.GetTempPath(), "buddy-tests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDirectory);

                try
                {
                        var configPath = Path.Combine(tempDirectory, "mcp-config.json");
                        File.WriteAllText(configPath, """
                        {
                            "mcpServers": {
                                "gmail": {
                                    "command": "npx",
                                    "args": ["@modelcontextprotocol/server-gmail@latest"]
                                }
                            }
                        }
                        """);

                        var resolver = CreateResolver(configPath);
                        var resolution = resolver.Resolve();

                        Assert.True(resolution.Servers.TryGetValue("gmail", out var rawServer));
                        Assert.IsType<JsonElement>(rawServer);

                        var gmailServer = (JsonElement)rawServer;
                        Assert.True(gmailServer.TryGetProperty("args", out var argsElement));
                        Assert.Equal(JsonValueKind.Array, argsElement.ValueKind);

                        var args = argsElement
                                .EnumerateArray()
                                .Where(static item => item.ValueKind == JsonValueKind.String)
                                .Select(static item => item.GetString())
                                .Where(static value => !string.IsNullOrWhiteSpace(value))
                                .Select(static value => value!.Trim())
                                .ToArray();

                        Assert.NotEmpty(args);
                        Assert.True(
                                args.Contains("-y", StringComparer.OrdinalIgnoreCase)
                                || args.Contains("--yes", StringComparer.OrdinalIgnoreCase));
                }
                finally
                {
                        if (Directory.Exists(tempDirectory))
                        {
                                Directory.Delete(tempDirectory, recursive: true);
                        }
                }
        }

        [Fact]
        public void Resolve_merges_workspace_and_user_configs_when_both_enabled()
        {
                var tempDirectory = Path.Combine(Path.GetTempPath(), "buddy-tests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDirectory);

                try
                {
                        var workspaceDirectory = Path.Combine(tempDirectory, ".vscode");
                        Directory.CreateDirectory(workspaceDirectory);

                        var workspaceConfigPath = Path.Combine(workspaceDirectory, "mcp.json");
                        File.WriteAllText(workspaceConfigPath, """
                        {
                            "mcpServers": {
                                "playwright": {
                                    "command": "npx",
                                    "args": ["--yes", "@playwright/mcp@latest"]
                                }
                            }
                        }
                        """);

                        var userConfigPath = Path.Combine(tempDirectory, "mcp-config.json");
                        File.WriteAllText(userConfigPath, """
                        {
                            "mcpServers": {
                                "gmail": {
                                    "command": "npx",
                                    "args": ["--yes", "@modelcontextprotocol/server-gmail@latest"]
                                }
                            }
                        }
                        """);

                        var resolver = CreateResolver(
                                userConfigPath,
                                includeWorkspaceConfig: true,
                                includeUserConfig: true,
                                contentRootPath: tempDirectory);

                        var resolution = resolver.Resolve();

                        Assert.Equal(2, resolution.Servers.Count);
                        Assert.Contains("playwright", resolution.Servers.Keys, StringComparer.OrdinalIgnoreCase);
                        Assert.Contains("gmail", resolution.Servers.Keys, StringComparer.OrdinalIgnoreCase);
                }
                finally
                {
                        if (Directory.Exists(tempDirectory))
                        {
                                Directory.Delete(tempDirectory, recursive: true);
                        }
                }
        }

        [Fact]
        public void Resolve_extracts_tool_names_from_server_configs()
        {
                var tempDirectory = Path.Combine(Path.GetTempPath(), "buddy-tests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDirectory);

                try
                {
                        var configPath = Path.Combine(tempDirectory, "mcp-config.json");
                        File.WriteAllText(configPath, """
                        {
                            "mcpServers": {
                                "weather": {
                                    "command": "npx",
                                    "args": ["-y", "@mauricio.wolff/mcp-weather@latest"],
                                    "tools": ["getCurrentWeather", "getWeatherForecast"]
                                },
                                "playwright": {
                                    "command": "npx",
                                    "args": ["-y", "@playwright/mcp@latest"]
                                }
                            }
                        }
                        """);

                        var resolver = CreateResolver(configPath);
                        var resolution = resolver.Resolve();

                        Assert.Contains("mcp:weather.getCurrentWeather", resolution.ToolNames);
                        Assert.Contains("mcp:weather.getWeatherForecast", resolution.ToolNames);
                        Assert.Contains("mcp:playwright.*", resolution.ToolNames);
                }
                finally
                {
                        if (Directory.Exists(tempDirectory))
                        {
                                Directory.Delete(tempDirectory, recursive: true);
                        }
                }
        }

    [Fact]
    public void Resolve_uses_global_copilot_mcp_config_file_when_present()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var globalPath = @"C:\Users\glenn\.copilot\mcp-config.json";
        if (!File.Exists(globalPath))
        {
            return;
        }

        var expectedCount = GetServerCount(globalPath);
        var resolver = CreateResolver(userConfigPath: null);
        var resolution = resolver.Resolve();

        Assert.Equal(expectedCount, resolution.Servers.Count);
    }

    [Fact]
    public void NormalizeCommandForHost_falls_back_to_powershell_when_pwsh_missing()
    {
        var method = typeof(McpServerResolver).GetMethod("NormalizeCommandForHost", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var availability = new Func<string, bool>(name =>
            string.Equals(name, "powershell", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "powershell.exe", StringComparison.OrdinalIgnoreCase));

        var args = new object?[] { "pwsh", availability, null };
        var normalized = method!.Invoke(null, args);

        Assert.Equal("powershell", normalized as string);
        Assert.NotNull(args[2]);
        Assert.Contains("falling back", args[2]!.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeCommandForHost_keeps_pwsh_when_available()
    {
        var method = typeof(McpServerResolver).GetMethod("NormalizeCommandForHost", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var availability = new Func<string, bool>(name => string.Equals(name, "pwsh", StringComparison.OrdinalIgnoreCase));

        var args = new object?[] { "pwsh", availability, null };
        var normalized = method!.Invoke(null, args);

        Assert.Equal("pwsh", normalized as string);
        Assert.Null(args[2]);
    }

    private static McpServerResolver CreateResolver(
        string? userConfigPath,
        bool includeWorkspaceConfig = false,
        bool includeUserConfig = true,
        string? contentRootPath = null)
    {
        var options = Options.Create(new CopilotOptions
        {
            McpDiscovery = new McpDiscoveryOptions
            {
                Enabled = true,
                IncludeWorkspaceConfig = includeWorkspaceConfig,
                IncludeUserConfig = includeUserConfig,
                UserConfigPath = userConfigPath,
            }
        });

        var environment = new TestHostEnvironment
        {
            ContentRootPath = string.IsNullOrWhiteSpace(contentRootPath)
                ? Directory.GetCurrentDirectory()
                : contentRootPath
        };

        return new McpServerResolver(options, environment, NullLogger<McpServerResolver>.Instance);
    }

    private static int GetServerCount(string configPath)
    {
        using var stream = File.OpenRead(configPath);
        using var document = JsonDocument.Parse(stream);

        if (document.RootElement.TryGetProperty("mcpServers", out var mcpServers) &&
            mcpServers.ValueKind == JsonValueKind.Object)
        {
            return mcpServers.EnumerateObject().Count();
        }

        if (document.RootElement.TryGetProperty("servers", out var servers) &&
            servers.ValueKind == JsonValueKind.Object)
        {
            return servers.EnumerateObject().Count();
        }

        return 0;
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";

        public string ApplicationName { get; set; } = "IntegrationTests";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}