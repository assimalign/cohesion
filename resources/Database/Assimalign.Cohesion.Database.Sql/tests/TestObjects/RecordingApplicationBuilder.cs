using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>Records dependency-free engine intent without a hosting dependency.</summary>
internal sealed class RecordingApplicationBuilder : IDatabaseApplicationBuilder
{
    public List<Func<IDatabaseApplicationContext, DatabaseEngine>> Factories { get; } = [];

    public IDatabaseApplicationBuilder AddEngine(DatabaseEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return AddEngine(_ => engine);
    }

    public IDatabaseApplicationBuilder AddEngine(Func<IDatabaseApplicationContext, DatabaseEngine> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Factories.Add(configure);
        return this;
    }

    public DatabaseEngine MaterializeEngine() => Factories[0](new EmptyContext());

    public IDatabaseApplication Build()
        => throw new NotSupportedException("The hosting layer builds applications.");

    private sealed class EmptyContext : IDatabaseApplicationContext
    {
        public IReadOnlyList<DatabaseEngine> Engines => [];
        public IReadOnlyList<DatabaseServer> Servers => [];
        public DatabaseEngine GetEngine(string name) => throw new KeyNotFoundException(name);
    }
}