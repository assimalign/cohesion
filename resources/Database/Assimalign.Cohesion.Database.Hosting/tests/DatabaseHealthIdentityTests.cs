using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Hosting.Internal;
using Assimalign.Cohesion.Web.Health;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

[Collection(DatabaseProvisioningHealthCollection.Name)]
public sealed class DatabaseHealthIdentityTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - Out of memory escapes discovery and both component evaluation forms (#1790)")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task GetHealthAsync_WithOutOfMemoryInventory_ShouldPropagate(int path)
    {
        // Arrange
        var failure = new OutOfMemoryException();
        await using var engine = new RecordingEngine("memory") { InventoryException = failure };
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(engine);
        await using var application = builder.Build();

        // Act / Assert
        if (path == 0) { Should.Throw<OutOfMemoryException>(() => application.Context.HealthComponentNames).ShouldBeSameAs(failure); }
        else
        {
            (await Should.ThrowAsync<OutOfMemoryException>(() => path == 1
                ? application.Context.GetHealthAsync("engine/memory", CancellationToken.None).AsTask()
                : (Task)application.Context.GetHealthAsync(CancellationToken.None).AsTask())).ShouldBeSameAs(failure);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Discovery alone evaluates no healthy engine (#1790)")]
    public async Task HealthComponentNames_WithHealthyEngine_ShouldReadNoOfflineState()
    {
        // Arrange
        await using var engine = new RecordingEngine("healthy");
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(engine);
        await using var application = builder.Build();

        // Act
        var names = application.Context.HealthComponentNames;

        // Assert
        names.ShouldBe(["engine/healthy", "database.accepting"]);
        engine.OfflineReads.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - Cancellation during inventory escapes both component evaluation forms (#1790)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetHealthAsync_CallerCancelsDuringInventory_ShouldCancel(bool single)
    {
        // Arrange
        using var caller = new CancellationTokenSource();
        await using var engine = new RecordingEngine("custom", EngineModel.Custom);
        await using var database = new IdentityDatabase(engine, "tenant 1");
        engine.OpenInventory = () => { caller.Cancel(); return [database]; };
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(engine);
        await using var application = builder.Build();

        // Act / Assert
        var failure = await Should.ThrowAsync<OperationCanceledException>(() => single
            ? application.Context.GetHealthAsync("database/custom/tenant%201", caller.Token).AsTask()
            : (Task)application.Context.GetHealthAsync(caller.Token).AsTask());
        failure.CancellationToken.ShouldBe(caller.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Escaped custom database names agree with the HTTP aggregate (#1790)")]
    public async Task GetHealthAsync_WithEscapedDatabaseName_ShouldMatchAggregate()
    {
        // Arrange
        await using var engine = new RecordingEngine("custom", EngineModel.Custom);
        await using var database = new IdentityDatabase(engine, "tenant 1");
        engine.Instances = [database];
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(engine);
        await using var application = builder.Build();

        // Act
        var name = application.Context.HealthComponentNames.Single(value => value.StartsWith("database/", StringComparison.Ordinal));
        var single = await application.Context.GetHealthAsync(name, CancellationToken.None);
        var aggregate = await application.Context.CheckAsync(CancellationToken.None);

        // Assert
        name.ShouldBe("database/custom/tenant%201");
        single.Status.ShouldBe(DatabaseHealthStatus.Healthy);
        ((int)single.Status).ShouldBe((int)aggregate.Status);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - ASCII storage folding preserves distinct Kelvin and K database names (#1790)")]
    public async Task GetHealthAsync_WithKelvinAndAsciiNames_ShouldKeepDistinctComponents()
    {
        // Arrange
        await using var engine = new RecordingEngine("custom", EngineModel.Custom);
        await using var ascii = new IdentityDatabase(engine, "K");
        await using var kelvin = new IdentityDatabase(engine, "\u212A");
        engine.Instances = [ascii, kelvin];
        engine.Offline = ["k"];
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(engine);
        await using var application = builder.Build();

        // Act
        var names = application.Context.HealthComponentNames;
        var observations = await application.Context.GetHealthAsync(CancellationToken.None);

        // Assert
        ((DatabaseName)"K").ShouldNotBe((DatabaseName)"\u212A");
        names.ShouldContain("database/custom/k");
        names.ShouldContain("database/custom/%E2%84%AA");
        observations.Single(value => value.Name == "database/custom/k").Status.ShouldBe(DatabaseHealthStatus.Unhealthy);
        observations.Single(value => value.Name == "database/custom/%E2%84%AA").Status.ShouldBe(DatabaseHealthStatus.Healthy);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Discovery and Complete sort escaped component names identically (#1790)")]
    public async Task HealthComponentNames_WithEscapedNames_ShouldMatchCompleteOrder()
    {
        // Arrange
        await using var engine = new RecordingEngine("custom", EngineModel.Custom);
        await using var ascii = new IdentityDatabase(engine, "z");
        await using var escaped = new IdentityDatabase(engine, "\u212A");
        engine.Instances = [ascii, escaped];
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(engine);
        await using var application = builder.Build();

        // Act
        var names = application.Context.HealthComponentNames;
        var observations = await application.Context.GetHealthAsync(CancellationToken.None);

        // Assert
        names.ShouldBe(["engine/custom", "database/custom/%E2%84%AA", "database/custom/z", "database.accepting"]);
        observations.Select(value => value.Name).ShouldBe(names);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Open, offline and pending inventories share one escaped component spelling (#1790)")]
    public async Task GetHealthAsync_WithInventoryCaseVariants_ShouldKeepComponentIdentity()
    {
        // Arrange
        await using var engine = new RecordingEngine("custom", EngineModel.Custom);
        await using var database = new IdentityDatabase(engine, "Tenant 1");
        engine.Instances = [database];
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(engine);
        await using var application = builder.Build();

        // Act
        var openNames = application.Context.HealthComponentNames;
        var open = await application.Context.GetHealthAsync("database/custom/tenant%201", CancellationToken.None);
        engine.Offline = ["TENANT 1"];
        var offline = await application.Context.GetHealthAsync(CancellationToken.None);
        engine.Instances = [];
        engine.Offline = [];
        PendingReopens(application.Context.ReopenService.ShouldNotBeNull()) =
            [new ReopenState(engine, "tenant 1", null, 1, new InvalidOperationException(), TimeSpan.Zero)];
        var pendingNames = application.Context.HealthComponentNames;
        var pending = await application.Context.GetHealthAsync("database/custom/tenant%201", CancellationToken.None);

        // Assert
        open.Status.ShouldBe(DatabaseHealthStatus.Healthy);
        open.Name.ShouldBe("database/custom/tenant%201");
        offline.Single(value => value.Name == open.Name).Status.ShouldBe(DatabaseHealthStatus.Unhealthy);
        offline.Select(value => value.Name).ShouldBe(openNames);
        pendingNames.ShouldBe(openNames);
        pending.Name.ShouldBe(open.Name);
        pending.Status.ShouldBe(DatabaseHealthStatus.Unhealthy);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_pendingReopens")]
    private static extern ref IReadOnlyList<ReopenState> PendingReopens(DatabaseReopenService service);

    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - Invalid UTF-16 engine names fail all registration paths (#1790)")]
    [InlineData(0xD800)]
    [InlineData(0xD801)]
    [InlineData(0xDC00)]
    public async Task AddEngine_WithUnpairedSurrogate_ShouldRejectBeforeFactory(int surrogate)
    {
        // Arrange
        string name = "engine" + (char)surrogate;
        await using var engine = new RecordingEngine(name, EngineModel.Custom);
        var builder = DatabaseApplication.CreateBuilder();
        bool invoked = false;

        // Act / Assert
        Should.Throw<ArgumentException>(() => builder.AddEngine(engine));
        Should.Throw<ArgumentException>(() => builder.AddEngine(name, _ => { invoked = true; return engine; }));
        Should.Throw<ArgumentException>(() => ((IDatabaseApplicationBuilder)builder).AddEngine(name, _ => { invoked = true; return engine; }));
        invoked.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Engine names retain valid surrogate pairs and ordinal identity (#1790)")]
    public async Task AddEngine_WithValidSurrogatePair_ShouldKeepEscapedIdentity()
    {
        // Arrange
        await using var engine = new RecordingEngine("engine\U0001F600", EngineModel.Custom);
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(engine);
        await using var application = builder.Build();

        // Act / Assert
        application.Context.HealthComponentNames.ShouldContain("engine/engine%F0%9F%98%80");
    }

    private sealed class IdentityDatabase : DatabaseInstance
    {
        internal IdentityDatabase(DatabaseEngine engine, string name) : base(name, engine) { }
        protected override ValueTask<DatabaseSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
        protected override void DisposeCore() { }
        protected override ValueTask DisposeAsyncCore() => ValueTask.CompletedTask;
    }
}
