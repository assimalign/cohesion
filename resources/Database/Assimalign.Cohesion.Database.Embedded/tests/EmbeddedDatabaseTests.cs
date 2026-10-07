using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;

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

    // An engine of the root base that implements only the protected cores (database-area.md,
    // "Test doubles"): it has no databases, and its disposal core records the disposal order.
    private sealed class TestDatabaseEngine : DatabaseEngine
    {
        private readonly List<string>? _disposalOrder;

        public TestDatabaseEngine(string name, EngineModel model, List<string>? disposalOrder = null)
            : base(name, model)
        {
            _disposalOrder = disposalOrder;
        }

        public override IReadOnlyList<DatabaseName> OfflineDatabases => [];

        protected override ValueTask<DatabaseInstance> CreateDatabaseCoreAsync(DatabaseName name, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        protected override ValueTask<DatabaseInstance> OpenDatabaseCoreAsync(DatabaseName name, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        protected override ValueTask DropDatabaseCoreAsync(DatabaseName name, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        protected override IAsyncEnumerable<DatabaseInstance> GetDatabasesCore(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        protected override bool TryGetDatabaseCore(DatabaseName name, [MaybeNullWhen(false)] out DatabaseInstance database)
        {
            database = null;
            return false;
        }

        protected override void ForgetClosedDatabaseCore(DatabaseInstance database)
        {
        }

        protected override StorageOfflineException? GetOfflineErrorCore(DatabaseName name) => null;

        protected override bool TakeDatabaseOfflineCore(DatabaseName name, StorageOfflineCause cause, string reason, Exception failure)
            => false;

        protected override ValueTask DisposeAsyncCore()
        {
            _disposalOrder?.Add(Name);
            return ValueTask.CompletedTask;
        }
    }
}
