using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection("SQL namespace DDL interlock")]
public sealed class SqlSchemaDdlInterlockTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - CREATE TABLE reservations and DROP SCHEMA exclude each other in both directions")]
    public async Task ExecuteAsync_ConcurrentSchemaAndTableDdl_ShouldRefuseConflictingAdmission()
    {
        // Arrange: pause the first catalog write, including the CREATE TABLE reservation.
        var storage = new CrashCaptureSqlStorageStrategy();
        await using var engine = SqlDatabaseEngine.Create("namespace-interlock", new() { StorageStrategy = storage });
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var first = await database.CreateSessionAsync(CancellationToken.None);
        await using var second = await database.CreateSessionAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var watch = Stopwatch.StartNew();
        int tableFirst = 0;
        int schemaFirst = 0;
        int iteration = 0;

        // Act: continue beyond the minimum until both interleavings have been exercised.
        while ((watch.Elapsed < TimeSpan.FromMilliseconds(100) || tableFirst < 3 || schemaFirst < 3) && watch.Elapsed < TimeSpan.FromSeconds(20))
        {
            string name = "ns" + iteration++;
            await first.ExecuteAsync("CREATE SCHEMA " + name, cancellationToken: timeout.Token);
            await ExerciseAsync("CREATE TABLE " + name + ".t(id INT)", "DROP SCHEMA " + name + " CASCADE");
            tableFirst++;
            await ExerciseAsync("DROP SCHEMA " + name + " CASCADE", "CREATE TABLE " + name + ".r(id INT)");
            schemaFirst++;
            database.Catalog.Tables.Count.ShouldBe(0);
        }

        // Assert
        tableFirst.ShouldBeGreaterThanOrEqualTo(3);
        schemaFirst.ShouldBeGreaterThanOrEqualTo(3);

        async Task ExerciseAsync(string leading, string refused)
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var leadingFlow = new AsyncLocal<bool>();
            int armed = 1;
            storage.StorageWriteStarting = catalog =>
            {
                if (catalog && leadingFlow.Value && Interlocked.Exchange(ref armed, 0) == 1)
                {
                    entered.Set();
                    release.Wait(timeout.Token);
                }
            };
            var pending = DedicatedTestTask.Run(async () =>
            {
                leadingFlow.Value = true;
                return await first.ExecuteAsync(leading, cancellationToken: timeout.Token);
            }, timeout.Token);
            try
            {
                entered.Wait(timeout.Token);
                var failure = await Should.ThrowAsync<DatabaseObjectLockedException>(async () =>
                    await second.ExecuteAsync(refused, cancellationToken: timeout.Token));
                failure.Message.ShouldContain("SQL definition DDL");
            }
            finally
            {
                storage.StorageWriteStarting = null;
                release.Set();
                await pending;
            }
        }
    }
}
