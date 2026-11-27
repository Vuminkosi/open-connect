using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;

namespace OpenConnect.Hosting.AspNetCore.Fencing;

/// <summary>
/// Policy-enforcing HTTP handler for SDK-mediated service fencing (FW-070).
/// Denies unauthorized requests BEFORE opening any socket and emits content-free evidence.
/// </summary>
public sealed class FencedHttpMessageHandler : DelegatingHandler
{
    public static readonly HttpRequestOptionsKey<ServiceFencingExecutionContext> ExecutionContextOptionKey =
        new("OpenConnect.ServiceFencing.ExecutionContext");

    private readonly string _serviceId;
    private readonly IOptionsMonitor<ServiceFencingOptions> _options;
    private readonly IServiceFencingContextAccessor _contextAccessor;
    private readonly IFencedHostAddressResolver _resolver;
    private readonly IServiceFencingEvidenceSink _evidenceSink;
    private readonly ISecurityEvidenceSink? _securityEvidenceSink;
    private readonly TimeProvider _timeProvider;

    public FencedHttpMessageHandler(
        string serviceId,
        IOptionsMonitor<ServiceFencingOptions> options,
        IServiceFencingContextAccessor contextAccessor,
        IFencedHostAddressResolver? resolver = null,
        IServiceFencingEvidenceSink? evidenceSink = null,
        TimeProvider? timeProvider = null,
        ISecurityEvidenceSink? securityEvidenceSink = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(contextAccessor);
        _serviceId = serviceId;
        _options = options;
        _contextAccessor = contextAccessor;
        _resolver = resolver ?? SystemFencedHostAddressResolver.Instance;
        _evidenceSink = evidenceSink ?? NullServiceFencingEvidenceSink.Instance;
        _securityEvidenceSink = securityEvidenceSink;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sw = Stopwatch.StartNew();

        var context = request.Options.TryGetValue(ExecutionContextOptionKey, out var explicitContext)
            ? explicitContext
            : _contextAccessor.Current;

        var fencingOptions = _options.CurrentValue;
        if (!fencingOptions.TryGetService(_serviceId, out var policy) || policy is null)
        {
            throw await DenyAsync(context, request.Method.Method, fencingOptions.PolicyRevision,
                ServiceFencingReasonCodes.ServiceNotRegistered, sw, cancellationToken).ConfigureAwait(false);
        }

        var policyRevision = string.IsNullOrWhiteSpace(policy.PolicyRevision) ? fencingOptions.PolicyRevision : policy.PolicyRevision;

        var contextDenial = ServiceFencingValidator.ValidateContext(context, _serviceId, policy);
        if (contextDenial is not null)
        {
            throw await DenyAsync(context, request.Method.Method, policyRevision, contextDenial, sw, cancellationToken).ConfigureAwait(false);
        }

        await ValidateHopAsync(request.RequestUri, request.Method, context!, policy, policyRevision, sw, cancellationToken).ConfigureAwait(false);

        var currentRequest = request;
        var currentOrigin = request.RequestUri!;
        var hops = 0;

        while (true)
        {
            var response = await base.SendAsync(currentRequest, cancellationToken).ConfigureAwait(false);

            if (!IsRedirect(response.StatusCode) || response.Headers.Location is null)
            {
                await RecordAsync(context, currentRequest.Method.Method, policyRevision,
                    decision: "allow", outcome: "executed", reasonCode: ServiceFencingReasonCodes.Allowed,
                    sw.ElapsedMilliseconds, cancellationToken).ConfigureAwait(false);
                return response;
            }

            if (policy.MaxRedirectHops == 0)
            {
                response.Dispose();
                throw await DenyAsync(context, currentRequest.Method.Method, policyRevision,
                    ServiceFencingReasonCodes.RedirectBlocked, sw, cancellationToken).ConfigureAwait(false);
            }

            if (hops >= policy.MaxRedirectHops)
            {
                response.Dispose();
                throw await DenyAsync(context, currentRequest.Method.Method, policyRevision,
                    ServiceFencingReasonCodes.RedirectLimitExceeded, sw, cancellationToken).ConfigureAwait(false);
            }

            var nextUri = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location
                : new Uri(currentOrigin, response.Headers.Location);
            var nextMethod = response.StatusCode == HttpStatusCode.SeeOther
                ? HttpMethod.Get
                : currentRequest.Method;
            response.Dispose();

            await ValidateHopAsync(nextUri, nextMethod, context!, policy, policyRevision, sw, cancellationToken).ConfigureAwait(false);

            var nextRequest = CloneRequestForRedirect(currentRequest, nextUri, nextMethod,
                stripSensitiveHeaders: !ServiceFencingValidator.SameOrigin(currentOrigin, nextUri));
            if (!ReferenceEquals(currentRequest, request))
                currentRequest.Dispose();
            currentRequest = nextRequest;
            currentOrigin = nextUri;
            hops++;
        }
    }

    private async Task ValidateHopAsync(
        Uri? uri,
        HttpMethod method,
        ServiceFencingExecutionContext context,
        FencedServicePolicy policy,
        string policyRevision,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        var destDenial = ServiceFencingValidator.ValidateRequestDestination(uri, method, policy);
        if (destDenial is not null)
            throw await DenyAsync(context, method.Method, policyRevision, destDenial, sw, cancellationToken).ConfigureAwait(false);

        var (_, addressDenial) = await ServiceFencingValidator.ResolveAndValidateAddressesAsync(
            uri!.DnsSafeHost, policy.AllowPrivateNetwork, _resolver, cancellationToken).ConfigureAwait(false);
        if (addressDenial is not null)
            throw await DenyAsync(context, method.Method, policyRevision, addressDenial, sw, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ServiceFencingDeniedException> DenyAsync(
        ServiceFencingExecutionContext? context,
        string method,
        string policyRevision,
        string reasonCode,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        var evidence = await RecordAsync(context, method, policyRevision,
            decision: "block", outcome: "prevented", reasonCode: reasonCode,
            sw.ElapsedMilliseconds, cancellationToken).ConfigureAwait(false);
        return new ServiceFencingDeniedException(
            _serviceId,
            reasonCode,
            evidence.EventId,
            $"Service fencing denied outbound request for service '{_serviceId}' ({reasonCode}).");
    }

    private async Task<ServiceFencingEvidence> RecordAsync(
        ServiceFencingExecutionContext? context,
        string method,
        string policyRevision,
        string decision,
        string outcome,
        string reasonCode,
        long durationMs,
        CancellationToken cancellationToken)
    {
        var evidence = new ServiceFencingEvidence(
            SchemaVersion: "openconnect.security-evidence.v1",
            EventId: Guid.NewGuid(),
            RequestId: string.IsNullOrWhiteSpace(context?.RequestId) ? Guid.NewGuid().ToString("D") : context.RequestId!,
            TraceId: context?.TraceId ?? Activity.Current?.TraceId.ToString(),
            OccurredAt: _timeProvider.GetUtcNow(),
            Capability: "service-fencing",
            Boundary: "egress-http",
            ServiceId: _serviceId,
            PolicyRevision: policyRevision,
            TenantId: context?.TenantId,
            PrincipalId: context?.PrincipalId,
            HttpMethod: method,
            Decision: decision,
            EnforcementOutcome: outcome,
            ReasonCode: reasonCode,
            DurationMs: durationMs);

        try
        {
            await _evidenceSink.RecordAsync(evidence, cancellationToken).ConfigureAwait(false);
            if (_securityEvidenceSink is not null)
            {
                var mappedDecision = decision == "allow" ? SecurityEvidenceDecision.Allow : SecurityEvidenceDecision.Block;
                var mappedOutcome = outcome == "executed" ? SecurityEnforcementOutcome.Executed : SecurityEnforcementOutcome.Prevented;
                var tenantStr = context is not null && context.TenantId.Value != default ? context.TenantId.Value.ToString() : "unauthenticated";
                var appStr = string.IsNullOrWhiteSpace(context?.ApplicationId) ? "default" : context.ApplicationId!;
                var canonical = SecurityEvidenceEvent.Create(
                    eventId: evidence.EventId,
                    requestId: evidence.RequestId,
                    tenantId: tenantStr,
                    applicationId: appStr,
                    occurredAt: evidence.OccurredAt,
                    capability: "service-fencing",
                    boundary: "egress-http",
                    detectorId: "host-service-fencing",
                    detectorVersion: "1.0.0",
                    policyRevision: evidence.PolicyRevision,
                    inspectionStatus: SecurityInspectionStatus.Completed,
                    decision: mappedDecision,
                    enforcementOutcome: mappedOutcome,
                    reasonCode: evidence.ReasonCode,
                    categoryCounts: null,
                    durationMs: Math.Max(0, evidence.DurationMs),
                    traceId: evidence.TraceId);
                await _securityEvidenceSink.RecordAsync(canonical, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Evidence sink outages must never weaken or bypass fencing enforcement.
        }

        return evidence;
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static HttpRequestMessage CloneRequestForRedirect(
        HttpRequestMessage original,
        Uri nextUri,
        HttpMethod nextMethod,
        bool stripSensitiveHeaders)
    {
        var clone = new HttpRequestMessage(nextMethod, nextUri);
        foreach (var header in original.Headers)
        {
            if (stripSensitiveHeaders &&
                (string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(header.Key, "Cookie", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(header.Key, "Proxy-Authorization", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return clone;
    }
}

/// <summary>Creates a hardened SocketsHttpHandler with DNS-rebinding-resistant ConnectCallback validation.</summary>
public static class FencedSocketsHttpHandlerFactory
{
    public static SocketsHttpHandler Create(
        string serviceId,
        IOptionsMonitor<ServiceFencingOptions> options,
        IFencedHostAddressResolver? resolver = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        ArgumentNullException.ThrowIfNull(options);
        var hostResolver = resolver ?? SystemFencedHostAddressResolver.Instance;

        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectCallback = async (context, cancellationToken) =>
            {
                if (!options.CurrentValue.TryGetService(serviceId, out var policy) || policy is null)
                    throw new ServiceFencingDeniedException(serviceId, ServiceFencingReasonCodes.ServiceNotRegistered, Guid.NewGuid(),
                        $"Service '{serviceId}' is not registered in service fencing policy.");

                var normalizedAllowedHost = ServiceFencingValidator.NormalizeHost(policy.AllowedHost);
                var normalizedConnectHost = ServiceFencingValidator.NormalizeHost(context.DnsEndPoint.Host);
                if (!string.Equals(normalizedAllowedHost, normalizedConnectHost, StringComparison.OrdinalIgnoreCase))
                    throw new ServiceFencingDeniedException(serviceId, ServiceFencingReasonCodes.HostNotAllowed, Guid.NewGuid(),
                        $"Connect host is not allowed for service '{serviceId}'.");

                if (!policy.AllowedPorts.Contains(context.DnsEndPoint.Port))
                    throw new ServiceFencingDeniedException(serviceId, ServiceFencingReasonCodes.PortNotAllowed, Guid.NewGuid(),
                        $"Connect port is not allowed for service '{serviceId}'.");

                var (addresses, denialReason) = await ServiceFencingValidator.ResolveAndValidateAddressesAsync(
                    context.DnsEndPoint.Host, policy.AllowPrivateNetwork, hostResolver, cancellationToken).ConfigureAwait(false);
                if (denialReason is not null || addresses is null || addresses.Length == 0)
                    throw new ServiceFencingDeniedException(serviceId, denialReason ?? ServiceFencingReasonCodes.DnsResolutionFailed, Guid.NewGuid(),
                        $"Connect address denied for service '{serviceId}' ({denialReason}).");

                Exception? lastError = null;
                foreach (var ip in addresses)
                {
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(ip, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        socket.Dispose();
                        lastError = ex;
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }

                throw lastError ?? new SocketException((int)SocketError.HostUnreachable);
            }
        };
    }
}
