using Microsoft.AspNetCore.Mvc;
using OpenConnect.LiveUI.Inference;

namespace OpenConnect.Docs.AssistantHost.LiveUI;

internal static class DocumentationLiveUIEndpoints
{
    public static void MapDocumentationLiveUIInference(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/openconnect/v1/liveui/inference").RequireAuthorization();
        group.MapGet("/worker/availability", async (IDocumentationWorkerPlanner worker, CancellationToken ct) =>
        {
            try
            { return Results.Ok(await worker.GetAvailabilityAsync(ct)); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { return Results.Problem(statusCode: 503, title: "LiveUI worker unavailable."); }
        });
        group.MapPost("/plan", async (GatewayLiveUIInferenceRequest request, HttpContext context,
            DocumentationLiveUIPlanner planner, CancellationToken ct) =>
        {
            var result = await planner.GenerateAsync(request, context.User, ct);
            return result.Response is not null ? Results.Ok(result.Response) :
                Results.Problem(statusCode: result.Status, title: "LiveUI planning request could not be completed.");
        }).WithMetadata(new RequestSizeLimitAttribute(512 * 1024));
    }
}
