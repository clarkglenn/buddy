using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Server.Options;
using Shared.Models;

namespace Server.Services;

public sealed class GitHubAuthService
{
    private readonly HttpClient _httpClient;
    private readonly GitHubOptions _options;

    public GitHubAuthService(HttpClient httpClient, IOptions<GitHubOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public async Task<TokenResponse> ExchangeCodeForTokenAsync(
        string code,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["code"] = code,
            ["redirect_uri"] = redirectUri
        });

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<TokenPayload>(cancellationToken: cancellationToken);
        if (payload == null)
        {
            throw new InvalidOperationException("GitHub token response was empty.");
        }

        return new TokenResponse(
            payload.AccessToken,
            payload.TokenType,
            payload.Scope,
            payload.Error,
            payload.ErrorDescription,
            payload.ErrorUri);
    }

    private sealed record TokenPayload(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("token_type")] string? TokenType,
        [property: JsonPropertyName("scope")] string? Scope,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_description")] string? ErrorDescription,
        [property: JsonPropertyName("error_uri")] string? ErrorUri);
}
