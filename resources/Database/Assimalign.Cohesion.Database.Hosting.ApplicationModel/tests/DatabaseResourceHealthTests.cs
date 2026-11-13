using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Hosting;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Hosting.ApplicationModel.Tests;

[Collection(DatabaseHealthCollection.Name)]
public sealed class DatabaseResourceHealthTests
{
    private const string displayPrefix = "Cohesion Test [Database.Hosting.ApplicationModel] - Health: ";

    [Fact(DisplayName = displayPrefix + "Each engine and database contributes independently and discovery follows opens and closes")]
    public async Task HealthContributors_WithChangingInventory_ShouldObserveEachComponent()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.CreateBuilder("sql/one").Build();
        await engine.CreateDatabaseAsync("Orders", guard.Token);
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(engine);
        Register(builder);
        await using var application = builder.Build(guard.Token);
        IApplicationInstance instance = Instance(application);

        // Act
        var contributors = instance.HealthContributors;
        IApplicationInstanceHealthContributor orders = contributors.Single(value => value.Name == "database/sql%2Fone/orders");
        var healthy = await orders.CheckAsync(guard.Token);
        await engine.CreateDatabaseAsync("later", guard.Token);
        var withLater = instance.HealthContributors;
        await engine.DropDatabaseAsync("orders", guard.Token);
        var closed = await orders.CheckAsync(guard.Token);

        // Assert
        contributors.Select(value => value.Name).ShouldBe(new[] { "engine/sql%2Fone", "database/sql%2Fone/orders", "database.accepting" });
        healthy.Status.ShouldBe(ApplicationInstanceHealthStatus.Healthy);
        healthy.Data.ShouldNotBeNull()["database"].ShouldBe("Orders");
        withLater.Count.ShouldBe(4);
        instance.HealthContributors.Select(value => value.Name).ShouldNotContain(orders.Name);
        closed.Status.ShouldBe(ApplicationInstanceHealthStatus.Unhealthy);
        (await contributors.Single(value => value.Name == "engine/sql%2Fone").CheckAsync(guard.Token)).Status.ShouldBe(ApplicationInstanceHealthStatus.Healthy);
        await engine.DisposeAsync();
        (await contributors.Single(value => value.Name == "engine/sql%2Fone").CheckAsync(guard.Token)).Status.ShouldBe(ApplicationInstanceHealthStatus.Unhealthy);
    }

    [Fact(DisplayName = displayPrefix + "Readiness is unavailable before accept and after stop")]
    public async Task CheckAsync_AcrossHostLifecycle_ShouldFollowAcceptingGate()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var resource = RunningResource.Build(new TestGatewayClient());
        IApplicationInstanceHealthContributor readiness = Instance(resource.Application).HealthContributors.Single(value => value.Name == "database.accepting");

        // Act
        var before = await readiness.CheckAsync(guard.Token);
        await resource.StartAsync(guard.Token);
        var accepting = await readiness.CheckAsync(guard.Token);
        await resource.StopAsync(guard.Token);
        var stopped = await readiness.CheckAsync(guard.Token);

        // Assert
        before.Status.ShouldBe(ApplicationInstanceHealthStatus.Unhealthy);
        accepting.Status.ShouldBe(ApplicationInstanceHealthStatus.Healthy);
        stopped.Status.ShouldBe(ApplicationInstanceHealthStatus.Unhealthy);
        (await TestPorts.AcceptsAsync(resource.Port, guard.Token)).ShouldBeFalse();
    }

    [Theory(DisplayName = displayPrefix + "A held schema keeps its database available and contributes its code and refused steps")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckAsync_WithHeldSchema_ShouldDegradeOnlyTheDatabase(bool previouslyApplied)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var directory = new TemporaryDirectory();
        var first = Schema(false);
        var second = Schema(true);
        var seed = SqlDatabaseEngine.CreateBuilder("sql");
        seed.Options.RootPath = directory.Path;
        await using (var engine = await seed.BuildAsync(guard.Token))
        {
            var database = await engine.CreateDatabaseAsync("held", guard.Token);
            if (previouslyApplied) { await database.ApplySchemaAsync(first.Compile(), guard.Token); }
            await database.ApplySchemaAsync(second.Compile(), guard.Token);
        }
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddSql("sql", sql =>
        {
            sql.Options.RootPath = directory.Path;
            sql.AddDatabase(first);
            sql.AddDatabase("ordinary");
        });
        Register(builder);
        using var events = new HealthEventRecorder();
        await using var application = builder.Build(guard.Token);
        int transitionsAtBuild = events.HealthTransitions;
        var contributors = Instance(application).HealthContributors;

        // Act
        var held = await contributors.Single(value => value.Name == "database/sql/held").CheckAsync(guard.Token);

        // Assert
        transitionsAtBuild.ShouldBe(0);
        held.Status.ShouldBe(ApplicationInstanceHealthStatus.Degraded);
        held.Data.ShouldNotBeNull()["failureCode"].ShouldBe(previouslyApplied ? "CS03405" : "CS03105");
        held.Data["detail"].ShouldContain("DropColumn 'items.Added'", Case.Sensitive);
        held.Description.ShouldNotBeNull().ShouldContain("DropColumn 'items.Added'", Case.Sensitive);
        held.Data["detail"].ShouldContain(first.Compile().Hash, Case.Sensitive);
        held.Data["detail"].ShouldContain(second.Compile().Hash, Case.Sensitive);
        (await contributors.Single(value => value.Name == "database/sql/ordinary").CheckAsync(guard.Token)).Status.ShouldBe(ApplicationInstanceHealthStatus.Healthy);
        (await contributors.Single(value => value.Name == "engine/sql").CheckAsync(guard.Token)).Status.ShouldBe(ApplicationInstanceHealthStatus.Healthy);
    }

    [Fact(DisplayName = displayPrefix + "Cancelled contribution evaluation preserves cancellation")]
    public async Task CheckAsync_WithCancelledToken_ShouldCancel()
    {
        // Arrange
        await using var resource = RunningResource.Build(new TestGatewayClient());
        var contributor = Instance(resource.Application).HealthContributors.First();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act & Assert
        await Should.ThrowAsync<OperationCanceledException>(() => contributor.CheckAsync(cancellation.Token).AsTask());
    }

    internal static void Register(DatabaseApplicationBuilder builder)
        => builder.Services.AddResource("health-db", options =>
        {
            options.Platform = "test";
            options.UseGatewayClient(new TestGatewayClient());
        });

    internal static IApplicationInstance Instance(DatabaseApplication application)
        => application.Context.HostedServices.OfType<IApplicationInstanceService>().Single().ControlPlane.Instance;

    private static SqlSchema Schema(bool extra) => SqlSchema.Create("held", schema =>
        schema.Table<HeldRow>("items", table =>
        {
            table.Key(row => row.Id);
            if (extra) { table.Column(row => row.Added); }
        }));

    private sealed record HeldRow(int Id, int? Added);
}
