using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections.InMemory;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

using static KeyValueTestHarness;

/// <summary>
/// The key-value model's application-builder verbs: <c>AddKeyValueDatabase</c>
/// and <c>AddKeyValueServer</c> compose against the area root's builder seam
/// alone and return the model's own composition objects.
/// </summary>
public class KeyValueApplicationBuilderTests
{
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - AddKeyValueDatabase: Registers a configured engine on the root builder seam")]
    public async Task AddKeyValueDatabase_WithOptions_ShouldRegisterConfiguredEngine()
    {
        // Arrange
        var builder = new RecordingApplicationBuilder();

        // Act
        await using KeyValueDatabaseEngine engine = builder.AddKeyValueDatabase(options => options.EngineName = "kv-verb");

        // Assert: registered on the seam, configured, and operational (data machine).
        builder.Engines.ShouldHaveSingleItem().ShouldBeSameAs(engine);
        engine.Name.ShouldBe("kv-verb");
        engine.State.ShouldBe(EngineState.Running);
        engine.Model.ShouldBe(EngineModel.KeyValueStore);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - AddKeyValueServer: Registers a per-model server fronting the given engine")]
    public async Task AddKeyValueServer_WithEngineAndListener_ShouldRegisterServer()
    {
        // Arrange
        var builder = new RecordingApplicationBuilder();
        await using KeyValueDatabaseEngine engine = builder.AddKeyValueDatabase(options => options.EngineName = "kv-server-verb");
        await using var listener = new InMemoryConnectionListener();

        // Act
        await using KeyValueDatabaseServer server = builder.AddKeyValueServer(engine, options => options.Listener = listener);

        // Assert
        builder.Servers.ShouldHaveSingleItem().ShouldBeSameAs(server);
        server.Engine.ShouldBeSameAs(engine);
        server.Context.Engine.ShouldBeSameAs(engine);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - AddKeyValueDatabase: Defaults register an in-memory engine that serves key-value commands")]
    public async Task AddKeyValueDatabase_WithDefaults_ShouldServeCommandsEndToEnd()
    {
        // Arrange
        var builder = new RecordingApplicationBuilder();
        await using KeyValueDatabaseEngine engine = builder.AddKeyValueDatabase();

        // Act: the registered engine is immediately usable (in-memory default).
        var database = (IKeyValueDatabase)await engine.CreateDatabaseAsync("verbs", TestTimeout.Token());
        await using var session = await database.CreateSessionAsync();

        var put = await database.PutAsync(session, Bytes("k"), Bytes("v"), cancellationToken: TestTimeout.Token());

        // Assert
        put.Applied.ShouldBeTrue();
        Text((await database.GetAsync(session, Bytes("k"), TestTimeout.Token()))!.Value.Value).ShouldBe("v");
    }
}
