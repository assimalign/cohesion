using System;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Blob;
using Assimalign.Cohesion.Database.Documents;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

/// <summary>
/// Engines are registrations in the application's services: the model packages' component-integration
/// verbs (<c>builder.Services.AddSql(...)</c>) register the same <see cref="DatabaseEngineRegistration"/>
/// the root verbs do, and Build refuses a duplicate name before any engine factory runs.
/// </summary>
public sealed class DatabaseEngineRegistrationTests
{
    private const string displayPrefix = "Cohesion Test [Database.Hosting] - Engine registrations: ";

    [Fact(DisplayName = displayPrefix + "The component-integration verbs register engines that Build creates in registration order")]
    public async Task Build_WithServicesVerbs_ShouldCreateEachEngineInRegistrationOrder()
    {
        // Arrange
        var builder = DatabaseApplication.CreateBuilder();
        builder.Services.AddSql("orders", sql => sql.AddDatabase("sales"));
        builder.Services.AddDocuments("catalog", documents => documents.AddDatabase("products"));
        builder.AddBlob("files", blob => blob.AddDatabase("assets"));

        // Act
        await using DatabaseApplication application = builder.Build();

        // Assert
        application.Context.Engines.Select(engine => engine.Name).ShouldBe(["orders", "catalog", "files"]);
        var orders = application.Context.GetEngine("orders").ShouldBeOfType<SqlDatabaseEngine>();
        orders.TryGetDatabase("sales", out SqlDatabase? _).ShouldBeTrue();
        application.Context.GetEngine("catalog").ShouldBeOfType<DocumentDatabaseEngine>();
        application.Context.GetEngine("files").ShouldBeOfType<BlobDatabaseEngine>();
        builder.Services.Container
            .Where(descriptor => descriptor.ServiceType == typeof(DatabaseEngineRegistration))
            .Select(descriptor => ((DatabaseEngineRegistration)descriptor.ImplementationInstance!).Name)
            .ShouldBe(["orders", "catalog", "files"]);
    }

    [Fact(DisplayName = displayPrefix + "A duplicate name registered through Services is refused at Build, before any engine factory runs")]
    public void Build_WithDuplicateNameThroughServices_ShouldRefuseBeforeAnyFactoryRuns()
    {
        // Arrange
        int factories = 0;
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine("first", _ => { factories++; return new RecordingEngine("first"); });
        builder.Services.AddSql("same", _ => factories++);
        builder.Services.AddSql("same", _ => factories++);

        // Act
        var failure = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        failure.Message.ShouldBe("Duplicate database engine name 'same'.");
        factories.ShouldBe(0);
        Should.Throw<InvalidOperationException>(() => builder.Services.AddSql("late", _ => { }));
    }

    [Fact(DisplayName = displayPrefix + "A builder verb reads Services and refuses a name a component integration already registered")]
    public void AddEngine_WhenServicesHoldTheName_ShouldRefuseAtRegistration()
    {
        // Arrange
        var builder = DatabaseApplication.CreateBuilder();
        builder.Services.AddSql("same", _ => { });

        // Act
        var verb = Should.Throw<InvalidOperationException>(() => builder.AddSql("same", _ => { }));
        var factory = Should.Throw<InvalidOperationException>(() => builder.AddEngine("same", _ => new RecordingEngine("same")));

        // Assert
        verb.Message.ShouldBe("Duplicate database engine name 'same'.");
        factory.Message.ShouldBe("Duplicate database engine name 'same'.");
    }

    [Fact(DisplayName = displayPrefix + "An engine registration added through a factory is refused, because its name is unknown until it runs")]
    public void Build_WithFactoryRegisteredRegistration_ShouldRefuse()
    {
        // Arrange
        int factories = 0;
        var builder = DatabaseApplication.CreateBuilder();
        builder.Services.AddSingleton<DatabaseEngineRegistration>(_ =>
        {
            factories++;
            return DatabaseEngineRegistration.Create(new RecordingEngine("late"));
        });

        // Act
        var failure = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        failure.Message.ShouldBe(ErrorMessages.EngineRegistrationNotInstance);
        factories.ShouldBe(0);
    }

    [Fact(DisplayName = displayPrefix + "DatabaseEngine is reserved: engines are registered by name")]
    public void Build_WithDirectEngineRegistration_ShouldRefuse()
    {
        // Arrange
        var engine = new RecordingEngine("direct");
        var builder = DatabaseApplication.CreateBuilder();
        builder.Services.AddSingleton<DatabaseEngine>(engine);

        // Act
        var failure = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        failure.Message.ShouldStartWith("DatabaseEngine is reserved for the application's own engine registrations", Case.Sensitive);
        engine.DisposeCount.ShouldBe(0);
    }

    [Fact(DisplayName = displayPrefix + "Lifecycle services resolve the engines from the container, owned and borrowed alike")]
    public async Task Build_WithServiceResolvingEngines_ShouldSeeEveryEngine()
    {
        // Arrange
        var borrowed = new RecordingEngine("borrowed");
        var owned = new RecordingEngine("owned");
        DatabaseEngine[]? seen = null;
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(borrowed);
        builder.AddEngine("owned", _ => owned);
        builder.Services.AddSingleton<IHostService>(services =>
        {
            seen = [.. services.GetServices<DatabaseEngine>()];
            return new RecordingService([], "probe");
        });

        // Act
        DatabaseApplication application = builder.Build();
        await ((IAsyncDisposable)application).DisposeAsync();

        // Assert
        seen.ShouldNotBeNull().ShouldBe([borrowed, owned]);
        owned.DisposeCount.ShouldBe(1);
        borrowed.DisposeCount.ShouldBe(0);
    }

    [Fact(DisplayName = displayPrefix + "A registration names a borrowed engine or defers an owned one")]
    public void Create_ShouldCarryTheNameAndOwnership()
    {
        // Arrange
        var borrowed = new RecordingEngine("borrowed");
        var created = new RecordingEngine("owned");

        // Act
        var lent = DatabaseEngineRegistration.Create(borrowed);
        var deferred = DatabaseEngineRegistration.Create("owned", _ => created);

        // Assert
        lent.Name.ShouldBe("borrowed");
        lent.IsOwned.ShouldBeFalse();
        lent.Engine.ShouldBeSameAs(borrowed);
        deferred.Name.ShouldBe("owned");
        deferred.IsOwned.ShouldBeTrue();
        deferred.Engine.ShouldBeNull();
        Should.Throw<ArgumentException>(() => DatabaseEngineRegistration.Create(" ", _ => created));
        Should.Throw<ArgumentNullException>(() => DatabaseEngineRegistration.Create(null!));
    }
}
