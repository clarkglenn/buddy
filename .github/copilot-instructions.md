- [x] Verify that the copilot-instructions.md file in the .github directory is created.

- [x] Clarify Project Requirements

- [x] Scaffold the Project

- [x] Customize the Project

- [x] Install Required Extensions

- [x] Compile the Project

- [x] Create and Run Task

- [x] Launch the Project

- [x] Ensure Documentation is Complete

## Playwright (MCP) for Copilot

This repo includes a template for enabling the **Playwright MCP server** with Copilot.

### VS Code (recommended: workspace config)

1. Create `.vscode/mcp.json` in the workspace root.
2. Paste the contents of `.github/playwright-vscode-mcp.template.json` into it.
3. Restart VS Code and trust/enable the server when prompted.

### Copilot CLI config

Alternatively, add this to `~/.copilot/mcp-config.json`:

```json
{
  "mcpServers": {
    "playwright": {
      "type": "local",
      "command": "npx",
      "tools": ["*"],
      "args": ["@playwright/mcp@latest"]
    }
  }
}
```

> Note: Playwright MCP runs via `npx @playwright/mcp@latest` (no repo-local Node project required).

- Work through each checklist item systematically.
- Keep communication concise and focused.
- Follow development best practices.
