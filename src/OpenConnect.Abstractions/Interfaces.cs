namespace OpenConnect.Abstractions;

public interface IClock { DateTimeOffset UtcNow { get; } }
public interface IIdGenerator { Ulid NewUlid(); string NewCorrelationId(); }
public interface IAgentDefinitionRegistry { AgentDefinition? Get(AgentId agentId); IReadOnlyList<AgentDefinition> GetAll(); void Register(AgentDefinition definition); }
public interface IToolCatalog { ToolDescriptor? Get(ToolId toolId); IReadOnlyList<ToolDescriptor> GetForAgent(AgentId agentId); IReadOnlyList<ToolDescriptor> GetAuthorized(AgentId agentId, PrincipalId principal, TenantId tenant); }
public interface ICapabilityRegistry { CapabilityDescriptor? Get(CapabilityId capabilityId); IReadOnlyList<CapabilityDescriptor> GetReady(CapabilityKind kind); void Register(CapabilityDescriptor descriptor); void MarkStale(CapabilityId capabilityId); }
public interface ITaskStore { Task<TaskRecord> AcceptAsync(TaskAcceptance acceptance, CancellationToken cancellationToken = default); Task<TaskRecord?> GetAsync(TaskId taskId, CancellationToken cancellationToken = default); Task<TaskRecord?> GetByIdempotencyKeyAsync(string clientTaskId, TenantId tenant, PrincipalId principal, CancellationToken cancellationToken = default); }
public interface IEventJournal { Task<long> AppendAsync(EventRecord @event, CancellationToken cancellationToken = default); IAsyncEnumerable<EventRecord> ReplayAsync(SessionId sessionId, long afterSequence = 0, CancellationToken cancellationToken = default); }
public interface IOutboxStore { Task<OutboxEntry> EnqueueAsync(OutboxEntry entry, CancellationToken cancellationToken = default); Task<IReadOnlyList<OutboxEntry>> GetPendingAsync(int limit, CancellationToken cancellationToken = default); Task MarkDeliveredAsync(Guid entryId, CancellationToken cancellationToken = default); }
public interface IWorkerCapabilityRegistry { WorkerCapabilityDescriptor? Get(OpenConnectWorkerKind kind); IReadOnlyList<WorkerCapabilityDescriptor> GetAll(); IReadOnlyList<WorkerCapabilityDescriptor> GetReady(); void Register(WorkerCapabilityDescriptor descriptor); }
