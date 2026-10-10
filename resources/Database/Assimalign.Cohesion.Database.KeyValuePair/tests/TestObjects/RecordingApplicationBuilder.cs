using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

/// <summary>Records dependency-free engine intent without a hosting dependency.</summary>
internal sealed class RecordingApplicationBuilder : IDatabaseApplicationBuilder
{
    public List<Func<IDatabaseApplicationContext, DatabaseEngine>> Factories { get; } = [];

    /// <summary>Gets the engine names, reserved in registration order.</summary>
    public List<string> Names { get; } = [];

    public IDatabaseApplicationBuilder AddEngine(DatabaseEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return AddEngine(engine.Name, _ => engine);
    }

    public IDatabaseApplicationBuilder AddEngine(string name, Func<IDatabaseApplicationContext, DatabaseEngine> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);
        Names.Add(name);
        Factories.Add(factory);
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