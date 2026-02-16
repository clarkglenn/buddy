using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IntegrationTests;

public sealed class AuthLoginRedirectTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AuthLoginRedirectTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Admin_login_redirects_to_github_oauth()
    {
        using var client = _factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    var settings = new Dictionary<string, string?>
                    {
                        ["GitHub:ClientId"] = "test-client-id",
                        ["GitHub:RedirectUri"] = "http://localhost:5260/api/auth/callback",
                        ["GitHub:Scopes"] = "copilot"
                    };

                    config.AddInMemoryCollection(settings);
                });
            })
            .CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });

        var response = await client.GetAsync("/api/auth/admin/login");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.StartsWith("https://github.com/login/oauth/authorize", response.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
