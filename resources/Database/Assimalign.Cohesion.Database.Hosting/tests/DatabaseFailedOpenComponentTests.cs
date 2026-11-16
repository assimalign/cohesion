using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Hosting.Internal;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Web.Health;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

[Collection(DatabaseProvisioningHealthCollection.Name)]
public sealed class DatabaseFailedOpenComponentTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - Failed-open health and discovery never take the engine completion gate (#1790)")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task GetHealthAsync_WithCompletionGateHeld_ShouldReadSnapshot(int path)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = SqlDatabaseEngine.CreateBuilder("snapshot").Build();
        await (await engine.CreateDatabaseAsync("stored", guard.Token)).DisposeAsync();
        SetBeforeProvisioning(engine, _ => throw new InvalidOperationException("SECRET-sentinel"));
        await Should.ThrowAsync<InvalidOperationException>(async () => await engine.OpenDatabaseAsync("stored", guard.Token));
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(engine);
        await using var application = builder.Build(guard.Token);
        var holder = Task.Factory.StartNew(() =>
        {
            lock (CompletionGate(engine)) { entered.SetResult(); release.Wait(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await entered.Task.WaitAsync(guard.Token);

        // Act
        var reading = Task.Run(async () => path == 0 ? application.Context.HealthComponentNames.Count
            : path == 1 ? (await application.Context.GetHealthAsync(guard.Token)).Count
            : (await application.Context.GetHealthAsync("database/snapshot/stored", guard.Token)).Data.Count, guard.Token);
        try { await reading.WaitAsync(guard.Token); }
        finally { release.Set(); await holder; }

        // Assert
        (await reading).ShouldBe(path == 2 ? 6 : 3);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Pending and open precedence keep one ordered component and redact failure text (#1790)")]
    public async Task GetHealthAsync_WithMixedMembership_ShouldPreferOpenThenPendingThenRecord()
    {
        // Arrange: actual SQL stored opens fail after storage recovery, with sentinel text.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.CreateBuilder("mixed/engine").Build();
        foreach (string name in new[] { "Zulu", "middle", "alpha" })
        { await (await engine.CreateDatabaseAsync(name, guard.Token)).DisposeAsync(); }
        SetBeforeProvisioning(engine, _ => throw new DatabaseException("SECRET-sentinel",
            [new Diagnostic { Code = "CS06041", Message = "SECRET-sentinel", Severity = DiagnosticSeverity.Error }]));
        foreach (string name in new[] { "Zulu", "middle", "alpha" })
        { await Should.ThrowAsync<DatabaseException>(async () => await engine.OpenDatabaseAsync(name, guard.Token)); }
        engine.FailedOpens.Count.ShouldBe(3);
        SetBeforeProvisioning(engine, null);
        await engine.OpenDatabaseAsync("Zulu", guard.Token);
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(engine);
        await using var application = builder.Build(guard.Token);
        var service = application.Context.ReopenService.ShouldNotBeNull();
        Pending(service) = Array.AsReadOnly(new[]
        {
            new ReopenState(engine, "middle", StorageOfflineCause.CheckpointFailures, 7, new InvalidOperationException("SECRET-pending"), TimeSpan.Zero),
            new ReopenState(engine, "Zulu", StorageOfflineCause.CheckpointFailures, 9, new InvalidOperationException("SECRET-pending"), TimeSpan.Zero),
        });

        // Act
        var names = application.Context.HealthComponentNames;
        var components = await application.Context.GetHealthAsync(guard.Token);
        var single = await application.Context.GetHealthAsync("database/mixed%2Fengine/middle", guard.Token);
        var aggregate = await application.Context.CheckAsync(guard.Token);

        // Assert
        names.ShouldBe(["engine/mixed%2Fengine", "database/mixed%2Fengine/alpha", "database/mixed%2Fengine/middle", "database/mixed%2Fengine/zulu", "database.accepting"]);
        components.Select(component => component.Name).ShouldBe(names);
        var pending = components.Single(component => component.Name.EndsWith("/middle", StringComparison.Ordinal));
        pending.Status.ShouldBe(DatabaseHealthStatus.Unhealthy);
        pending.Data["reopenAttempts"].ShouldBe("7");
        pending.Data["reopenFault"].ShouldBe(nameof(InvalidOperationException));
        pending.Data.ContainsKey("failedAt").ShouldBeFalse();
        single.Data.ShouldBe(pending.Data);
        components.Single(component => component.Name.EndsWith("/zulu", StringComparison.Ordinal)).Status.ShouldBe(DatabaseHealthStatus.Healthy);
        var recorded = components.Single(component => component.Name.EndsWith("/alpha", StringComparison.Ordinal));
        recorded.Data["failureCode"].ShouldBe("CS06041");
        recorded.Data["reopenFault"].ShouldBe(nameof(DatabaseException));
        recorded.Description.ShouldNotBeNull().ShouldStartWith("Open failed:");
        aggregate.Status.ShouldBe(HealthStatus.Unhealthy);
        var data = aggregate.Data.ShouldNotBeNull();
        data["engine.0.offlineDatabaseCount"].ShouldBe(1);
        data["engine.0.offlineDatabases"].ShouldBe("middle");
        data["engine.0.failedOpenDatabaseCount"].ShouldBe(1);
        data["engine.0.failedOpenDatabases"].ShouldBe("alpha");
        data["offlineDatabaseCount"].ShouldBe(1);
        data["failedOpenDatabaseCount"].ShouldBe(1);
        aggregate.Description.ShouldNotBeNull().ShouldContain("Each needs repair and an explicit open");
        foreach (var component in components)
        {
            component.Name.ShouldNotContain("SECRET", Case.Sensitive);
            component.Description.ShouldNotBeNull().ShouldNotContain("SECRET", Case.Sensitive);
            foreach (var detail in component.Data) { (detail.Key + detail.Value).ShouldNotContain("SECRET", Case.Sensitive); }
        }
        aggregate.Description.ShouldNotBeNull().ShouldNotContain("SECRET", Case.Sensitive);
        foreach (var detail in aggregate.Data.ShouldNotBeNull())
        { (detail.Key + detail.Value).ShouldNotContain("SECRET", Case.Sensitive); }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Failed manual reopen cannot improve an offline database's health (#1790)")]
    public async Task GetHealthAsync_WithFailedManualReopen_ShouldRemainUnhealthy()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.CreateBuilder("manual").Build();
        var database = await engine.CreateDatabaseAsync("offline", guard.Token);
        var builder = new DatabaseApplicationBuilder(new DatabaseApplicationOptions { ReopenOfflineDatabases = false });
        builder.AddEngine(engine);
        await using var application = builder.Build(guard.Token);
        DataStorage(database).TakeOffline(StorageOfflineCause.CheckpointFailures, "SECRET-sentinel", new InvalidOperationException("SECRET-sentinel"));
        (await application.Context.GetHealthAsync("database/manual/offline", guard.Token)).Status.ShouldBe(DatabaseHealthStatus.Unhealthy);
        SetBeforeProvisioning(engine, _ => throw new DatabaseException("SECRET-sentinel",
            [new Diagnostic { Code = "CS06041", Message = "SECRET-sentinel", Severity = DiagnosticSeverity.Error }]));

        // Act
        await Should.ThrowAsync<DatabaseException>(async () => await engine.OpenDatabaseAsync("offline", guard.Token));
        var failed = await application.Context.GetHealthAsync("database/manual/offline", guard.Token);
        var aggregate = await application.Context.CheckAsync(guard.Token);

        // Assert
        engine.OpenDatabases.ShouldBeEmpty();
        engine.OfflineDatabases.ShouldBeEmpty();
        application.Context.HealthComponentNames.ShouldContain(failed.Name);
        failed.Status.ShouldBe(DatabaseHealthStatus.Unhealthy);
        failed.Data["reason"].ShouldBe("Open failed");
        aggregate.Status.ShouldBe(HealthStatus.Unhealthy);
        aggregate.Data.ShouldNotBeNull()["engine.0.offlineDatabaseCount"].ShouldBe(0);
        aggregate.Data["engine.0.failedOpenDatabaseCount"].ShouldBe(1);
        aggregate.Data["engine.0.failedOpenDatabases"].ShouldBe("offline");
        aggregate.Description.ShouldNotBeNull().ShouldNotContain("The application reopens each");
        aggregate.Description.ShouldNotContain("Offline databases:");
        SetBeforeProvisioning(engine, null);
        await engine.OpenDatabaseAsync("offline", guard.Token);
        engine.FailedOpens.ShouldBeEmpty();
        (await application.Context.GetHealthAsync(failed.Name, guard.Token)).Status.ShouldBe(DatabaseHealthStatus.Healthy);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - An online instance outranks a stale pending reopen in aggregate and components (#1790)")]
    public async Task GetHealthAsync_WithOnlineInstanceAndStalePending_ShouldBeHealthy()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.CreateBuilder("online").Build();
        await engine.CreateDatabaseAsync("stored", guard.Token);
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(engine);
        await using var application = builder.Build(guard.Token);
        Pending(application.Context.ReopenService.ShouldNotBeNull()) = Array.AsReadOnly(new[]
        { new ReopenState(engine, "stored", StorageOfflineCause.CheckpointFailures, 7, new InvalidOperationException("stale"), TimeSpan.Zero) });

        // Act
        var aggregate = await application.Context.CheckAsync(guard.Token);
        var database = await application.Context.GetHealthAsync("database/online/stored", guard.Token);

        // Assert
        aggregate.Status.ShouldBe(HealthStatus.Healthy);
        aggregate.Data.ShouldNotBeNull()["offlineDatabaseCount"].ShouldBe(0);
        database.Status.ShouldBe(DatabaseHealthStatus.Healthy);
        database.Data.ContainsKey("reopenAttempts").ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - Failed opens with no code have their own repair clause and no empty detail slot (#1790)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetHealthAsync_WithUncodedRecord_ShouldRequireExplicitOpen(bool automaticReopen)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.CreateBuilder("uncoded").Build();
        await (await engine.CreateDatabaseAsync("stored", guard.Token)).DisposeAsync();
        SetBeforeProvisioning(engine, _ => throw new InvalidOperationException("SECRET-sentinel"));
        await Should.ThrowAsync<InvalidOperationException>(async () => await engine.OpenDatabaseAsync("stored", guard.Token));
        var builder = new DatabaseApplicationBuilder(new DatabaseApplicationOptions { ReopenOfflineDatabases = automaticReopen });
        builder.AddEngine(engine);
        await using var application = builder.Build(guard.Token);

        // Act
        var aggregate = await application.Context.CheckAsync(guard.Token);
        var database = await application.Context.GetHealthAsync("database/uncoded/stored", guard.Token);

        // Assert
        aggregate.Description.ShouldNotBeNull().ShouldContain("open failed; InvalidOperationException;");
        aggregate.Description.ShouldNotContain("; ;");
        aggregate.Description.ShouldNotContain("The application reopens each");
        aggregate.Description.ShouldContain("Each needs repair and an explicit open");
        database.Description.ShouldNotBeNull().ShouldStartWith("Open failed:");
        database.Data.ContainsKey("failureCode").ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - An unavailable engine does not hide a failed open's repair clause (#1790)")]
    public async Task GetHealthAsync_WithUnavailableEngineAndRecord_ShouldKeepRepairClause()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.CreateBuilder("recorded").Build();
        await using var unavailable = SqlDatabaseEngine.CreateBuilder("unavailable").Build();
        await (await engine.CreateDatabaseAsync("stored", guard.Token)).DisposeAsync();
        SetBeforeProvisioning(engine, _ => throw new InvalidOperationException("SECRET-sentinel"));
        await Should.ThrowAsync<InvalidOperationException>(async () => await engine.OpenDatabaseAsync("stored", guard.Token));
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(engine);
        builder.AddEngine(unavailable);
        await using var application = builder.Build(guard.Token);
        await unavailable.DisposeAsync();

        // Act
        var aggregate = await application.Context.CheckAsync(guard.Token);

        // Assert
        aggregate.Status.ShouldBe(HealthStatus.Unhealthy);
        aggregate.Description.ShouldNotBeNull().ShouldContain("Unavailable database engines:");
        aggregate.Description.ShouldContain("Failed opens: recorded/stored");
        aggregate.Description.ShouldContain("Each needs repair and an explicit open");
        aggregate.Description.ShouldNotContain("The application reopens each");
        aggregate.Description.ShouldNotContain("SECRET", Case.Sensitive);
        aggregate.Data.ShouldNotBeNull()["engine.0.failedOpenDatabaseCount"].ShouldBe(1);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_BeforeProvisioning")]
    private static extern void SetBeforeProvisioning(SqlDatabaseEngine engine, Action<SqlDatabase>? action);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_DataStorage")]
    private static extern Sql.Storage.SqlStorage DataStorage(SqlDatabase database);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_pendingReopens")]
    private static extern ref IReadOnlyList<ReopenState> Pending(DatabaseReopenService service);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_openSync")]
    private static extern ref object CompletionGate(DatabaseEngine engine);
}
