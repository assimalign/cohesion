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

    [Fact(DisplayName = "Cohesion Test [Database] - Context: the typed lookup refuses an engine of another type, naming both types")]
    public async Task GetEngine_WrongType_ShouldThrowInvalidOperation()
    {
        // Arrange
        await using var engine = new TestEngine("orders");
        IDatabaseApplicationContext context = new TestContext(engine);

        // Act
        var error = Should.Throw<InvalidOperationException>(() => context.GetEngine<OtherEngine>("orders"));

        // Assert
        error.Message.ShouldContain("'orders'", Case.Sensitive);
        error.Message.ShouldContain(nameof(TestEngine), Case.Sensitive);
        error.Message.ShouldContain(nameof(OtherEngine), Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Context: the typed lookup passes on the context's refusal of an unknown name")]
    public async Task GetEngine_UnknownName_ShouldThrowKeyNotFound()
    {
        // Arrange
        await using var engine = new TestEngine("orders");
        IDatabaseApplicationContext context = new TestContext(engine);

        // Act
        var error = Should.Throw<KeyNotFoundException>(() => context.GetEngine<TestEngine>("missing"));

        // Assert
        error.Message.ShouldContain("missing", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Database] - Context: the typed lookup refuses an empty or white-space name")]
    [InlineData("")]
    [InlineData(" ")]
    public async Task GetEngine_BlankName_ShouldThrowArgument(string name)
    {
        // Arrange
        await using var engine = new TestEngine("orders");
        IDatabaseApplicationContext context = new TestContext(engine);

        // Act
        var error = Should.Throw<ArgumentException>(() => context.GetEngine<TestEngine>(name));

        // Assert
        error.ParamName.ShouldBe("name");
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Context: the typed lookup refuses a null name")]
    public async Task GetEngine_NullName_ShouldThrowArgumentNull()
    {
        // Arrange
        await using var engine = new TestEngine("orders");
        IDatabaseApplicationContext context = new TestContext(engine);

        // Act
        var error = Should.Throw<ArgumentNullException>(() => context.GetEngine<TestEngine>(null!));

        // Assert
        error.ParamName.ShouldBe("name");
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Context: the typed lookup refuses a null context")]
    public void GetEngine_NullContext_ShouldThrowArgumentNull()
    {
        // Arrange
        IDatabaseApplicationContext context = null!;

        // Act
        var error = Should.Throw<ArgumentNullException>(() => context.GetEngine<TestEngine>("orders"));

        // Assert
        error.ParamName.ShouldBe("context");
    }

    // The kept context seam over a fixed list of engines, as the hosting layer's context lists them.
    private sealed class TestContext : IDatabaseApplicationContext
    {
        private readonly IReadOnlyList<DatabaseEngine> _engines;

        public TestContext(params DatabaseEngine[] engines)
        {
            _engines = engines;
        }

        public IReadOnlyList<DatabaseEngine> Engines => _engines;

        public IReadOnlyList<DatabaseServer> Servers => [];

        public DatabaseEngine GetEngine(string name)
        {
            foreach (DatabaseEngine engine in _engines)
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
    private sealed class OtherEngine : DatabaseEngine
    {
        public OtherEngine(string name)
            : base(name, EngineModel.Custom)
        {
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

        protected override ValueTask DisposeAsyncCore() => ValueTask.CompletedTask;
    }
}
