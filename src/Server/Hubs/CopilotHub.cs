using Microsoft.AspNetCore.SignalR;

namespace Server.Hubs;

public sealed class CopilotHub : Hub
{
    public Task JoinRun(string runId) => Groups.AddToGroupAsync(Context.ConnectionId, runId);

    public Task LeaveRun(string runId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, runId);
}
