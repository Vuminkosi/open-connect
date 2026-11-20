using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OpenConnect.Docs.Cloud;

public sealed class CloudControlPlaneDesignTimeFactory : IDesignTimeDbContextFactory<CloudControlPlaneDbContext>
{
    public CloudControlPlaneDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__CloudControlPlane");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Set ConnectionStrings__CloudControlPlane before running Cloud control-plane EF commands.");
        }

        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new CloudControlPlaneDbContext(options);
    }
}
