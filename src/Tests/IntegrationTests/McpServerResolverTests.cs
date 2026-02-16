using System.Text.Json;
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

    private static McpServerResolver CreateResolver(string? userConfigPath)
    {
        var options = Options.Create(new CopilotOptions
        {
            McpDiscovery = new McpDiscoveryOptions
            {
                Enabled = true,
                UserConfigPath = userConfigPath,
            }
        });

        var environment = new TestHostEnvironment
        {
            ContentRootPath = Directory.GetCurrentDirectory()
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