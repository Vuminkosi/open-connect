namespace OpenConnect.Persistence.EFCore.Entities;
internal sealed class PrivacyTokenEntity
{
    public Guid Id { get; set; }
    public string TenantScope { get; set; } = null!;
    public string PrincipalOrWorkloadScope { get; set; } = null!;
    public Guid SessionId { get; set; }
    public Guid? TurnId { get; set; }
    public string TokenId { get; set; } = null!;
    public string EntityType { get; set; } = null!;
    public byte[] EncryptedValue { get; set; } = [];
    public byte[] Nonce { get; set; } = [];
    public byte[] AuthenticationTag { get; set; } = [];
    public byte[] WrappedDataKey { get; set; } = [];
    public string KeyProviderId { get; set; } = null!;
    public string KeyVersion { get; set; } = null!;
    public string BindingDigest { get; set; } = null!;
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
}
