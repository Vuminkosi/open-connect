namespace OpenConnect.Persistence.EFCore.Model;

public sealed record OpenConnectModelOptions
{
    public string Schema { get; init; } = "openconnect";
    public string SqliteTablePrefix { get; init; } = "oc_";
    public bool UseSqlitePrefix { get; init; }
    public static OpenConnectModelOptions Default { get; } = new();
}
