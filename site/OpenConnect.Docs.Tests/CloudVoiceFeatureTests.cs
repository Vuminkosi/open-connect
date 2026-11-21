using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudVoiceFeatureTests
{
    [Fact]
    public async Task RegistersVoiceServicesAndMapsSeparateGatewayAndWorkerRoutes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddCloudVoiceFeature();
        await using var app = builder.Build();
        var gateway = new CloudGatewayClientTrust([new string('1', 64)]);
        var worker = new CloudWorkerClientTrust([new(Guid.NewGuid(), new string('2', 64))], gateway);
        app.MapCloudVoiceGatewayEndpoints(gateway, true);
        app.MapCloudVoiceWorkerEndpoints(worker, true);

        var paths = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Select(endpoint => endpoint.RoutePattern.RawText).ToArray();
        Assert.Contains("/cloud/internal/v1/voice-synthesis/quota/fingerprint-key/{requestId:guid}", paths);
        Assert.Contains("/cloud/internal/v1/voice-synthesis/output-privacy", paths);
        Assert.Contains("/cloud/internal/v1/voice-synthesis/quota/reservations/{reservationId:guid}/claims/{claimId:guid}/consume", paths);
        Assert.Contains("/cloud/internal/v1/voice-synthesis/quota/reservations/{reservationId:guid}/claims/{claimId:guid}/complete", paths);
        Assert.NotNull(app.Services.GetService<CloudVoicePrivacyService>());
        Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(ICloudVoiceAccessStore) &&
            descriptor.ImplementationType == typeof(EfCloudVoiceAccessStore) && descriptor.Lifetime == ServiceLifetime.Scoped);
    }
}
