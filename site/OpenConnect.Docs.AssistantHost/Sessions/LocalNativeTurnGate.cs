using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Agents.Execution;

namespace OpenConnect.Docs.AssistantHost.Sessions;

// Development-only gate: every owner and its state must be in this process.
internal sealed class LocalNativeTurnGate : INativeSessionTurnGate
{
    private const int Capacity = 2048;
    private readonly Dictionary<NativeAgentSessionBinding, SemaphoreSlim> _gates = [];

    public Task<INativeSessionTurnOwnership?> TryAcquireAsync(NativeAgentSessionBinding binding,
        AgentExecutionContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.IdentityKind != AgentIdentityMode.ActiveUser || binding.SessionId != context.SessionId ||
            binding.TenantId.Value.ToString() != context.TenantScope ||
            binding.PrincipalId.Value.ToString() != context.PrincipalOrWorkloadScope)
            return Task.FromResult<INativeSessionTurnOwnership?>(null);
        lock (_gates)
        {
            if (!_gates.TryGetValue(binding, out var gate))
            {
                if (_gates.Count >= Capacity)
                    return Task.FromResult<INativeSessionTurnOwnership?>(null);
                _gates.Add(binding, gate = new SemaphoreSlim(1, 1));
            }
            return Task.FromResult<INativeSessionTurnOwnership?>(gate.Wait(0) ? new Ownership(gate) : null);
        }
    }

    private sealed class Ownership(SemaphoreSlim gate) : INativeSessionTurnOwnership
    {
        private int _disposed;
        public CancellationToken OwnershipLost => CancellationToken.None;
        public Task ValidateOwnershipAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
