using Microsoft.EntityFrameworkCore;
using OpenConnect.Persistence.EFCore.Entities;
using OpenConnect.Persistence.EFCore.EntityConfigurations;

namespace OpenConnect.Persistence.EFCore.Model;

public static class OpenConnectModelBuilderExtensions
{
    public static ModelBuilder ApplyOpenConnectModel(this ModelBuilder modelBuilder, OpenConnectModelOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var mapping = options ?? OpenConnectModelOptions.Default;
        modelBuilder.ApplyConfiguration(new SessionEntityConfiguration(mapping));
        modelBuilder.ApplyConfiguration(new TaskEntityConfiguration(mapping));
        modelBuilder.ApplyConfiguration(new TurnEntityConfiguration(mapping));
        modelBuilder.ApplyConfiguration(new EventEntityConfiguration(mapping));
        modelBuilder.ApplyConfiguration(new OutboxEntityConfiguration(mapping));
        modelBuilder.ApplyConfiguration(new LeaseEntityConfiguration(mapping));
        modelBuilder.ApplyConfiguration(new ApprovalEntityConfiguration(mapping));
        modelBuilder.ApplyConfiguration(new PrivacyTokenEntityConfiguration(mapping));
        modelBuilder.ApplyConfiguration(new OperationalRunEntityConfiguration(mapping));
        return modelBuilder;
    }
}
