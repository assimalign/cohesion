using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Hosting;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Sql.Schema;

namespace Assimalign.Cohesion.Database.Hosting.ApplicationModel.Tests;

[Collection(DatabaseHealthCollection.Name)]
public sealed class DatabaseResourceHealthProgressTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Hosting.ApplicationModel] - Health: an offline database is Unhealthy until its real engine reopens it")]
    public async Task CheckAsync_WithOfflineDatabase_ShouldRecoverAfterReopen()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var worker = new HealthFailureWorker();
        var engineBuilder = SqlDatabaseEngine.CreateBuilder("sql");
        engineBuilder.Options.WorkerFailureWindow = TimeSpan.FromTicks(1);
        engineBuilder.Options.WorkerFailureMinimumPasses = 2;
        engineBuilder.AddWorker(_ => worker);
        await using var engine = engineBuilder.Build();
        await engine.CreateDatabaseAsync("offline", guard.Token);
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(engine);
        DatabaseResourceHealthTests.Register(builder);
        await using var application = builder.Build(guard.Token);
        var contributors = DatabaseResourceHealthTests.Instance(application).HealthContributors;
        var database = contributors.Single(value => value.Name == "database/sql/OFFLINE");
        var engineHealth = contributors.Single(value => value.Name == "engine/sql");

        // Act: two recorded failed passes cross the engine's configured offline window.
        worker.RunIteration(guard.Token).ShouldBeFalse();
        (await engineHealth.CheckAsync(guard.Token)).Status.ShouldBe(ApplicationInstanceHealthStatus.Degraded);
        Thread.Sleep(1);
        worker.RunIteration(guard.Token).ShouldBeFalse();
        while (engine.GetOfflineError("offline") is null || engine.State != EngineState.Running)
        {
            await Task.Delay(1, guard.Token);
        }
        var offline = await database.CheckAsync(guard.Token);
        await engine.OpenDatabaseAsync("offline", guard.Token);
        var reopened = await database.CheckAsync(guard.Token);

        // Assert
        offline.Status.ShouldBe(ApplicationInstanceHealthStatus.Unhealthy);
        offline.Data.ShouldNotBeNull()["cause"].ShouldBe("CheckpointFailures");
        offline.Description.ShouldNotBeNull().ShouldNotContain("private storage path", Case.Sensitive);
        reopened.Status.ShouldBe(ApplicationInstanceHealthStatus.Healthy);
        (await engineHealth.CheckAsync(guard.Token)).Status.ShouldBe(ApplicationInstanceHealthStatus.Healthy);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting.ApplicationModel] - Health: a real deferred validation contributes provisioning progress then becomes Healthy")]
    public async Task CheckAsync_WithDeferredProvisioning_ShouldDegradeThenRecover()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var directory = new TemporaryDirectory();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seed = SqlDatabaseEngine.CreateBuilder("seed");
        seed.Options.RootPath = directory.Path;
        await using (var engine = await seed.AddDatabase(Schema(false)).BuildAsync(guard.Token))
        {
            var database = await engine.OpenDatabaseAsync("progress", guard.Token);
            await using var session = await database.CreateSessionAsync(cancellationToken: guard.Token);
            await session.ExecuteAsync("INSERT INTO items (Id) VALUES (1), (2), (3)", cancellationToken: guard.Token);
        }
        int calls = 0;
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddSql("sql", sql =>
        {
            sql.Options.RootPath = directory.Path;
            sql.Functions.Add(SqlScalarFunction.Create<int, bool>("health_probe", value =>
            {
                if (Interlocked.Increment(ref calls) == 2)
                {
                    entered.TrySetResult();
                    release.Wait(guard.Token);
                }
                return value > 0;
            }, SqlFunctionVolatility.Immutable));
            sql.AddDatabase(Schema(true));
        });
        DatabaseResourceHealthTests.Register(builder);
        await using var application = builder.Build(guard.Token);
        try
        {
            await entered.Task.WaitAsync(guard.Token);
            var contributor = DatabaseResourceHealthTests.Instance(application).HealthContributors.Single(value => value.Name == "database/sql/PROGRESS");

            // Act
            var provisioning = await contributor.CheckAsync(guard.Token);
            release.Set();
            var database = await application.Context.GetEngine<SqlDatabaseEngine>("sql").OpenDatabaseAsync("progress", guard.Token);
            while (database.Provisioning is not null) { await Task.Delay(1, guard.Token); }
            var healthy = await contributor.CheckAsync(guard.Token);

            // Assert
            provisioning.Status.ShouldBe(ApplicationInstanceHealthStatus.Degraded);
            provisioning.Data.ShouldNotBeNull()["rowsProcessed"].ShouldBe("1");
            provisioning.Data["operation"].ShouldBe("Joint/AddConstraint");
            provisioning.Data.ContainsKey("totalRows").ShouldBeFalse();
            healthy.Status.ShouldBe(ApplicationInstanceHealthStatus.Healthy);
            calls.ShouldBe(3);
        }
        finally { release.Set(); }
    }

    private static SqlSchema Schema(bool check) => SqlSchema.Create("progress", schema => schema.Table<Item>("items", table =>
    {
        table.Column(row => row.Id);
        if (check) { table.Check("ck_probe", "health_probe(Id)"); }
    }));

    private sealed record Item(int Id);
}
