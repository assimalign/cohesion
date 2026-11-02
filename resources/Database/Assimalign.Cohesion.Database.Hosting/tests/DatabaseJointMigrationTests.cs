using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Hosting.Health;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

[Collection(DatabaseProvisioningHealthCollection.Name)]
public sealed class DatabaseJointMigrationTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - Joint migration: progress and failure stay degraded online, retry restores health")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckAsync_JointMigration_ShouldReportOnlineFailureAndSuccessfulRetry(bool failFirst)
    {
        // Arrange
        string path = Path.Combine(Path.GetTempPath(), "cohesion-joint-health", Guid.NewGuid().ToString("N"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        int inject = failFirst ? 1 : 0;
        try
        {
            var seed = SqlDatabaseEngine.CreateBuilder("seed");
            seed.Options.RootPath = path;
            await using (var engine = await seed.AddDatabase(Source()).BuildAsync(timeout.Token))
            {
                var database = await engine.OpenDatabaseAsync("typed", timeout.Token);
                await using var writer = await database.CreateSessionAsync(timeout.Token);
                await writer.ExecuteAsync("INSERT INTO items VALUES(1,10),(2,20)", cancellationToken: timeout.Token);
            }
            var builder = DatabaseApplication.CreateBuilder();
            builder.Options.ReopenOfflineDatabases = false;
            builder.AddSql("sql", sql =>
            {
                sql.Options.RootPath = path;
                var domain = SqlType.CreateDomain("migrated", SqlType.BigInt);
                sql.Types.Add(domain).AddCast(SqlCast.Create<int, long>(SqlType.Integer, domain, value =>
                {
                    if (Interlocked.Increment(ref calls) == 1)
                    { entered.TrySetResult(); release.Wait(timeout.Token); }
                    if (Interlocked.Exchange(ref inject, 0) == 1) { throw new InvalidOperationException("injected conversion refusal"); }
                    return value;
                }, SqlCastContext.Assignment));
                sql.AddDatabase(Target());
            });
            await using var application = builder.Build(timeout.Token);
            try
            {
                await entered.Task.WaitAsync(timeout.Token);
                var engine = (SqlDatabaseEngine)application.Context.Engines.Single();
                var database = await engine.OpenDatabaseAsync("typed", timeout.Token);

                // Act
                (await application.Context.CheckAsync(timeout.Token)).Status.ShouldBe(HealthStatus.Degraded);
                release.Set();
                while (database.Provisioning is { FailureCode: null }) { await Task.Delay(1, timeout.Token); }

                // Assert
                engine.OfflineDatabases.ShouldBeEmpty();
                if (failFirst)
                {
                    database.Provisioning!.FailureCode.ShouldBe("CS03402");
                    var health = await application.Context.CheckAsync(timeout.Token);
                    health.Status.ShouldBe(HealthStatus.Degraded);
                    health.Data!["engine.0.provisioning.0.failureCode"].ShouldBe("CS03402");
                    await using var writer = await database.CreateSessionAsync(timeout.Token);
                    await writer.ExecuteAsync("INSERT INTO items VALUES(3,30)", cancellationToken: timeout.Token);
                    while (true)
                    {
                        try { await database.ApplySchemaAsync(Target().Compile(), timeout.Token); break; }
                        catch (DatabaseObjectLockedException) { await Task.Delay(1, timeout.Token); }
                    }
                    (await application.Context.CheckAsync(timeout.Token)).Status.ShouldBe(HealthStatus.Healthy);
                }
                else
                { (await application.Context.CheckAsync(timeout.Token)).Status.ShouldBe(HealthStatus.Healthy); }
            }
            finally { release.Set(); }
        }
        finally { release.Set(); if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); } }
    }

    private static SqlSchema Source() => SqlSchema.Create("typed", schema =>
        schema.Table<OldRow>("items", table => { table.Key(row => row.Id); table.Column(row => row.Value); }));

    private static SqlSchema Target() => SqlSchema.Create("typed", schema =>
    {
        schema.AllowDestructiveChanges();
        schema.Table<NewRow>("items", table => { table.Key(row => row.Id); table.Column(row => row.Value, "migrated"); });
    });

    private sealed record OldRow(int Id, int Value);
    private sealed record NewRow(int Id, long Value);
}
