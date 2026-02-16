using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Server.Options;
using Server.Services;
using Buddy.Server.Services.Messaging;

namespace Server.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly GitHubAuthService _authService;
    private readonly IGitHubTokenStore _tokenStore;
    private readonly IMultiChannelTokenStore? _multiChannelTokenStore;
    private readonly IMessagingProviderFactory? _providerFactory;
    private readonly GitHubOptions _gitHubOptions;

    public AuthController(
        GitHubAuthService authService,
        IGitHubTokenStore tokenStore,
        IOptions<GitHubOptions> gitHubOptions,
        IMultiChannelTokenStore? multiChannelTokenStore = null,
        IMessagingProviderFactory? providerFactory = null)
    {
        _authService = authService;
        _tokenStore = tokenStore;
        _gitHubOptions = gitHubOptions.Value;
        _multiChannelTokenStore = multiChannelTokenStore;
        _providerFactory = providerFactory;
    }

    [HttpGet("admin/login")]
    public IActionResult AdminLogin()
    {
        if (string.IsNullOrWhiteSpace(_gitHubOptions.ClientId)
            || string.IsNullOrWhiteSpace(_gitHubOptions.RedirectUri))
        {
            return Problem("GitHub OAuth is not configured.", statusCode: StatusCodes.Status500InternalServerError);
        }

        var state = Guid.NewGuid().ToString("N");
        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddMinutes(10)
        };
        Response.Cookies.Append("oauth_state", state, cookieOptions);

        var authorizeUrl =
            "https://github.com/login/oauth/authorize" +
            "?client_id=" + Uri.EscapeDataString(_gitHubOptions.ClientId) +
            "&redirect_uri=" + Uri.EscapeDataString(_gitHubOptions.RedirectUri) +
            "&scope=" + Uri.EscapeDataString(_gitHubOptions.Scopes) +
            "&state=" + Uri.EscapeDataString(state);

        return Redirect(authorizeUrl);
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken cancellationToken)
    {
        var token = await _tokenStore.GetTokenAsync(cancellationToken);
        return Ok(new { connected = !string.IsNullOrWhiteSpace(token) });
    }

    [HttpGet("callback")]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        [FromQuery] string? error_description,
        [FromQuery] string? platform,
        [FromQuery] string? platformUserId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            var errorMessage = string.IsNullOrWhiteSpace(error_description) ? error : error_description;
            return BadRequest(new { status = "error", message = errorMessage });
        }

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        {
            return BadRequest(new { status = "error", message = "Missing authorization data." });
        }

        if (!Request.Cookies.TryGetValue("oauth_state", out var expectedState) || expectedState != state)
        {
            return BadRequest(new { status = "error", message = "Invalid OAuth state." });
        }

        var token = await _authService.ExchangeCodeForTokenAsync(code, _gitHubOptions.RedirectUri, cancellationToken);
        if (!string.IsNullOrWhiteSpace(token.AccessToken))
        {
            await _tokenStore.SetTokenAsync(token.AccessToken, cancellationToken);

            // If platform and platformUserId are provided, also store in multi-channel store
            if (!string.IsNullOrEmpty(platform) && !string.IsNullOrEmpty(platformUserId) && _multiChannelTokenStore != null)
            {
                if (Enum.TryParse<MessagingPlatform>(platform, true, out var platformEnum))
                {
                    var platformUser = new PlatformUser
                    {
                        Platform = platformEnum,
                        PlatformUserId = platformUserId
                    };

                    await _multiChannelTokenStore.SetTokenAsync(platformUser, token.AccessToken, cancellationToken);

                    // Notify the user on their platform
                    try
                    {
                        if (_providerFactory != null)
                        {
                            var provider = _providerFactory.GetProvider(platformEnum);
                            var msg = new SendMessageParams
                            {
                                User = platformUser,
                                Message = "✅ Successfully linked! You can now send prompts."
                            };
                            await provider.SendMessageAsync(msg, cancellationToken);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log but don't fail the auth callback
                        Console.Error.WriteLine($"Failed to notify user of linking: {ex.Message}");
                    }
                }
            }

            return Ok(new { status = "success" });
        }

        var message = token.ErrorDescription ?? token.Error ?? "Authorization failed.";
        return BadRequest(new { status = "error", message });
    }

    [HttpPost("device/clear")]
    public async Task<IActionResult> ClearToken(CancellationToken cancellationToken)
    {
        await _tokenStore.ClearAsync(cancellationToken);
        return NoContent();
    }
}
