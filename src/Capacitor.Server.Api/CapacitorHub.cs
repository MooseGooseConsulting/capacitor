using Microsoft.AspNetCore.SignalR;
using Capacitor.Server.Data.Entities;

namespace Capacitor.Server.Api;

public interface ICapacitorHubClient {
    Task OnEventAppended(SessionEventRecord ev);
    Task OnSessionStarted(string sessionId, string vendor);
    Task OnSessionEnded(string sessionId);
    Task OnRollupUpdated(string sessionId, int eventCount, long totalTokens, decimal totalCostUsd);
}

public class CapacitorHub : Hub<ICapacitorHubClient> {
    public async Task JoinSessionGroup(string sessionId) {
        var group = $"session_{sessionId.Replace("-", "")}";
        await Groups.AddToGroupAsync(Context.ConnectionId, group);
    }

    public async Task LeaveSessionGroup(string sessionId) {
        var group = $"session_{sessionId.Replace("-", "")}";
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, group);
    }

    public async Task JoinRepoGroup(string repoHash) {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"repo_{repoHash}");
    }

    public async Task JoinMachineGroup(string machineId) {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"machine_{machineId}");
    }
}
