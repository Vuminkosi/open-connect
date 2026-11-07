namespace OpenConnect.Persistence.EFCore.Entities;
internal sealed class TurnEntity
{
    public Guid TurnId { get; set; }
    public string TenantScope { get; set; } = null!;
    public string PrincipalScope { get; set; } = null!;
    public Guid SessionId { get; set; }
    public long Sequence { get; set; }
    public string State { get; set; } = null!;
    public string? Context { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
}
