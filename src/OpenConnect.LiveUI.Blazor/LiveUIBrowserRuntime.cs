using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using OpenConnect.LiveUI;
using OpenConnect.LiveUI.Agents;

namespace OpenConnect.LiveUI.Blazor;

public sealed class LiveUIBrowserRuntime(
    IJSRuntime jsRuntime,
    NavigationManager navigationManager,
    IOptions<LiveUIOptions> options,
    LiveUIManifestState? manifestState = null) : ILiveUIRuntime, IAsyncDisposable
{
    private const string ModulePath = "./_content/OpenConnect.LiveUI.Blazor/liveui.js";
    private IJSObjectReference? _module;
    private bool _disposed;

    public async ValueTask<LiveUIRuntimeResult> ExecuteAsync(
        LiveUIExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_disposed) return Failed("runtime_disposed");

        var currentPath = navigationManager.ToAbsoluteUri(navigationManager.Uri).AbsolutePath;
        var expectedPath = context.Action.ExpectedPath ?? navigationManager.ToAbsoluteUri(context.Route.Path).AbsolutePath;
        if (context.Action.ExpectedPath is null && (context.Route.Path.Contains('{') || context.Route.Path.Contains('}')) ||
            !LiveUIRouteMatcher.TryMatch(context.Route.Path, expectedPath, out _))
            return new LiveUIRuntimeResult(LiveUIActionOutcome.Conflict, "route_mismatch", false);
        if (!string.Equals(currentPath, expectedPath, StringComparison.OrdinalIgnoreCase))
            return new LiveUIRuntimeResult(LiveUIActionOutcome.Conflict, "route_mismatch", false);
        if (context.Target?.Id.StartsWith(LiveUIRuntimeTargetIds.Prefix, StringComparison.Ordinal) == true &&
            (manifestState is null || !manifestState.IsRuntimeTargetCurrent(
                new LiveUIAgentContext(context.Action.SessionId, context.Route.Id) { CurrentPath = currentPath },
                context.Manifest, context.Target.Id)))
            return new LiveUIRuntimeResult(LiveUIActionOutcome.Conflict, "runtime_target_stale", false);

        if (context.Action.Type == LiveUIActionType.Navigate)
            return await NavigateAsync(context, cancellationToken).ConfigureAwait(false);

        try
        {
            _module ??= await jsRuntime.InvokeAsync<IJSObjectReference>(
                "import", cancellationToken, ModulePath).ConfigureAwait(false);
            var result = await _module.InvokeAsync<BrowserResult>(
                "execute", cancellationToken, context with { Route = context.Route with { Path = expectedPath } },
                (int)context.Action.Timeout.TotalMilliseconds,
                options.Value.Discovery.EnableRuntimeFallback).ConfigureAwait(false);
            if (!Enum.TryParse<LiveUIActionOutcome>(result.Outcome, ignoreCase: true, out var outcome))
                return Failed("runtime_result_invalid");
            return new LiveUIRuntimeResult(outcome, SafeCode(result.Code), result.PostconditionVerified,
                result.VerifiedState is null ? null : SafeCode(result.VerifiedState), SafeMeasurements(result.Measurements));
        }
        catch (JSDisconnectedException)
        {
            return new LiveUIRuntimeResult(LiveUIActionOutcome.Uncertain, "browser_disconnected", false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JSException)
        {
            return new LiveUIRuntimeResult(LiveUIActionOutcome.Failed, "browser_runtime_failed", false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_module is not null)
        {
            try { await _module.DisposeAsync().ConfigureAwait(false); }
            catch (JSDisconnectedException) { }
        }
    }

    private async ValueTask<LiveUIRuntimeResult> NavigateAsync(
        LiveUIExecutionContext context,
        CancellationToken cancellationToken)
    {
        var destination = context.Manifest.Routes.SingleOrDefault(
            route => route.Id == context.Action.Parameters.DestinationRouteId);
        if (destination is null || !destination.Path.StartsWith("/", StringComparison.Ordinal) ||
            destination.Path.StartsWith("//", StringComparison.Ordinal) || destination.Path.Contains('\\') ||
            destination.Path.Contains('{') || destination.Path.Contains('}'))
            return new LiveUIRuntimeResult(LiveUIActionOutcome.Denied, "route_invalid", false);

        var expectedPath = navigationManager.ToAbsoluteUri(destination.Path).AbsolutePath;
        try
        {
            navigationManager.NavigateTo(destination.Path);
            _module ??= await jsRuntime.InvokeAsync<IJSObjectReference>(
                "import", cancellationToken, ModulePath).ConfigureAwait(false);
            var result = await _module.InvokeAsync<BrowserResult>(
                "awaitRoute", cancellationToken, expectedPath, destination, (int)context.Action.Timeout.TotalMilliseconds,
                options.Value.Discovery.EnableRuntimeFallback)
                .ConfigureAwait(false);
            if (!Enum.TryParse<LiveUIActionOutcome>(result.Outcome, ignoreCase: true, out var outcome))
                return Failed("runtime_result_invalid");
            return new LiveUIRuntimeResult(outcome, SafeCode(result.Code), result.PostconditionVerified,
                result.VerifiedState is null ? null : SafeCode(result.VerifiedState));
        }
        catch (JSDisconnectedException)
        {
            return new LiveUIRuntimeResult(LiveUIActionOutcome.Uncertain, "browser_disconnected", false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JSException)
        {
            return new LiveUIRuntimeResult(LiveUIActionOutcome.Failed, "browser_runtime_failed", false);
        }
    }

    private static LiveUIRuntimeResult Failed(string code) => new(LiveUIActionOutcome.Failed, code, false);

    private static string SafeCode(string value) =>
        value.Length <= 80 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            ? value
            : "runtime_result_invalid";

    private static LiveUIRuntimeMeasurements? SafeMeasurements(BrowserMeasurements? measurements)
    {
        static double? SafeMilliseconds(double? value) =>
            value is { } milliseconds && double.IsFinite(milliseconds) && milliseconds is >= 0 and <= 120_000
                ? milliseconds
                : null;

        var targetResolution = SafeMilliseconds(measurements?.TargetResolutionMilliseconds);
        var browserAction = SafeMilliseconds(measurements?.BrowserActionMilliseconds);
        return targetResolution is null && browserAction is null
            ? null
            : new LiveUIRuntimeMeasurements(targetResolution, browserAction);
    }

    private sealed record BrowserMeasurements(double? TargetResolutionMilliseconds, double? BrowserActionMilliseconds);
    private sealed record BrowserResult(
        string Outcome,
        string Code,
        bool PostconditionVerified,
        string? VerifiedState,
        BrowserMeasurements? Measurements = null);
}
