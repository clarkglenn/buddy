# Buddy Copilot

A minimal .NET 10 local server that authenticates to GitHub Copilot and streams responses over HTTP and SignalR.

## Requirements

- .NET SDK 10.0.103 or newer

## Configuration

This app uses GitHub OAuth (redirect flow) to obtain a token, and uses a configured Copilot model for chat completions.

For local development, prefer `dotnet user-secrets` so secrets do not get committed.

Required settings:

- `GitHub:ClientId`
- `GitHub:ClientSecret`
- `GitHub:RedirectUri`
  - For local dev: `http://localhost:5260/api/auth/callback` (must match your GitHub OAuth App callback URL)
- `Copilot:Model`

Optional settings:

- `Copilot:DefaultMode` (default: `agent`)

Example (local dev):

- `dotnet user-secrets set "GitHub__ClientId" "<your-client-id>" --project src/Server/Server.csproj`
- `dotnet user-secrets set "GitHub__ClientSecret" "<your-client-secret>" --project src/Server/Server.csproj`
- `dotnet user-secrets set "GitHub__RedirectUri" "http://localhost:5260/api/auth/callback" --project src/Server/Server.csproj`
- `dotnet user-secrets set "Copilot__Model" "gpt-5.2" --project src/Server/Server.csproj`

## Run

From the workspace root:

- dotnet run --project src/Server/Server.csproj

## Use

1. Start the OAuth flow at `http://localhost:5260/api/auth/admin/login` in a browser.
2. After GitHub redirects back, the callback returns JSON with the auth result.
3. Check connection status at `http://localhost:5260/api/auth/status`.
