using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

/// <summary>Records dependency-free engine intent without a hosting dependency.</summary>
internal sealed class RecordingApplicationBuilder : IDatabaseApplicationBuilder
{
    public List<Func<IDatabaseApplicationContext, IDatabaseEngine>> Factories { get; } = [];

    public IDatabaseApplicationBuilder AddEngine(IDatabaseEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return AddEngine(_ => engine);
    }

    public IDatabaseApplicationBuilder AddEngine(Func<IDatabaseApplicationContext, IDatabaseEngine> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Factories.Add(configure);
        return this;
    }

    public IDatabaseEngine MaterializeEngine() => Factories[0](new EmptyContext());

    public IDatabaseApplication Build()
        => throw new NotSupportedException("The hosting layer builds applications.");

    private sealed class EmptyContext : IDatabaseApplicationContext
    {
        public IReadOnlyList<IDatabaseEngine> Engines => [];
        public IReadOnlyList<IDatabaseServer> Servers => [];
        public IDatabaseEngine GetEngine(string name) => throw new KeyNotFoundException(name);
    }
}