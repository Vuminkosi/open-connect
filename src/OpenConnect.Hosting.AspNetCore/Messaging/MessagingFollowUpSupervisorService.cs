using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions.Messaging;

namespace OpenConnect.Hosting.AspNetCore.Messaging;

/// <summary>
/// Durable background supervisor polling <see cref="IFollowUpSchedulerCoordinator"/> with bounded batch claims,
/// lease fencing, injected <see cref="TimeProvider"/>, and graceful shutdown.
/// </summary>
public sealed class MessagingFollowUpSupervisorService : BackgroundService
{
    private readonly IFollowUpSchedulerCoordinator _coordinator;
    private readonly IMessagingFollowUpDispatcher _dispatcher;
    private readonly IOptions<MessagingHostingOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MessagingFollowUpSupervisorService> _logger;

    public MessagingFollowUpSupervisorService(
        IFollowUpSchedulerCoordinator coordinator,
        IMessagingFollowUpDispatcher dispatcher,
        IOptions<MessagingHostingOptions> options,
        ILogger<MessagingFollowUpSupervisorService> logger,
        TimeProvider? timeProvider = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Executes a single bounded claim-and-dispatch cycle using the configured lease owner and fence.</summary>
    public async Task<int> ExecuteSingleCycleAsync(CancellationToken cancellationToken)
    {
        var opts = _options.Value;
        if (!opts.Enabled)
            return 0;

        var batchSize = Math.Clamp(opts.FollowUpBatchSize, 1, 200);
        var leaseDuration = opts.FollowUpLeaseDuration <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(30)
            : opts.FollowUpLeaseDuration;
        var leaseOwner = string.IsNullOrWhiteSpace(opts.FollowUpLeaseOwner)
            ? $"gateway-{Environment.MachineName}"
            : opts.FollowUpLeaseOwner;

        var claimed = await _coordinator.ClaimDueBatchAsync(
            leaseOwner,
            leaseDuration,
            batchSize,
            cancellationToken).ConfigureAwait(false);

        var processed = 0;
        foreach (var item in claimed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _coordinator.AuthorizeAndDispatchAsync(
                    item.TenantId,
                    item.FollowUpId,
                    leaseOwner,
                    item.LeaseFence,
                    (record, ct) => _dispatcher.DispatchAsync(record, ct),
                    cancellationToken).ConfigureAwait(false);
                processed++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (MessagingDomainException ex)
            {
                _logger.LogWarning(
                    "Follow-up {FollowUpId} dispatch stopped with domain code {ErrorCode}.",
                    item.FollowUpId.Value,
                    ex.ErrorCode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Follow-up {FollowUpId} dispatch failed with {ExceptionType}.",
                    item.FollowUpId.Value,
                    ex.GetType().Name);
            }
        }

        return processed;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ExecuteSingleCycleAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Follow-up supervisor poll cycle failed ({ExceptionType}).", ex.GetType().Name);
            }

            var interval = _options.Value.FollowUpPollInterval;
            if (interval <= TimeSpan.Zero)
                interval = TimeSpan.FromSeconds(5);

            try
            {
                await Task.Delay(interval, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
