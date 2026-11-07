using System.Text.Json.Serialization;

namespace OpenConnect.Abstractions;

[JsonConverter(typeof(KebabCaseStringEnumConverter<CapabilityKind>))]
public enum CapabilityKind { Security, Model, Tool, Transport }
public sealed record AgentDefinition(AgentId Id, string Name);
public sealed record ToolDescriptor(ToolId Id, string Name, AgentId AgentId);
public sealed record CapabilityDescriptor(CapabilityId Id, CapabilityKind Kind, DetectorState State);
public sealed record TaskAcceptance(TaskId TaskId, TenantId TenantId, PrincipalId PrincipalId, string IdempotencyKey);
// AgentRunState is the canonical public enum name used by the abstractions contract.
public sealed record TaskRecord(TaskId Id, TenantId TenantId, PrincipalId PrincipalId, AgentRunState State);
public sealed record EventRecord(SessionId SessionId, long Sequence, string Type, string Data);
public sealed record OutboxEntry(Guid Id, string Type, string Payload);
