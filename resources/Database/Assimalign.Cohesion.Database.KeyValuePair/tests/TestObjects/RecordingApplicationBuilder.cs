using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

/// <summary>
/// A minimal <see cref="IDatabaseApplicationBuilder"/> that records registrations,
/// proving the model verbs compose against the area root's builder seam alone —
/// no hosting reference is involved anywhere in these tests (COHRES001 stays
/// intact for <c>Database.KeyValuePair</c>).
/// </summary>
internal sealed class RecordingApplicationBuilder : IDatabaseApplicationBuilder
{
    private readonly List<IDatabaseEngine> _engines = new();
    private readonly List<IDatabaseServer> _servers = new();

    public IReadOnlyList<IDatabaseEngine> Engines => _engines;

    public IReadOnlyList<IDatabaseServer> Servers => _servers;

    public IDatabaseApplicationBuilder AddEngine(IDatabaseEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engines.Add(engine);
        return this;
    }

    public IDatabaseApplicationBuilder AddServer(IDatabaseServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        _servers.Add(server);
        return this;
    }

    public IDatabaseApplicationBuilder AddServer(Func<IDatabaseApplicationContext, IDatabaseServer> configure)
        => throw new NotSupportedException("Deferred factories resolve at Build — the hosting layer's job, deliberately not simulated here.");

    public IDatabaseApplication Build()
        => throw new NotSupportedException("Building is the hosting layer's job — deliberately not simulated here.");
}
