# Buddy Copilot

A minimal .NET 10 local server that uses Copilot CLI and streams responses over HTTP and SignalR.

## Requirements

- .NET SDK 10.0.103 or newer
- PowerShell 7 (`pwsh`) recommended for MCP/CLI tooling; if unavailable, the server falls back to Windows PowerShell (`powershell`) when possible

## Configuration

This app uses machine-level Copilot CLI authentication (the signed-in runtime user profile).

For local development, prefer `dotnet user-secrets` so secrets do not get committed.

Required settings:

- `Copilot:Cli:Command` (default: `copilot`)

Optional settings:

- `Copilot:Cli:ResponseTimeoutSeconds`
- `Copilot:McpDiscovery:*`

Example (local dev):

- `dotnet user-secrets set "Copilot__Cli__Command" "copilot" --project src/Server/Server.csproj`

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

1. Ensure Copilot CLI is installed and authenticated for the runtime Windows user.
2. Start the server.
3. Send prompts via your configured messaging platform.

## Gmail MCP in headless/server mode

- Buddy always runs Copilot CLI with `--allow-all-tools` and `COPILOT_ALLOW_ALL=1`.
- For non-interactive Slack/server flows, Gmail MCP must already be authorized for the **same Windows user profile** that runs the server process.
- If you see permission errors like “could not request permission from user”, run a one-time interactive authorization using that same runtime user, then restart Buddy.
- Startup logs now include runtime identity and configured user MCP config path so you can confirm profile/config alignment quickly.
