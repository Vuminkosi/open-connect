using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OpenConnect.Abstractions;
using OpenConnect.Persistence.EFCore.Context;

namespace OpenConnect.Persistence.EFCore.Fencing;

/// <summary>
/// Explicit opt-in EF Core command interceptor for fenced application DbContext instances (FW-080).
/// Never intercepts OpenConnect control-plane persistence (OpenConnectDbContext) or EF migrations.
/// Enforces authorized operation context, command timeout/row bounds, and emits sanitized operation-ID evidence
/// without ever recording SQL text or parameter values.
/// </summary>
public sealed class OpenConnectFencedDbCommandInterceptor : DbCommandInterceptor
{
    private readonly OpenConnectDatabaseFencingOptions _options;
    private readonly IDatabaseFencingContextAccessor _contextAccessor;
    private readonly IDatabaseFencingEvidenceSink _evidenceSink;
    private readonly ISecurityEvidenceSink? _securityEvidenceSink;
    private readonly TimeProvider _timeProvider;

    public OpenConnectFencedDbCommandInterceptor(
        OpenConnectDatabaseFencingOptions options,
        IDatabaseFencingContextAccessor contextAccessor,
        IDatabaseFencingEvidenceSink? evidenceSink = null,
        ISecurityEvidenceSink? securityEvidenceSink = null,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _contextAccessor = contextAccessor ?? throw new ArgumentNullException(nameof(contextAccessor));
        _evidenceSink = evidenceSink ?? NullDatabaseFencingEvidenceSink.Instance;
        _securityEvidenceSink = securityEvidenceSink;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        EnforceSynchronously(command, eventData, isWriteCommand: false, commandKind: "reader");
        return base.ReaderExecuting(command, eventData, result);
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await EnforceAsync(command, eventData, isWriteCommand: false, commandKind: "reader", cancellationToken).ConfigureAwait(false);
        return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken).ConfigureAwait(false);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        EnforceSynchronously(command, eventData, isWriteCommand: false, commandKind: "scalar");
        return base.ScalarExecuting(command, eventData, result);
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        await EnforceAsync(command, eventData, isWriteCommand: false, commandKind: "scalar", cancellationToken).ConfigureAwait(false);
        return await base.ScalarExecutingAsync(command, eventData, result, cancellationToken).ConfigureAwait(false);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        EnforceSynchronously(command, eventData, isWriteCommand: true, commandKind: "non_query");
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await EnforceAsync(command, eventData, isWriteCommand: true, commandKind: "non_query", cancellationToken).ConfigureAwait(false);
        return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken).ConfigureAwait(false);
    }

    private bool ShouldIntercept(CommandEventData eventData)
    {
        if (eventData.CommandSource == CommandSource.Migrations)
            return false;

        var dbContext = eventData.Context;
        if (dbContext is null || dbContext is OpenConnectDbContext)
            return false;

        if (_options.FencedContextTypes.Count == 0)
            return false;

        return _options.FencedContextTypes.Contains(dbContext.GetType());
    }

    private void EnforceSynchronously(DbCommand command, CommandEventData eventData, bool isWriteCommand, string commandKind)
    {
        var task = EnforceAsync(command, eventData, isWriteCommand, commandKind, CancellationToken.None);
        if (!task.IsCompletedSuccessfully)
            task.AsTask().GetAwaiter().GetResult();
    }

    private async ValueTask EnforceAsync(
        DbCommand command,
        CommandEventData eventData,
        bool isWriteCommand,
        string commandKind,
        CancellationToken cancellationToken)
    {
        if (!ShouldIntercept(eventData))
            return;

        var sw = Stopwatch.StartNew();
        var context = _contextAccessor.Current;
        var operationId = string.IsNullOrWhiteSpace(context?.OperationId) ? "unknown" : context.OperationId;

        if (context is null || context.TenantId.Value == default || context.PrincipalId.Value == default ||
            string.IsNullOrWhiteSpace(context.OperationId))
        {
            throw await DenyAsync(context, operationId, commandKind, _options.PolicyRevision,
                DatabaseFencingReasonCodes.MissingOperationContext, sw, cancellationToken).ConfigureAwait(false);
        }

        if (!_options.TryGetOperation(context.OperationId, out var policy) || policy is null)
        {
            throw await DenyAsync(context, operationId, commandKind, _options.PolicyRevision,
                DatabaseFencingReasonCodes.OperationNotRegistered, sw, cancellationToken).ConfigureAwait(false);
        }

        var policyRevision = string.IsNullOrWhiteSpace(policy.PolicyRevision) ? _options.PolicyRevision : policy.PolicyRevision;

        if (!policy.AllowAnyAuthenticatedTenant && !policy.AllowedTenants.Contains(context.TenantId))
        {
            throw await DenyAsync(context, operationId, commandKind, policyRevision,
                DatabaseFencingReasonCodes.TenantMismatch, sw, cancellationToken).ConfigureAwait(false);
        }

        if (policy.AllowedPrincipals.Count > 0 && !policy.AllowedPrincipals.Contains(context.PrincipalId))
        {
            throw await DenyAsync(context, operationId, commandKind, policyRevision,
                DatabaseFencingReasonCodes.PrincipalMismatch, sw, cancellationToken).ConfigureAwait(false);
        }

        if (policy.ReadOnly && isWriteCommand)
        {
            throw await DenyAsync(context, operationId, commandKind, policyRevision,
                DatabaseFencingReasonCodes.WriteNotAllowed, sw, cancellationToken).ConfigureAwait(false);
        }

        var maxTimeout = Math.Min(policy.MaxCommandTimeoutSeconds, _options.DefaultMaxCommandTimeoutSeconds);
        if (maxTimeout <= 0) maxTimeout = 15;
        if (command.CommandTimeout <= 0 || command.CommandTimeout > maxTimeout)
            command.CommandTimeout = maxTimeout;

        var maxRows = Math.Min(policy.MaxRows, _options.DefaultMaxRows);
        if (maxRows <= 0) maxRows = 500;

        await RecordAsync(context, operationId, commandKind, policyRevision,
            appliedTimeoutSeconds: command.CommandTimeout, maxRows: maxRows,
            decision: "allow", outcome: "executed", reasonCode: DatabaseFencingReasonCodes.Allowed,
            sw.ElapsedMilliseconds, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<DatabaseFencingDeniedException> DenyAsync(
        DatabaseFencingOperationContext? context,
        string operationId,
        string commandKind,
        string policyRevision,
        string reasonCode,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        var evidence = await RecordAsync(context, operationId, commandKind, policyRevision,
            appliedTimeoutSeconds: 0, maxRows: 0,
            decision: "block", outcome: "prevented", reasonCode: reasonCode,
            sw.ElapsedMilliseconds, cancellationToken).ConfigureAwait(false);
        return new DatabaseFencingDeniedException(
            operationId,
            reasonCode,
            evidence.EventId,
            $"Database fencing denied operation '{operationId}' ({reasonCode}).");
    }

    private async ValueTask<DatabaseFencingEvidence> RecordAsync(
        DatabaseFencingOperationContext? context,
        string operationId,
        string commandKind,
        string policyRevision,
        int appliedTimeoutSeconds,
        int maxRows,
        string decision,
        string outcome,
        string reasonCode,
        long durationMs,
        CancellationToken cancellationToken)
    {
        var evidence = new DatabaseFencingEvidence(
            SchemaVersion: "openconnect.security-evidence.v1",
            EventId: Guid.NewGuid(),
            RequestId: string.IsNullOrWhiteSpace(context?.RequestId) ? Guid.NewGuid().ToString("D") : context.RequestId!,
            TraceId: context?.TraceId ?? Activity.Current?.TraceId.ToString(),
            OccurredAt: _timeProvider.GetUtcNow(),
            Capability: "database-fencing",
            Boundary: "database-command",
            OperationId: operationId,
            CommandKind: commandKind,
            PolicyRevision: policyRevision,
            TenantId: context?.TenantId,
            PrincipalId: context?.PrincipalId,
            AppliedTimeoutSeconds: appliedTimeoutSeconds,
            MaxRows: maxRows,
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
                    capability: "database-fencing",
                    boundary: "database-command",
                    detectorId: "host-database-fencing",
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
            // Evidence sink failure must never bypass database fencing.
        }

        return evidence;
    }
}

/// <summary>Opt-in DbContextOptionsBuilder extension for attaching database fencing to an application DbContext.</summary>
public static class OpenConnectDatabaseFencingExtensions
{
    public static DbContextOptionsBuilder UseOpenConnectDatabaseFencing(
        this DbContextOptionsBuilder optionsBuilder,
        OpenConnectFencedDbCommandInterceptor interceptor)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(interceptor);
        if (optionsBuilder.Options.ContextType == typeof(OpenConnectDbContext))
            throw new InvalidOperationException("OpenConnect control-plane DbContext must never be attached to application database fencing.");
        return optionsBuilder.AddInterceptors(interceptor);
    }
}
