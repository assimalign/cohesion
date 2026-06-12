using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections.Tests;

/// <summary>
/// An <see cref="IConnectionLayer"/> double that records the order it was applied in, optionally
/// transforms capabilities, and optionally wraps the upgraded connection in a
/// <see cref="LayerWrappedConnection"/> (set <c>wrapConnection</c> to <see langword="false"/> for
/// a pass-through layer that returns the inner connection unchanged).
/// </summary>
internal sealed class RecordingConnectionLayer : IConnectionLayer
{
    private readonly string _name;
    private readonly List<string> _upgrades;
    private readonly Func<ConnectionCapabilities, ConnectionCapabilities> _describe;
    private readonly bool _wrapConnection;

    public RecordingConnectionLayer(
        string name,
        List<string> upgrades,
        Func<ConnectionCapabilities, ConnectionCapabilities>? describe = null,
        bool wrapConnection = true)
    {
        _name = name;
        _upgrades = upgrades;
        _describe = describe ?? (static capabilities => capabilities);
        _wrapConnection = wrapConnection;
    }

    public ConnectionCapabilities Describe(ConnectionCapabilities capabilities) => _describe(capabilities);

    public ValueTask<IConnection> UpgradeAsync(IConnection connection, CancellationToken cancellationToken = default)
    {
        _upgrades.Add(_name);

        return ValueTask.FromResult(_wrapConnection
            ? new LayerWrappedConnection(connection, _name)
            : connection);
    }
}
