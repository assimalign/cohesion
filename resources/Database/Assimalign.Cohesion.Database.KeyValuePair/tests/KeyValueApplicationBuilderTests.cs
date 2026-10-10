using System;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections.InMemory;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

using static KeyValueTestHarness;

/// <summary>
/// The key-value model's application-builder verbs: <c>AddKeyValue</c>
/// and nested server factories compose against the area root's builder seam
/// alone and defer construction until Build.
/// </summary>
public class KeyValueApplicationBuilderTests
{
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - AddKeyValue: Registers a configured engine on the root builder seam")]
    public async Task AddKeyValue_WithOptions_ShouldRegisterConfiguredEngine()
    {
        // Arrange
        var builder = new RecordingApplicationBuilder();

        // Act
        builder.AddKeyValue("kv-verb", options => options.Options.RootPath = null);
        builder.Factories.ShouldHaveSingleItem();
        builder.Names.ShouldBe(["kv-verb"]);
        await using var engine = (KeyValueDatabaseEngine)builder.MaterializeEngine();

        // Assert: registered on the seam, configured, and operational (data machine).
        builder.Factories.ShouldHaveSingleItem();
        engine.Name.ShouldBe("kv-verb");
        engine.State.ShouldBe(EngineState.Running);
        engine.Model.ShouldBe(EngineModel.KeyValueStore);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - NestedServer: Registers a per-model server fronting the given engine")]
    public async Task NestedServer_WithEngineAndListener_ShouldRegisterServer()
    {
        var builder = new RecordingApplicationBuilder();
        bool serverCreated = false;
        builder.AddKeyValue("kv-server-verb", options =>
        {
            options.AddServer(engine =>
            {
                serverCreated = true;
                return KeyValueDatabaseServer.Create(engine,
                    new KeyValueDatabaseServerOptions { Listener = new InMemoryConnectionListener() });
            });
        });
        serverCreated.ShouldBeFalse();
        await using var engine = (KeyValueDatabaseEngine)builder.MaterializeEngine();
        serverCreated.ShouldBeTrue();
        var server = engine.Servers.ShouldHaveSingleItem().ShouldBeOfType<KeyValueDatabaseServer>();
        server.Engine.ShouldBeSameAs(engine);
        server.Engine.ShouldBeSameAs(engine);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - AddKeyValue: Defaults register an in-memory engine that serves key-value commands")]
    public async Task AddKeyValue_WithDefaults_ShouldServeCommandsEndToEnd()
    {
        // Arrange
        var builder = new RecordingApplicationBuilder();
        builder.AddKeyValue("defaults", _ => { });
        await using var engine = (KeyValueDatabaseEngine)builder.MaterializeEngine();

        // Act: the registered engine is immediately usable (in-memory default).
        var database = await engine.CreateDatabaseAsync("verbs", TestTimeout.Token());
        await using var session = await database.CreateSessionAsync();

        var put = await database.PutAsync(session, Bytes("k"), Bytes("v"), cancellationToken: TestTimeout.Token());

        // Assert
        put.Applied.ShouldBeTrue();
        Text((await database.GetAsync(session, Bytes("k"), TestTimeout.Token()))!.Value.Value).ShouldBe("v");
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - AddKeyValue: a declared database is open when the verb's factory returns, and the engine refuses to drop it")]
    public async Task AddKeyValue_WithDeclaredDatabase_ShouldOpenItInsideBuildAndRefuseItsDrop()
    {
        // Arrange
        var builder = new RecordingApplicationBuilder();
        builder.AddKeyValue("kv-declared", kv => kv.AddDatabase("sales"));

        // Act: the verb's factory is what application Build runs.
        await using var engine = (KeyValueDatabaseEngine)builder.MaterializeEngine();
        var refusal = await Should.ThrowAsync<DatabaseObjectLockedException>(async () => await engine.DropDatabaseAsync("SALES"));

        // Assert
        engine.TryGetDatabase("sales", out KeyValueDatabase? _).ShouldBeTrue();
        refusal.Operation.ShouldBe("DROP DATABASE");
        refusal.Message.ShouldStartWith("Key-value engine 'kv-declared' declares database 'sales'", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - AddKeyValue: a declared name the model refuses fails the verb's factory before the engine exists")]
    public void AddKeyValue_WithInvalidDeclaredName_ShouldFailBeforeTheEngineExists()
    {
        // Arrange
        var builder = new RecordingApplicationBuilder();
        KeyValueDatabaseEngine? product = null;
        builder.AddKeyValue("kv-invalid", kv =>
        {
            kv.AddWorker(engine => new RecordingWorker(product = engine));
            kv.AddDatabase("../escaped");
        });

        // Act
        var failure = Should.Throw<ArgumentException>(() => builder.MaterializeEngine());

        // Assert
        failure.ParamName.ShouldBe("name");
        product.ShouldBeNull();
    }
}
