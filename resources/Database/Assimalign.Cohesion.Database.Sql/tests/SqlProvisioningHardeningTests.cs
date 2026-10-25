using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlProvisioningHardeningTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - B2: Build after every committed provisioning step applies only the remaining steps")]
    public async Task BuildAsync_CrashAfterEveryStep_ShouldConvergeWithOnlyRemainingSteps()
    {
        // Arrange: every builder-provisionable kind, including DEFAULTs, bounded named
        // types, a declared domain, a nullable CLR primary key, CHECK and foreign key.
        var storage = new CrashCaptureSqlStorageStrategy();
        SqlSchema initial = Declare(complete: false);
        SqlSchema desired = Declare(complete: true);
        SqlCompiledSchema seed = initial.Compile();
        CompiledSchemaTable child = seed.Tables.Single();
        SqlCompiledSchema before = new(SqlCompiledSchema.CurrentFormat, "b2", true, seed.Types,
            [new CompiledSchemaTable(child.Name, child.RowType,
                child.Columns.Concat([new CompiledSchemaColumn("Legacy", DatabaseType.String, true, MaxLength: 20, DefaultLiteral: "'old'")]).ToArray(),
                child.PrimaryKey, [new CompiledSchemaIndex("uq_legacy", ["Legacy"], true)],
                [new CompiledSchemaConstraint("ck_legacy", CompiledSchemaConstraintKind.Check, [], null, [],
                    new CompiledSchemaExpression("Legacy IS NULL OR LENGTH(Legacy) > 0"))]),
             new CompiledSchemaTable("obsolete", "OldRow", [new("id", DatabaseType.Int32, false)],
                 new CompiledSchemaKey("PK_obsolete", ["id"]), [], [])], []);
        SqlCompiledSchema after = desired.Compile();
        SqlMigrationScript script = SqlMigrationScriptGenerator.Generate(SqlSchemaMigrationPlanner.Plan(before, after), before);
        script.Steps.Count.ShouldBeGreaterThan(5);
        var crashes = new List<CrashCaptureSqlStorageStrategy>();
        var completed = new List<string>();

        await using (var engine = CreateBuilder(storage).Build())
        {
            var database = await engine.CreateDatabaseAsync("b2", CancellationToken.None);
            await database.ApplySchemaAsync(before, CancellationToken.None);
            await using (var adhoc = await database.CreateSessionAsync(cancellationToken: CancellationToken.None))
            {
                // Force k=0 to reconstruct the unique index and the objects to remove,
                // rather than relying on the recorded document for that prefix.
                await adhoc.ExecuteAsync("CREATE TABLE scratch (id INT)", cancellationToken: CancellationToken.None);
            }
            crashes.Add(storage.CaptureDurableImages());
            database.SchemaProvisioner.StepApplied = step =>
            {
                completed.Add(Describe(step.Operation));
                // Capture before the next statement or final schema-state save. Closing
                // the original afterwards cannot change these independent durable images.
                crashes.Add(storage.CaptureDurableImages());
            };

            // Act: one real apply, crashed at each committed step in its durable images.
            await database.ApplySchemaAsync(after, CancellationToken.None);
            completed.ShouldBe(script.Steps.Select(step => Describe(step.Operation)).ToArray());
        }

        // Assert: engine Build recovers each image and plans exactly the uncommitted tail.
        crashes.Count.ShouldBe(script.Steps.Count + 1);
        script.Steps.Select(step => step.Operation.Kind).Distinct().Order().ShouldBe(new[]
        {
            SqlSchemaMigrationOperationKind.AddTable, SqlSchemaMigrationOperationKind.DropTable,
            SqlSchemaMigrationOperationKind.AddColumn, SqlSchemaMigrationOperationKind.DropColumn,
            SqlSchemaMigrationOperationKind.AddIndex, SqlSchemaMigrationOperationKind.DropIndex,
            SqlSchemaMigrationOperationKind.AddConstraint, SqlSchemaMigrationOperationKind.DropConstraint,
        }.Order());
        for (int k = 0; k < crashes.Count; k++)
        {
            var builder = CreateBuilder(crashes[k]);
            builder.AddDatabase(desired);
            await using var engine = await builder.BuildAsync(CancellationToken.None);
            var database = await engine.OpenDatabaseAsync("b2", CancellationToken.None);
            database.SchemaProvisioner.LastPlan.ShouldNotBeNull().Operations
                .Select(Describe).ToArray().ShouldBe(completed.Skip(k).ToArray(), $"crash after step {k}");
            var state = database.Catalog.SchemaState.ShouldNotBeNull();
            state.ContentHash.ShouldBe(after.Hash);
            state.Generation.ShouldBe(database.Catalog.DdlGeneration);
            state.CanonicalDocument.ShouldBe(after.CanonicalDocument);
            GetTable(database, "parents").FindColumn("Id")!.IsNullable.ShouldBeFalse();
            var balance = GetTable(database, "children").FindColumn("Balance").ShouldNotBeNull();
            balance.DomainName.ShouldBe(nameof(Money));
            balance.Type.Precision.ShouldBe(9);
            balance.Type.Scale.ShouldBe(2);
            (await database.ApplySchemaAsync(after, CancellationToken.None)).WasAlreadyApplied.ShouldBeTrue();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - B2: a second Build skips without a provisioning catalog walk; ad-hoc DDL forces a plan")]
    public async Task BuildAsync_UnchangedThenAdhocDdl_ShouldSkipThenReplanWithoutSchemaSteps()
    {
        // Arrange
        SqlSchema schema = Declare(complete: true);
        var storage = new CrashCaptureSqlStorageStrategy();
        var firstBuilder = CreateBuilder(storage);
        firstBuilder.AddDatabase(schema);
        await using (var first = await firstBuilder.BuildAsync(CancellationToken.None))
        {
            storage = storage.CaptureDurableImages();
        }

        // Act: a new engine over the same closed storage and retained declaration.
        var secondBuilder = CreateBuilder(storage);
        secondBuilder.AddDatabase(schema);
        await using var second = await secondBuilder.BuildAsync(CancellationToken.None);
        var database = await second.OpenDatabaseAsync("b2", CancellationToken.None);

        // Assert: the skip did not enter ownership validation, comparison or snapshotting.
        second.DeclaredDatabases.Single().Result.ShouldNotBeNull().WasAlreadyApplied.ShouldBeTrue();
        // Schema's own assembly asserts that this retained instance computes its
        // document and SHA-256 once; Build must keep that same warmed instance.
        second.DeclaredDatabases.Single().Schema.ShouldBeSameAs(schema.Compile());
        database.SchemaProvisioner.CatalogWalkCount.ShouldBe(0);
        database.Definitions.ValidationWalkCount.ShouldBe(0);
        database.SchemaProvisioner.LastPlan.ShouldBeNull();
        ulong recordedGeneration = database.Catalog.SchemaState!.Generation;
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE scratch (id INT)", cancellationToken: CancellationToken.None);
        database.Catalog.DdlGeneration.ShouldBeGreaterThan(recordedGeneration);
        database.Definitions.FindUnresolved().ShouldBeNull();
        database.Definitions.FindUnresolvedDomain().ShouldBeNull();
        database.Definitions.ValidationWalkCount.ShouldBe(1);
        var afterDdl = storage.CaptureDurableImages();
        var result = await database.ApplySchemaAsync(schema.Compile(), CancellationToken.None);
        result.WasAlreadyApplied.ShouldBeFalse();
        result.OperationCount.ShouldBe(0);
        database.SchemaProvisioner.CatalogWalkCount.ShouldBeGreaterThan(0);
        database.SchemaProvisioner.LastPlan.ShouldNotBeNull().Operations.ShouldBeEmpty();
        database.Catalog.SchemaState!.Generation.ShouldBe(database.Catalog.DdlGeneration);
        GetTable(database, "scratch").Owner.ShouldBe(DatabaseObjectOwner.Adhoc);

        var thirdBuilder = CreateBuilder(afterDdl);
        thirdBuilder.AddDatabase(schema);
        await using var third = await thirdBuilder.BuildAsync(CancellationToken.None);
        var replanned = await third.OpenDatabaseAsync("b2", CancellationToken.None);
        third.DeclaredDatabases.Single().Result.ShouldNotBeNull().WasAlreadyApplied.ShouldBeFalse();
        replanned.SchemaProvisioner.CatalogWalkCount.ShouldBeGreaterThan(0);
        replanned.SchemaProvisioner.LastPlan.ShouldNotBeNull().Operations.ShouldBeEmpty();
        replanned.Catalog.SchemaState!.Generation.ShouldBe(replanned.Catalog.DdlGeneration);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - B2: Verify reports a changed generation without writing a new marker")]
    public async Task BuildAsync_VerifyAfterAdhocDdl_ShouldReportDriftWithoutWriting()
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        SqlSchema schema = Declare(complete: true);
        var builder = CreateBuilder(storage);
        builder.AddDatabase(schema);
        ulong generation;
        await using (var engine = await builder.BuildAsync(CancellationToken.None))
        {
            var database = await engine.OpenDatabaseAsync("b2", CancellationToken.None);
            generation = database.Catalog.SchemaState!.Generation;
            await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
            await session.ExecuteAsync("CREATE TABLE scratch (id INT)", cancellationToken: CancellationToken.None);
            storage = storage.CaptureDurableImages();
        }

        // Act
        var verify = CreateBuilder(storage);
        verify.AddDatabase("b2", database => { database.Schema(schema); database.Provisioning = SqlProvisioningMode.Verify; });
        var error = await Should.ThrowAsync<SqlSchemaMigrationException>(async () =>
            await verify.BuildAsync(CancellationToken.None));

        // Assert
        error.Message.ShouldContain("CS03103");
        await using var reopened = CreateBuilder(storage.CaptureDurableImages()).Build();
        (await reopened.OpenDatabaseAsync("b2", CancellationToken.None)).Catalog.SchemaState!.Generation.ShouldBe(generation);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - B2: compiled unique indexes and composite keys survive every crash boundary")]
    public async Task ApplySchemaAsync_CompiledUniqueAndCompositeKeyCrash_ShouldApplyOnlyRemainingSteps()
    {
        // Arrange: these are compiled-schema kinds; the retained C# builder has a
        // single-column key and a non-unique Index verb. Exercise the same provisioner.
        var schema = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "b2", false, [],
            [new CompiledSchemaTable("pairs", "Pairs",
                [new("id", DatabaseType.Int32, true), new("part", DatabaseType.Int32, true),
                 new("code", DatabaseType.String, true, MaxLength: 32, DefaultLiteral: "'unset'")],
                new CompiledSchemaKey("PK_pairs", ["id", "part"]),
                [new CompiledSchemaIndex("uq_code", ["code"], true)], [])], []);
        var storage = new CrashCaptureSqlStorageStrategy();
        var crashes = new List<CrashCaptureSqlStorageStrategy>();
        string[] expected = SqlMigrationScriptGenerator.Generate(SqlSchemaMigrationPlanner.Plan(null, schema)).Steps
            .Select(step => Describe(step.Operation)).ToArray();
        await using (var engine = CreateBuilder(storage).Build())
        {
            var database = await engine.CreateDatabaseAsync("b2", CancellationToken.None);
            crashes.Add(storage.CaptureDurableImages());
            database.SchemaProvisioner.StepApplied = _ => crashes.Add(storage.CaptureDurableImages());
            await database.ApplySchemaAsync(schema, CancellationToken.None);
        }

        // Act / Assert: reopen through Build, then the public compiled-schema entry point.
        for (int k = 0; k < crashes.Count; k++)
        {
            var builder = CreateBuilder(crashes[k]);
            builder.AddDatabase("b2");
            await using var engine = await builder.BuildAsync(CancellationToken.None);
            var database = await engine.OpenDatabaseAsync("b2", CancellationToken.None);
            await database.ApplySchemaAsync(schema, CancellationToken.None);
            database.SchemaProvisioner.LastPlan!.Operations.Select(Describe).ToArray().ShouldBe(expected.Skip(k).ToArray());
            var table = GetTable(database, "pairs");
            table.PrimaryKeyColumns.ShouldBe(["id", "part"]);
            database.Catalog.GetIndexes(table.ObjectId).Single(index => index.Name == "uq_code").IsUnique.ShouldBeTrue();
            database.Catalog.SchemaState!.Generation.ShouldBe(database.Catalog.DdlGeneration);
        }
    }

    private static SqlDatabaseEngineBuilder CreateBuilder(CrashCaptureSqlStorageStrategy storage)
    {
        var builder = SqlDatabaseEngine.CreateBuilder("b2-tests");
        builder.Options.StorageStrategy = storage;
        builder.Types.Add(SqlType.CreateDomain(nameof(Money), SqlType.Decimal(9, 2)));
        builder.Types.Add(SqlType.CreateDomain("label", SqlType.VarChar(32)));
        return builder;
    }

    private static SqlSchema Declare(bool complete)
        => SqlSchema.Create("b2", schema =>
        {
            schema.AllowDestructiveChanges();
            schema.Type<Money>(type => type.Decimal(9, 2));
            schema.Table<Child>("children", table =>
            {
                table.Key(row => row.Id);
                if (complete)
                {
                    table.Default(row => row.ParentId, (int?)null);
                    table.Column(row => row.Note, "label");
                    table.DefaultSql(row => row.Note, " 'pending' ");
                    table.Column(row => row.Amount, "DECIMAL(12, 3)");
                    table.DefaultSql(row => row.Balance, "0.00");
                    table.Index(row => row.Note);
                    table.References<Parent>(row => row.ParentId);
                    table.Check("ck_balance", "Balance >= 0");
                }
            });
            if (complete)
            {
                schema.Table<Parent>("parents", table =>
                {
                    table.Key(row => row.Id);
                    table.Column(row => row.Name, "VARCHAR(24)");
                    table.Default(row => row.Name, "unnamed");
                    table.Index(row => row.Name);
                    table.Check("ck_name", "LENGTH(Name) > 0");
                });
            }
        });

    private static Catalog.SqlCatalogTable GetTable(SqlDatabase database, string name)
    {
        database.Catalog.TryGetTable("dbo", name, out var table).ShouldBeTrue();
        return table;
    }

    private static string Describe(SqlSchemaMigrationOperation operation)
        => $"{operation.Kind}:{operation.ParentName}:{operation.ObjectName}";

    private sealed record Parent(int? Id, string Name);
    private sealed record Child(int Id, int? ParentId, string? Note, decimal? Amount, Money Balance);
    private readonly record struct Money(decimal Value);
}
