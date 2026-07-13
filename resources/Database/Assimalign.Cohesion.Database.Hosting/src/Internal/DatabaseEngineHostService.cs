using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Hosting.Internal;

/// <summary>
/// Maps one <see cref="IDatabaseEngine"/>'s lifecycle onto the host: the engine starts
/// with the host and stops with it, through the root contract's
/// <see cref="IDatabaseEngine.StartAsync"/>/<see cref="IDatabaseEngine.StopAsync"/> seam.
/// </summary>
/// <remarks>
/// <see cref="DatabaseApplication"/> registers one of these per composed engine
/// <em>first</em>, ahead of the durability worker slots and the endpoint. A host starts
/// services in registration order and stops them in reverse, so engines are running
/// before any worker slot pumps them and stop only after the endpoint has drained and
/// the worker slots have quiesced — the engine's own stop then performs the final
/// durable flush. The engine contract makes start idempotent, so an engine the
/// composition root already started (for example to seed databases) is served as-is.
/// </remarks>
internal sealed class DatabaseEngineHostService : IHostService
{
    private readonly IDatabaseEngine _engine;

    internal DatabaseEngineHostService(IDatabaseEngine engine)
    {
        _engine = engine;
        Id = ServiceId.New();
    }

    /// <inheritdoc />
    public ServiceId Id { get; }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
        => _engine.StartAsync(cancellationToken);

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
        => _engine.StopAsync(cancellationToken);
}
