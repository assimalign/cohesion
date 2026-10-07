using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Tests;

/// <summary>
/// The typed engine lookup on the kept application context (concrete-types plan, row 4): the
/// context lists its engines as the root base, and <c>GetEngine&lt;TEngine&gt;</c> returns a
/// model's engine without a cast at the call site.
/// </summary>
public class DatabaseApplicationContextExtensionsTests
{
    [Fact(DisplayName = "Cohesion Test [Database] - Context: the typed lookup returns the named engine as its own type")]
    public async Task GetEngine_RegisteredType_ShouldReturnTheTypedEngine()
    {
        // Arrange
        await using var engine = new TestEngine("orders");
        await using var other = new OtherEngine("audit");
        IDatabaseApplicationContext context = new TestContext(engine, other);

        // Act
        TestEngine typed = context.GetEngine<TestEngine>("orders");
        DatabaseEngine untyped = context.GetEngine<DatabaseEngine>("audit");

        // Assert
        typed.ShouldBeSameAs(engine);
        untyped.ShouldBeSameAs(other);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Context: the typed lookup refuses an engine of another type, an unknown name and a blank one")]
    public async Task GetEngine_WrongTypeOrName_ShouldThrow()
    {
        // Arrange
        await using var engine = new TestEngine("orders");
        IDatabaseApplicationContext context = new TestContext(engine);

        // Act
        var wrongType = Should.Throw<InvalidOperationException>(() => context.GetEngine<OtherEngine>("orders"));

        // Assert
        wrongType.Message.ShouldContain("'orders'");
        wrongType.Message.ShouldContain(nameof(TestEngine));
        wrongType.Message.ShouldContain(nameof(OtherEngine));
        Should.Throw<KeyNotFoundException>(() => context.GetEngine<TestEngine>("missing"));
        Should.Throw<ArgumentException>(() => context.GetEngine<TestEngine>(" "));
        Should.Throw<ArgumentNullException>(() => ((IDatabaseApplicationContext)null!).GetEngine<TestEngine>("orders"));
    }

    // The kept context seam over a fixed list of engines, as the hosting layer's context lists them.
    private sealed class TestContext(params DatabaseEngine[] engines) : IDatabaseApplicationContext
    {
        public IReadOnlyList<DatabaseEngine> Engines { get; } = engines;

        public IReadOnlyList<DatabaseServer> Servers => [];

        public DatabaseEngine GetEngine(string name)
        {
            foreach (DatabaseEngine engine in Engines)
            {
                if (string.Equals(engine.Name, name, StringComparison.Ordinal))
                {
                    return engine;
                }
            }

            throw new KeyNotFoundException(name);
        }
    }

    // An engine of another type, with no databases.
    private sealed class OtherEngine(string name) : DatabaseEngine(name, EngineModel.Custom)
    {
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

        protected override ValueTask DisposeAsyncCore() => ValueTask.CompletedTask;
    }
}
