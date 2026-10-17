using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Hosting.Health;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DatabaseProvisioningHealthCollection
{
    public const string Name = "Database provisioning health";
}

[Collection(DatabaseProvisioningHealthCollection.Name)]
public sealed class DatabaseProvisioningHealthTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - 49a: real deferred DDL reports its step and rows through root health, then Healthy")]
    public async Task CheckAsync_DeferredCheck_ShouldReportProgressThenHealthy()
    {
        // Arrange: the immutable test function pauses validation of the second existing row.
        string path = Path.Combine(Path.GetTempPath(), "cohesion-host-49a", Guid.NewGuid().ToString("N"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        try
        {
            await SeedAsync(path, timeout.Token);
            DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder();
            builder.AddSql("sql", sql =>
            {
                sql.Options.RootPath = path;
                sql.Functions.Add(SqlScalarFunction.Create<int, bool>("provision_probe", value =>
                {
                    if (Interlocked.Increment(ref calls) == 2)
                    {
                        entered.TrySetResult();
                        release.Wait(timeout.Token);
                    }
                    return value > 0;
                }, SqlFunctionVolatility.Immutable));
                sql.AddDatabase(Declare(check: true));
            });

            // Act: host Build returns while its real engine's table validation is paused.
            await using var application = builder.Build(timeout.Token);
            try
            {
                await entered.Task.WaitAsync(timeout.Token);
                HealthContribution degraded = await application.Context.CheckAsync(timeout.Token);

                // Assert: Hosting depends only on the root instance's general state.
                degraded.Status.ShouldBe(HealthStatus.Degraded);
                degraded.Description.ShouldNotBeNull().ShouldContain("AddConstraint items");
                degraded.Description.ShouldContain("step 1 of 1");
                degraded.Data.ShouldNotBeNull()["engine.0.provisioning.0.rowsProcessed"].ShouldBe(1L);
                degraded.Data["engine.0.provisioning.0.totalRows"].ShouldBe(3L);
            }
            finally
            {
                release.Set();
            }
            var engine = (SqlDatabaseEngine)application.Context.Engines.Single();
            var database = await engine.OpenDatabaseAsync("health", timeout.Token);
            while (database.Provisioning is not null)
            {
                await Task.Delay(1, timeout.Token);
            }
            calls.ShouldBe(3);
            (await application.Context.CheckAsync(timeout.Token)).Status.ShouldBe(HealthStatus.Healthy);
        }
        finally
        {
            release.Set();
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - 49a: the application Build token cancels SQL provisioning and the next Build converges")]
    public async Task Build_CanceledDuringSqlProvisioning_ShouldPropagateAndConverge()
    {
        // Arrange
        string path = Path.Combine(Path.GetTempPath(), "cohesion-host-49a", Guid.NewGuid().ToString("N"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        try
        {
            await SeedAsync(path, timeout.Token);
            DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder();
            builder.AddSql("sql", sql =>
            {
                sql.Options.RootPath = path;
                sql.Functions.Add(SqlScalarFunction.Create<int, bool>("provision_probe", value =>
                {
                    Interlocked.Increment(ref calls);
                    cancellation.Cancel();
                    return value > 0;
                }, SqlFunctionVolatility.Immutable));
                sql.AddDatabase("health", database => { database.Schema(Declare(check: true)); database.RunMigrationsInline = true; });
            });

            // Act
            Should.Throw<OperationCanceledException>(() => builder.Build(cancellation.Token));
            var retry = DatabaseApplication.CreateBuilder();
            retry.AddSql("sql", sql =>
            {
                sql.Options.RootPath = path;
                sql.Functions.Add(SqlScalarFunction.Create<int, bool>("provision_probe", value => value > 0, SqlFunctionVolatility.Immutable));
                sql.AddDatabase("health", database => { database.Schema(Declare(check: true)); database.RunMigrationsInline = true; });
            });
            await using var application = retry.Build(timeout.Token);

            // Assert
            calls.ShouldBeGreaterThan(0);
            (await application.Context.CheckAsync(timeout.Token)).Status.ShouldBe(HealthStatus.Healthy);
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private static SqlSchema Declare(bool check) => SqlSchema.Create("health", schema => schema.Table<Item>("items", table =>
    {
        table.Column(row => row.Id);
        if (check)
        {
            table.Check("ck_probe", "provision_probe(Id)");
        }
    }));

    private static async Task SeedAsync(string path, CancellationToken cancellationToken)
    {
        var builder = SqlDatabaseEngine.CreateBuilder("seed");
        builder.Options.RootPath = path;
        await using var engine = await builder.AddDatabase(Declare(check: false)).BuildAsync(cancellationToken);
        var database = await engine.OpenDatabaseAsync("health", cancellationToken);
        await using var session = await database.CreateSessionAsync(cancellationToken);
        await session.ExecuteAsync("INSERT INTO items (Id) VALUES (1), (2), (3)", cancellationToken: cancellationToken);
    }

    private sealed record Item(int Id);
}
