# Buddy Copilot

A minimal .NET 10 local server that authenticates to GitHub Copilot and streams responses over HTTP and SignalR.

## Requirements

- .NET SDK 10.0.103 or newer
- PowerShell 7 (`pwsh`) recommended for MCP/CLI tooling; if unavailable, the server falls back to Windows PowerShell (`powershell`) when possible

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

## PowerShell setup (Windows)

Install PowerShell 7:

- `winget install --id Microsoft.PowerShell --source winget`

Verify shells:

- `pwsh -Version`
- `powershell -Command "$PSVersionTable.PSVersion"`

The server logs startup availability for `pwsh`, `powershell`, `node`, and `npx`. If an MCP config uses `pwsh` but it is missing, command resolution automatically falls back to `powershell` when available.

## Always-running server (Windows Service)

Publish the server:

- `dotnet publish src/Server/Server.csproj -c Release -o .\\artifacts\\server`

Create/update service (run elevated PowerShell):

- `sc.exe create BuddyCopilotServer binPath= "C:\\vcs\\buddy\\artifacts\\server\\Server.exe" start= auto`
- `sc.exe description BuddyCopilotServer "Buddy Copilot Slack/MCP bridge"`
- `sc.exe failure BuddyCopilotServer reset= 86400 actions= restart/5000/restart/15000/restart/60000`

Control service:

- `sc.exe start BuddyCopilotServer`
- `sc.exe stop BuddyCopilotServer`
- `sc.exe query BuddyCopilotServer`

If the service already exists and you republish, update path and restart:

- `sc.exe config BuddyCopilotServer binPath= "C:\\vcs\\buddy\\artifacts\\server\\Server.exe"`
- `sc.exe stop BuddyCopilotServer`
- `sc.exe start BuddyCopilotServer`

## Use

1. Start the OAuth flow at `http://localhost:5260/api/auth/admin/login` in a browser.
2. After GitHub redirects back, the callback returns JSON with the auth result.
3. Check connection status at `http://localhost:5260/api/auth/status`.
