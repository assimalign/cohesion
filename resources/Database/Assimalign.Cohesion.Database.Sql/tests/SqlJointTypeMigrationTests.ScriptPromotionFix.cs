using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    private sealed record PromotionRow(int Id, int Part);
    private sealed record PromotionWideRow(int Id, long Part);

    private static SqlSchema PromotionSchema(bool declared, bool rewrite = false, bool extra = false) => SqlSchema.Create("typed", schema =>
    {
        schema.AllowDestructiveChanges();
        if (rewrite)
        {
            schema.Table<PromotionWideRow>("items", table =>
            {
                table.Key(row => row.Id); table.Column(row => row.Part);
                if (declared) { table.Index(row => row.Part); }
                if (extra) { table.Index(row => row.Id); }
            });
        }
        else
        {
            schema.Table<PromotionRow>("items", table =>
            {
                table.Key(row => row.Id); table.Column(row => row.Part);
                if (declared) { table.Index(row => row.Part); }
                if (extra) { table.Index(row => row.Id); }
            });
        }
    });

    private static SqlCompiledSchema PromotionDeclaration(bool declared, bool unique = false, bool rewrite = false, bool extra = false)
    {
        var compiled = PromotionSchema(declared, rewrite, extra).Compile();
        return unique ? ChangeItems(compiled, indexes: compiled.Tables.Single().Indexes.Select(index =>
            new CompiledSchemaIndex(index.Name, index.Columns, index.Name == "IX_items_Part", index.Predicate, index.KeyOrders)).ToArray()) : compiled;
    }

    private static int DurableIndexRows(SqlDatabase database, string name)
    {
        int count = 0;
        using var rows = database.CatalogStorage.GetUnitIterator();
        while (rows.MoveNext())
        {
            var reader = new DatabaseKeyReader(rows.Current.Data.Span);
            if (reader.ReadInt32() != 5) { continue; }
            reader.ReadInt64();
            if (reader.ReadString(out _).Equals(name, StringComparison.OrdinalIgnoreCase)) { count++; }
        }
        return count;
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Script promotion fix: declared names win on keep, replacement, TYPE and stale receipts (#1778, #1821)")]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    public async Task ApplyAsync_ScriptPromotionFixDeclaredName_ShouldPublishOneDurableIndex(bool different, bool rewrite, bool stale)
    {
        // Arrange: remove the script from the next release, retaining its durable claim.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var storage = new CrashCaptureSqlStorageStrategy();
        ulong heap;
        string script = $"CREATE {(different ? "UNIQUE " : "")}INDEX IX_items_Part ON items(Part)" + (stale ? "; DROP INDEX IX_items_Part ON items" : "");
        var seedBuilder = Builder(storage).AddDatabase("typed", db =>
        { db.Schema(PromotionSchema(false)); db.Script("001", script); });
        await using (var seed = await seedBuilder.BuildAsync(guard.Token))
        {
            var database = await seed.OpenDatabaseAsync("typed", guard.Token);
            heap = Table(database).ObjectId;
            database.Catalog.Scripts.ShouldHaveSingleItem().Objects.ShouldContain("I:\"dbo\".\"items\".\"IX_items_Part\"");
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES(1,7)", cancellationToken: guard.Token);
            storage = storage.CaptureDurableImages();
        }
        var target = PromotionDeclaration(true, rewrite: rewrite, extra: rewrite);
        await using (var engine = await Builder(storage).AddDatabase("typed", db => db.Schema(PromotionSchema(true, rewrite, rewrite))).BuildAsync(guard.Token))
        {
            var database = await engine.OpenDatabaseAsync("typed", guard.Token);
            // Act
            await WaitDoneAsync(database, guard.Token);
            // Assert
            database.Provisioning.ShouldBeNull();
            database.Catalog.SchemaState!.ContentHash.ShouldBe(target.Hash);
            if (!different && !rewrite) { Table(database).ObjectId.ShouldBe(heap); }
            DurableIndexRows(database, "IX_items_Part").ShouldBe(1);
            database.Catalog.GetIndexes(Table(database).ObjectId).Single(index => index.Name == "IX_items_Part").IsUnique.ShouldBeFalse();
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES(2,7)", cancellationToken: guard.Token);
            storage = storage.CaptureDurableImages();
        }
        await using (var restarted = await Builder(storage).AddDatabase("typed", db =>
            db.Schema(PromotionSchema(true, rewrite, rewrite))).BuildAsync(guard.Token))
        {
            var database = await restarted.OpenDatabaseAsync("typed", guard.Token);
            DurableIndexRows(database, "IX_items_Part").ShouldBe(1);
            database.Catalog.GetIndexes(Table(database).ObjectId).ShouldContain(index => index.Name == "IX_items_Part" && index.Owner == DatabaseObjectOwner.Schema);
            storage = storage.CaptureDurableImages();
        }
        if (stale)
        {
            // A later release rewrites the declared index despite its stale script claim.
            rewrite = true;
            await using var rewritten = await Builder(storage).AddDatabase("typed", db =>
                db.Schema(PromotionSchema(true, rewrite: true, extra: true))).BuildAsync(guard.Token);
            var database = await rewritten.OpenDatabaseAsync("typed", guard.Token);
            await WaitDoneAsync(database, guard.Token);
            database.Provisioning.ShouldBeNull();
            DurableIndexRows(database, "IX_items_Part").ShouldBe(1);
            storage = storage.CaptureDurableImages();
        }
        // Another release removes the declaration through the ordinary inline path.
        await using (var removed = await Builder(storage).AddDatabase("typed", db =>
            db.Schema(PromotionSchema(false, rewrite, rewrite))).BuildAsync(guard.Token))
        {
            var database = await removed.OpenDatabaseAsync("typed", guard.Token);
            await WaitDoneAsync(database, guard.Token);
            database.Provisioning.ShouldBeNull();
            DurableIndexRows(database, "IX_items_Part").ShouldBe(0);
            storage = storage.CaptureDurableImages();
        }
        await using var final = await Builder(storage).BuildAsync(guard.Token);
        var current = await final.OpenDatabaseAsync("typed", guard.Token);
        DurableIndexRows(current, "IX_items_Part").ShouldBe(0);
        await using var session = await current.CreateSessionAsync(guard.Token);
        await session.ExecuteAsync("INSERT INTO items VALUES(3,7)", cancellationToken: guard.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Script promotion fix: deferred DropIndex removes a promoted uniqueness claim (#1778, #1821)")]
    public async Task ApplyAsync_ScriptPromotionFixDeferredDrop_ShouldRemoveUniqueness()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = await Builder(new()).AddDatabase("typed", db =>
        { db.Schema(PromotionSchema(false)); db.Script("001", "CREATE UNIQUE INDEX IX_items_Part ON items(Part)"); }).BuildAsync(guard.Token);
        var database = await engine.OpenDatabaseAsync("typed", guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("INSERT INTO items VALUES(1,7)", cancellationToken: guard.Token);
        await database.SchemaProvisioner.ApplyAsync(PromotionDeclaration(true, unique: true), guard.Token, deferDataScalingSteps: true);
        await WaitDoneAsync(database, guard.Token);
        database.Provisioning.ShouldBeNull();
        DurableIndexRows(database, "IX_items_Part").ShouldBe(1);
        await Should.ThrowAsync<DatabaseException>(async () => await writer.ExecuteAsync("INSERT INTO items VALUES(2,7)", cancellationToken: guard.Token));

        // Act: AddIndex forces a deferred plan alongside DropIndex.
        await database.SchemaProvisioner.ApplyAsync(PromotionDeclaration(false, extra: true), guard.Token, deferDataScalingSteps: true);
        await WaitDoneAsync(database, guard.Token);

        // Assert
        database.Provisioning.ShouldBeNull();
        DurableIndexRows(database, "IX_items_Part").ShouldBe(0);
        await writer.ExecuteAsync("INSERT INTO items VALUES(2,7)", cancellationToken: guard.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Script promotion fix: duplicate replacement names refuse before the catalog transaction (#1778, #1821)")]
    public async Task PublishStorageReplacementAsync_ScriptPromotionFixDuplicateName_ShouldRefuseBeforeWrite()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = await Builder(new()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(PromotionDeclaration(true), guard.Token);
        var table = Table(database);
        var indexes = database.Catalog.GetIndexes(table.ObjectId).ToArray();
        var duplicate = indexes.Single(index => index.Name == "IX_items_Part");
        long before = database.CatalogStorage.JournalLength;
        ulong generation = database.Catalog.DdlGeneration;

        // Act
        var failure = await Should.ThrowAsync<SqlCatalogException>(async () => await database.Catalog.PublishStorageReplacementAsync(
            [], null, [], indexes.Append(duplicate).ToArray(), database.IndexManager.ExportRegistrations(), new(), false,
            TransactionSequence.None, string.Empty, null, guard.Token, kept: [table], expectedKept: [table]));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05060");
        failure.Message.ShouldContain("IX_items_Part", Case.Sensitive);
        database.CatalogStorage.JournalLength.ShouldBe(before);
        database.Catalog.DdlGeneration.ShouldBe(generation);
        DurableIndexRows(database, "IX_items_Part").ShouldBe(1);
    }
}
