using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;
using OpenConnect.Agents.Templates;

namespace OpenConnect.Samples.Blazor.Agents;

/// <summary>
/// Canonical definitions and DI registration for the 3 operational Todo agents.
/// </summary>
public static class TodoOperationalAgents
{
    public const string TaskMonitoringAgentId = "task-monitoring-agent";
    public const string TaskFollowUpAgentId = "task-followup-agent";
    public const string TaskSupervisorAgentId = "task-supervisor-agent";

    public static readonly AgentId MonitoringAgent = new(TaskMonitoringAgentId);
    public static readonly AgentId FollowUpAgent = new(TaskFollowUpAgentId);
    public static readonly AgentId SupervisorAgent = new(TaskSupervisorAgentId);

    public static readonly IReadOnlyList<OperationalAgentDescriptor> All =
    [
        new OperationalAgentDescriptor(
            MonitoringAgent,
            "Task Monitoring Agent",
            "Detects expired deadlines on urgent tasks and dispatches encrypted alerts to assigned employees.",
            ["messaging.send", "messaging.followup", "todo.query_tasks", "todo.record_outcome"]),
        new OperationalAgentDescriptor(
            FollowUpAgent,
            "Task Follow-Up Agent",
            "Coordinates durable follow-ups, tracks employee replies, and updates task status with audit trails.",
            ["messaging.send", "messaging.followup", "todo.query_tasks", "todo.update_status", "todo.record_outcome"]),
        new OperationalAgentDescriptor(
            SupervisorAgent,
            "Task Supervisor Agent",
            "Handles escalations, supervisor approvals, and authorized task reassignments within a tenant.",
            ["messaging.send", "messaging.approval", "todo.query_tasks", "todo.assign_task", "todo.request_approval", "todo.record_outcome"])
    ];

    public static bool IsKnownAgent(AgentId agentId) =>
        All.Any(a => string.Equals(a.AgentId.Value, agentId.Value, StringComparison.Ordinal));

    public static IServiceCollection AddTodoOperationalAgents(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOpenConnectAgent(TaskMonitoringAgentId);
        services.AddOpenConnectAgent(TaskFollowUpAgentId);
        services.AddOpenConnectAgent(TaskSupervisorAgentId);
        return services;
    }
}

public sealed record OperationalAgentDescriptor(
    AgentId AgentId,
    string DisplayName,
    string Description,
    IReadOnlyList<string> Capabilities);
