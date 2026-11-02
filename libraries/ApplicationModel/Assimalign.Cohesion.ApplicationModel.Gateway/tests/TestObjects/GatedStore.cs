using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// An in-memory record store whose saves can be held, so a test can keep a save in flight.
/// </summary>
internal sealed class GatedStore : IApplicationGatewayStore
{
    private readonly InMemoryApplicationGatewayStore _inner = new();
    private TaskCompletionSource? _gate;
    private TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _saves;

    /// <summary>Gets how many saves reached the inner store.</summary>
    public int Saves => Volatile.Read(ref _saves);

    /// <summary>Gets a task that completes when a save is held at the gate.</summary>
    public Task Held => _held.Task;

    /// <summary>Holds every later save until <see cref="Release"/>.</summary>
    public void Hold()
    {
        _held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Lets the held saves through.</summary>
    public void Release()
    {
        _gate?.TrySetResult();
        _gate = null;
    }

    public ValueTask<ApplicationGatewayRecord?> LoadAsync(GatewayName gateway, CancellationToken cancellationToken = default)
    {
        return _inner.LoadAsync(gateway, cancellationToken);
    }

    public ValueTask<ApplicationGatewayRecord> AcquireAsync(GatewayName gateway, CancellationToken cancellationToken = default)
    {
        return _inner.AcquireAsync(gateway, cancellationToken);
    }

    public async ValueTask<bool> SaveAsync(ApplicationGatewayRecord record, CancellationToken cancellationToken = default)
    {
        if (_gate is { } gate)
        {
            _held.TrySetResult();
            await gate.Task.ConfigureAwait(false);
        }

        bool saved = await _inner.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _saves);
        return saved;
    }
}
