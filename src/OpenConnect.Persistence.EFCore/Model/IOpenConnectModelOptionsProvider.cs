namespace OpenConnect.Persistence.EFCore.Model;

public interface IOpenConnectModelOptionsProvider
{
    OpenConnectModelOptions ModelOptions { get; }
}
