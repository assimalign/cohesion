using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Persisted mixed DEFAULT, CHECK and partial predicate rebind identically after reopen (#1788, #1808)")]
    public async Task OpenAsync_CommonPersistedExpressions_ShouldRetainBinding()
    {
        // Arrange: exercise the accepted SQL DDL surface independently of the compiled boundary.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var storage = new CrashCaptureSqlStorageStrategy();
        string? defaultText = null;
        string? checkText = null, predicateText = null;
        for (int pass = 0; pass < 2; pass++)
        {
            await using var engine = await Builder(storage).BuildAsync(guard.Token);
            var database = pass == 0 ? await engine.CreateDatabaseAsync("typed", guard.Token) : await engine.OpenDatabaseAsync("typed", guard.Token);
            await using var session = await database.CreateSessionAsync(guard.Token);
            if (pass == 0)
            {
                await session.ExecuteAsync("CREATE TABLE items(Id INT,n INT,amount DECIMAL DEFAULT(COALESCE(NULL::INT,1.5)),CHECK(COALESCE(n,0.4)>0))", cancellationToken: guard.Token);
                await session.ExecuteAsync("CREATE INDEX partial ON items(n) WHERE CASE WHEN n IS NULL THEN 0.4 ELSE n END>0", cancellationToken: guard.Token);
                defaultText = Table(database).FindColumn("amount")!.DefaultLiteral!;
                checkText = (string)(await RowsAsync(session, "SELECT CHECK_CLAUSE FROM INFORMATION_SCHEMA.CHECK_CONSTRAINTS", guard.Token)).Single()[0]!;
                predicateText = (string)(await RowsAsync(session, "SELECT PREDICATE FROM COHESION_SCHEMA.INDEXES WHERE INDEX_NAME='partial'", guard.Token)).Single()[0]!;
            }

            // Act: reopened defaults and constraints are bound before use.
            await session.ExecuteAsync($"INSERT INTO items(Id) VALUES({pass + 1})", cancellationToken: guard.Token);

            // Assert.
            Table(database).FindColumn("amount")!.DefaultLiteral.ShouldBe(defaultText);
            (await RowsAsync(session, "SELECT CHECK_CLAUSE FROM INFORMATION_SCHEMA.CHECK_CONSTRAINTS", guard.Token)).Single()[0].ShouldBe(checkText);
            (await RowsAsync(session, "SELECT PREDICATE FROM COHESION_SCHEMA.INDEXES WHERE INDEX_NAME='partial'", guard.Token)).Single()[0].ShouldBe(predicateText);
            await using (var result = (DatabaseResultSet)await session.ExecuteAsync(
                "SELECT amount,COALESCE(n,1.5),CASE WHEN n IS NULL THEN 1.5 ELSE n END FROM items", cancellationToken: guard.Token))
            {
                result.Columns.Select(c => c.Type).ShouldBe([DatabaseType.Decimal, DatabaseType.Decimal, DatabaseType.Decimal]);
                int count = 0;
                await foreach (var row in result.GetRowsAsync(guard.Token))
                {
                    row.GetValue(0).ShouldBe(1.5m); row.GetValue(1).ShouldBe(1.5m); row.GetValue(2).ShouldBe(1.5m);
                    count++;
                }
                count.ShouldBe(pass + 1);
            }
            var check = await Should.ThrowAsync<DatabaseException>(async () =>
                await session.ExecuteAsync("INSERT INTO items(Id,n) VALUES(9,-1)", cancellationToken: guard.Token));
            check.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03038");
            var plan = await RowsAsync(session, "EXPLAIN SELECT COUNT(*) FROM items WHERE CASE WHEN n IS NULL THEN 0.4 ELSE n END>0", guard.Token);
            string description = string.Join('|', plan.SelectMany(row => row));
            description.ShouldContain("partial", Case.Sensitive);
            description.ShouldContain("partial_predicate=", Case.Sensitive);
            (await RowsAsync(session, "SELECT COUNT(*) FROM items WHERE CASE WHEN n IS NULL THEN 0.4 ELSE n END>0", guard.Token)).Single()[0].ShouldBe((long)pass + 1);
            storage = storage.CaptureDurableImages();
        }
    }
    [Theory(DisplayName = "Cohesion Test [Sql] - Common-type partial predicates prove SELECT and upsert and retire exactly their owner (#1788, #1808)")]
    [InlineData("COALESCE(v,0)>0")]
    [InlineData("CASE WHEN v IS NULL THEN 0 ELSE v END>0")]
    public async Task ExecuteAsync_CommonPartialPredicate_ShouldInferSeekAndReclaimExactly(string predicate)
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = SqlDatabaseEngine.Create("partial-common-owner", new());
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        await session.ExecuteAsync("CREATE TABLE items(id INT PRIMARY KEY,v INT)", cancellationToken: guard.Token);
        for (int first = 0; first < 512; first += 128)
        {
            await session.ExecuteAsync("INSERT INTO items VALUES" + string.Join(',', Enumerable.Range(first, 128).Select(i => $"({i},{i})")), cancellationToken: guard.Token);
        }
        await session.ExecuteAsync("INSERT INTO items VALUES(900,NULL),(901,-1)", cancellationToken: guard.Token);
        await session.ExecuteAsync("CREATE UNIQUE INDEX ix ON items(v) WHERE " + predicate, cancellationToken: guard.Token);
        var registration = database.IndexManager.ExportRegistrations().Single(i => i.Definition.Name == "ix");
        ulong owner = database.IndexManager.PageOwner(registration);
        var pages = database.DataStorage.GetOwnerIndexPages(owner).ToArray();
        pages.Length.ShouldBeGreaterThan(1);
        var survivor = database.IndexManager.ExportRegistrations().Single(i => i.Definition.Name != "ix");
        ulong survivorOwner = database.IndexManager.PageOwner(survivor);
        var survivorPages = database.DataStorage.GetOwnerIndexPages(survivorOwner).ToArray();
        var heapPages = database.DataStorage.GetOwnerPages(Table(database).ObjectId).ToArray();

        // Act / Assert: exact implication selects the partial tree and conflict arbiter.
        var plan = await RowsAsync(session, $"EXPLAIN SELECT v FROM items WHERE v=1 AND {predicate}", guard.Token);
        string description = string.Join('|', plan.SelectMany(r => r));
        description.ShouldContain("partial_predicate=", Case.Sensitive);
        description.ShouldContain("ix", Case.Sensitive);
        (await RowsAsync(session, $"SELECT v FROM items WHERE v=1 AND {predicate}", guard.Token)).Single()[0].ShouldBe(1);
        (await session.ExecuteAsync($"INSERT INTO items VALUES(999,1) ON CONFLICT(v) WHERE {predicate} DO NOTHING", cancellationToken: guard.Token)).AffectedCount.ShouldBe(0);
        var pin = await database.Coordinator.BeginSnapshotPinAsync(guard.Token);
        try
        {
            await session.ExecuteAsync("DROP INDEX ix ON items", cancellationToken: guard.Token);
            database.TableReclamation.RunBatch(guard.Token);
            database.DataStorage.GetOwnerIndexPages(owner).ShouldBe(pages);
        }
        finally { await database.Coordinator.RollbackAsync(pin, guard.Token); }
        await DrainReclamationAsync(database, guard.Token);
        database.DataStorage.GetOwnerIndexPages(owner).ShouldBeEmpty();
        foreach (var page in pages) { database.DataStorage.FreeSpaceMap.IsAllocated(page).ShouldBeFalse(); }
        database.DataStorage.GetOwnerIndexPages(survivorOwner).ShouldBe(survivorPages);
        database.DataStorage.GetOwnerPages(Table(database).ObjectId).ShouldBe(heapPages);
        (await RowsAsync(session, "SELECT COUNT(*) FROM items", guard.Token)).Single()[0].ShouldBe(514L);
    }
}
