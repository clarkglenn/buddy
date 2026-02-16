namespace Shared.Models;

public sealed record TokenResponse(
    string? AccessToken,
    string? TokenType,
    string? Scope,
    string? Error,
    string? ErrorDescription,
    string? ErrorUri);
