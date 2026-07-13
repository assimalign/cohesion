using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// A minimal <see cref="IDatabaseApplicationBuilder"/> that records registrations,
/// proving the model verb composes against the area root's builder seam alone —
/// no hosting reference is involved anywhere in these tests (COHRES001 stays
/// intact for <c>Database.Sql</c>).
/// </summary>
internal sealed class RecordingApplicationBuilder : IDatabaseApplicationBuilder
{
    private readonly List<IDatabaseEngine> _engines = new();

    public IReadOnlyList<IDatabaseEngine> Engines => _engines;

    public IDatabaseApplicationBuilder AddEngine(IDatabaseEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engines.Add(engine);
        return this;
    }

    public IDatabaseApplicationBuilder AddServer(IDatabaseServer server)
        => throw new NotSupportedException("The verb under test registers engines only.");

    public IDatabaseApplicationBuilder AddServer(Func<IReadOnlyList<IDatabaseEngine>, IDatabaseServer> configure)
        => throw new NotSupportedException("The verb under test registers engines only.");

    public IDatabaseApplication Build()
        => throw new NotSupportedException("Building is the hosting layer's job — deliberately not simulated here.");
}
