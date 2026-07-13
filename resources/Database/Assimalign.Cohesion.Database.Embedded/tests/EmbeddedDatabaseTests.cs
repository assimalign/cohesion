using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Embedded.Tests;

public class EmbeddedDatabaseTests
{
    [Fact(DisplayName = "Cohesion Test [Database] - Embedded: Composes registered engines in order")]
    public void Create_WithEngines_ShouldExposeEnginesInRegistrationOrder()
    {
        // Arrange
        var first = new TestDatabaseEngine("kv", EngineModel.KeyValueStore);
        var second = new TestDatabaseEngine("docs", EngineModel.Document);

        // Act
        var embedded = EmbeddedDatabase.Create(options =>
        {
            options.Engines.Add(first);
            options.Engines.Add(second);
        });

        // Assert
        embedded.Engines.Count.ShouldBe(2);
        embedded.Engines[0].ShouldBeSameAs(first);
        embedded.TryGetEngine("DOCS", out var byName).ShouldBeTrue();
        byName.ShouldBeSameAs(second);
        embedded.TryGetEngine(EngineModel.KeyValueStore, out var byModel).ShouldBeTrue();
        byModel.ShouldBeSameAs(first);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Embedded: Requires at least one engine")]
    public void Create_WithoutEngines_ShouldThrow()
    {
        // Act & Assert
        Should.Throw<DatabaseException>(() => EmbeddedDatabase.Create(_ => { }));
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Embedded: Rejects colliding engine names")]
    public void Create_DuplicateEngineNames_ShouldThrow()
    {
        // Act & Assert
        Should.Throw<DatabaseException>(() => EmbeddedDatabase.Create(options =>
        {
            options.Engines.Add(new TestDatabaseEngine("kv", EngineModel.KeyValueStore));
            options.Engines.Add(new TestDatabaseEngine("kv", EngineModel.Document));
        }));
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Embedded: Disposal stops engines in reverse order")]
    public async Task DisposeAsync_MultipleEngines_ShouldDisposeInReverseOrder()
    {
        // Arrange
        var disposalOrder = new List<string>();
        var first = new TestDatabaseEngine("first", EngineModel.KeyValueStore, disposalOrder);
        var second = new TestDatabaseEngine("second", EngineModel.Document, disposalOrder);
        var embedded = EmbeddedDatabase.Create(options =>
        {
            options.Engines.Add(first);
            options.Engines.Add(second);
        });

        // Act
        await embedded.DisposeAsync();

        // Assert
        disposalOrder.ShouldBe(new[] { "second", "first" });
    }

    private sealed class TestDatabaseEngine : IDatabaseEngine
    {
        private readonly List<string>? _disposalOrder;

        public TestDatabaseEngine(string name, EngineModel model, List<string>? disposalOrder = null)
        {
            Name = name;
            Model = model;
            _disposalOrder = disposalOrder;
        }

        public string Name { get; }

        public EngineState State => EngineState.Idle;

        public EngineModel Model { get; }

        public IReadOnlyList<IDatabaseEngineWorker> Workers => Array.Empty<IDatabaseEngineWorker>();

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask<IDatabase> CreateDatabaseAsync(string name, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<IDatabase> OpenDatabaseAsync(string name, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask DropDatabaseAsync(string name, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<IDatabase> GetDatabasesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public bool TryGetDatabase(string name, out IDatabase database)
        {
            database = null!;
            return false;
        }

        public void Dispose()
        {
            _disposalOrder?.Add(Name);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
