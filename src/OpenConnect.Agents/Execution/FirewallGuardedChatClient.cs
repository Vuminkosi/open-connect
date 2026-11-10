using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Privacy;

namespace OpenConnect.Agents.Execution;

/// <summary>Provides the privacy egress context used when guarding model-bound chat content.</summary>
public interface IEgressContextProvider
{
    /// <summary>Gets the egress context for the current operation.</summary>
    EgressContext GetContext();
}

/// <summary>Provides a fixed egress context for firewall-guarded chat clients.</summary>
public sealed class FixedEgressContextProvider : IEgressContextProvider
{
    private readonly EgressContext _context;

    /// <summary>Creates a provider that always returns the supplied context.</summary>
    public FixedEgressContextProvider(EgressContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <inheritdoc />
    public EgressContext GetContext() => _context;
}

/// <summary>Firewall-decorated <see cref="IChatClient"/> that guards outbound model content fail-closed.</summary>
/// <remarks>Every message carrying non-empty text is evaluated through the egress gate before reaching
/// the inner client; sanitized content is forwarded and denials throw without echoing content.
/// Service resolution and disposal delegate to the inner client via <see cref="DelegatingChatClient"/>.</remarks>
public sealed class FirewallGuardedChatClient : DelegatingChatClient
{
    private readonly IEgressGate _gate;
    private readonly IEgressContextProvider _contextProvider;

    /// <summary>Creates a firewall-guarded decorator over the supplied inner client.</summary>
    public FirewallGuardedChatClient(IChatClient inner, IEgressGate gate, IEgressContextProvider contextProvider)
        : base(inner ?? throw new ArgumentNullException(nameof(inner)))
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _contextProvider = contextProvider ?? throw new ArgumentNullException(nameof(contextProvider));
    }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var guarded = await GuardAsync(messages, cancellationToken).ConfigureAwait(false);
        return await InnerClient.GetResponseAsync(guarded, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var guarded = await GuardAsync(messages, cancellationToken).ConfigureAwait(false);
        await foreach (var update in InnerClient.GetStreamingResponseAsync(guarded, options, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    /// <inheritdoc />
    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        InnerClient.GetService(serviceType, serviceKey);

    private async Task<IReadOnlyList<ChatMessage>> GuardAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        cancellationToken.ThrowIfCancellationRequested();

        EgressContext context;
        try
        {
            context = _contextProvider.GetContext();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            throw FailClosed();
        }

        if (context is null) throw FailClosed();

        var guarded = new List<ChatMessage>();
        foreach (var message in messages)
        {
            ArgumentNullException.ThrowIfNull(message);
            if (string.IsNullOrEmpty(message.Text))
            {
                guarded.Add(message);
                continue;
            }

            EgressDecision decision;
            try
            {
                decision = await _gate.EvaluateAsync(message.Text, context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                throw FailClosed();
            }

            if (decision is null || !decision.Allowed || decision.SanitizedContent is null) throw FailClosed();
            guarded.Add(new ChatMessage(message.Role, decision.SanitizedContent) { AuthorName = message.AuthorName });
        }

        return guarded;
    }

    private static PrivacyBlockedException FailClosed() =>
        new($"{ErrorCodes.PrivacyBlocked}: content blocked by privacy policy.");
}
