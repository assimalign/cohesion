using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Tests;

/// <summary>
/// The root <see cref="DatabaseInstance"/> base (concrete-types plan rows 1 and 8, phase 3, #1259):
/// the field-backed name and engine, the session factory's guards, schema provisioning as the one
/// capability, and idempotent disposal.
/// </summary>
public class DatabaseInstanceTests
{
    [Fact(DisplayName = "Cohesion Test [Database] - Database: the name, engine and capability are the constructor's, and both references are required")]
    public void Constructor_Identity_ShouldBeFixedAndChecked()
    {
        // Arrange
        var engine = new TestEngine();

        // Act
        var database = new TestDatabase("appdb", engine);
        var provisioned = new TestDatabase("schemadb", engine, supportsSchemaProvisioning: true);

        // Assert
        database.Name.ShouldBe(new DatabaseName("appdb"));
        database.Engine.ShouldBeSameAs(engine);
        database.SupportsSchemaProvisioning.ShouldBeFalse();
        provisioned.SupportsSchemaProvisioning.ShouldBeTrue();
        Should.Throw<ArgumentException>(() => new TestDatabase(default, engine));
        Should.Throw<ArgumentNullException>(() => new TestDatabase("appdb", null!));
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Database: a session is created by the core while the database is open and the token is not canceled")]
    public async Task CreateSessionAsync_Guards_ShouldCheckBeforeTheCore()
    {
        // Arrange
        var database = new TestDatabase("appdb", new TestEngine());
        using var source = new CancellationTokenSource();
        source.Cancel();

        // Act
        var session = await database.CreateSessionAsync();
        await Should.ThrowAsync<OperationCanceledException>(async () => await database.CreateSessionAsync(source.Token));
        database.Dispose();
        await Should.ThrowAsync<ObjectDisposedException>(async () => await database.CreateSessionAsync());

        // Assert
        session.Database.ShouldBeSameAs(database);
        database.Sessions.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Database: a database without the capability refuses a schema before the core runs")]
    public async Task ApplySchemaAsync_Unsupported_ShouldThrowNotSupported()
    {
        // Arrange
        var database = new TestDatabase("appdb", new TestEngine());

        // Act
        var error = await Should.ThrowAsync<NotSupportedException>(async () => await database.ApplySchemaAsync(new TestSchema("appdb")));

        // Assert
        error.Message.ShouldBe("Database 'appdb' of the Sql model does not apply compiled schemas.");
        database.SchemaApplies.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Database: a provisioning database applies the schema through its core")]
    public async Task ApplySchemaAsync_Supported_ShouldCallTheCore()
    {
        // Arrange
        var database = new TestDatabase("appdb", new TestEngine(), supportsSchemaProvisioning: true);
        var schema = new TestSchema("appdb");

        // Act
        var result = await database.ApplySchemaAsync(schema);

        // Assert
        result.ToHash.ShouldBe(schema.Hash);
        result.WasAlreadyApplied.ShouldBeFalse();
        database.SchemaApplies.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Database: the default schema core refuses, so a capability without a core is never silent")]
    public async Task ApplySchemaAsync_CapabilityWithoutCore_ShouldThrowFromTheDefaultCore()
    {
        // Arrange
        var database = new UnprovisionedTestDatabase("appdb", new TestEngine());

        // Act / Assert
        database.SupportsSchemaProvisioning.ShouldBeTrue();
        await Should.ThrowAsync<NotSupportedException>(async () => await database.ApplySchemaAsync(new TestSchema("appdb")));
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Database: a schema needs a schema, an open database and an uncanceled token")]
    public async Task ApplySchemaAsync_Guards_ShouldCheckBeforeTheCore()
    {
        // Arrange
        var database = new TestDatabase("appdb", new TestEngine(), supportsSchemaProvisioning: true);
        using var source = new CancellationTokenSource();
        source.Cancel();

        // Act / Assert
        await Should.ThrowAsync<ArgumentNullException>(async () => await database.ApplySchemaAsync(null!));
        await Should.ThrowAsync<OperationCanceledException>(async () => await database.ApplySchemaAsync(new TestSchema("appdb"), source.Token));
        await database.DisposeAsync();
        await Should.ThrowAsync<ObjectDisposedException>(async () => await database.ApplySchemaAsync(new TestSchema("appdb")));
        database.SchemaApplies.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Database: disposal is idempotent across both disposal members")]
    public async Task Dispose_Repeated_ShouldReachTheLeafOnce()
    {
        // Arrange
        var engine = new TestEngine();
        var synchronous = new TestDatabase("first", engine);
        var asynchronous = new TestDatabase("second", engine);

        // Act
        synchronous.Dispose();
        synchronous.Dispose();
        await synchronous.DisposeAsync();
        await asynchronous.DisposeAsync();
        await asynchronous.DisposeAsync();
        asynchronous.Dispose();

        // Assert
        synchronous.DisposeCores.ShouldBe(1);
        synchronous.AsyncDisposeCores.ShouldBe(0);
        asynchronous.AsyncDisposeCores.ShouldBe(1);
        asynchronous.DisposeCores.ShouldBe(0);
        synchronous.Disposed.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Database: the interface view is the same database, engine and session type")]
    public async Task InterfaceBridge_IDatabase_ShouldExposeTheSameObjects()
    {
        // Arrange
        var engine = new TestEngine();
        IDatabase bridged = new TestDatabase("appdb", engine);

        // Act
        var session = await bridged.CreateSessionAsync();

        // Assert
        bridged.Name.ShouldBe(new DatabaseName("appdb"));
        bridged.Engine.ShouldBeSameAs(engine);
        session.ShouldBeOfType<TestSession>().Database.ShouldBeSameAs(bridged);
        bridged.ShouldNotBeAssignableTo<IDatabaseSchemaProvisioner>();
    }
}
