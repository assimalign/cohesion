using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// The SQL engine builder provisions the databases it declares before its build returns (B1 of
/// the engine extensibility design, §5.2 and §5.3): the create, open and already-applied paths,
/// Verify mode, the refusals before and after the engine exists, the options snapshot, and the
/// declaration's ownership of its database. No test here references the hosting layer: this is
/// the embedded path.
/// </summary>
public sealed class SqlEngineProvisioningTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "cohesion-sql-provisioning",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            try
            {
                Directory.Delete(_rootPath, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Provisioning: Build creates a declared database that does not exist and applies its schema")]
    public async Task BuildAsync_DeclaredDatabaseMissing_ShouldCreateItAndApplyTheSchema()
    {
        // Arrange
        var builder = CreateBuilder("create-path");
        builder.AddDatabase("sales", database => database.Schema(Sales));

        // Act
        await using var engine = await builder.BuildAsync();

        // Assert: the database is open when the build returns, its tables schema-owned.
        engine.TryGetDatabase("sales", out SqlDatabase? sales).ShouldBeTrue();
        sales!.Catalog.TryGetTable("dbo", "orders", out var orders).ShouldBeTrue();
        orders.Owner.ShouldBe(DatabaseObjectOwner.Schema);
        orders.OwningSchema.ShouldBe("sales");
        var result = engine.DeclaredDatabases.ShouldHaveSingleItem().Result.ShouldNotBeNull();
        result.WasAlreadyApplied.ShouldBeFalse();
        result.FromHash.ShouldBeNull();
        result.ToHash.ShouldBe(Sales.Compile().Hash);
        result.OperationCount.ShouldBeGreaterThan(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Provisioning: Build opens a declared database that exists, with its data, and skips a schema already applied")]
    public async Task BuildAsync_DeclaredDatabaseExists_ShouldOpenItAndSkipTheAppliedSchema()
    {
        // Arrange: a first build creates and provisions the database, and a row is written.
        await using (var first = await CreateBuilder("open-path").AddDatabase(Sales).BuildAsync())
        {
            first.TryGetDatabase("sales", out SqlDatabase? created).ShouldBeTrue();
            await using var session = await created!.CreateSessionAsync();
            await session.ExecuteAsync("INSERT INTO orders VALUES (1, 'kept')");
        }

        // Act: a second engine over the same files declares the same schema.
        await using var second = await CreateBuilder("open-path").AddDatabase(Sales).BuildAsync();

        // Assert: opened, not created, and no DDL ran.
        second.TryGetDatabase("sales", out SqlDatabase? opened).ShouldBeTrue();
        await using (var session = await opened!.CreateSessionAsync())
        {
            (await CountAsync(session, "orders")).ShouldBe(1);
        }

        var result = second.DeclaredDatabases.ShouldHaveSingleItem().Result.ShouldNotBeNull();
        result.WasAlreadyApplied.ShouldBeTrue();
        result.OperationCount.ShouldBe(0);
        result.FromHash.ShouldBe(result.ToHash);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Provisioning: a declared database without a schema is only ensured to exist")]
    public async Task BuildAsync_DeclaredDatabaseWithoutSchema_ShouldOnlyEnsureItExists()
    {
        // Act
        await using var engine = await CreateBuilder("ensure").AddDatabase("scratch").BuildAsync();

        // Assert
        engine.TryGetDatabase("scratch", out SqlDatabase? scratch).ShouldBeTrue();
        scratch!.Catalog.Tables.ShouldBeEmpty();
        scratch.Catalog.SchemaState.ShouldBeNull();
        engine.DeclaredDatabases.ShouldHaveSingleItem().Result.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Provisioning: embedded code provisions through BuildAsync, without the hosting layer")]
    public async Task BuildAsync_Embedded_ShouldReturnAnEngineWhoseDeclaredDatabaseIsOpen()
    {
        // Arrange: the design's §3.10, an in-memory engine with a reusable schema value.
        var sql = SqlDatabaseEngine.CreateBuilder("local");
        sql.AddDatabase(Sales);

        // Act
        await using SqlDatabaseEngine engine = await sql.BuildAsync(TestTimeout.Token());
        SqlDatabase sales = await engine.OpenDatabaseAsync("sales", TestTimeout.Token());
        await using var session = await sales.CreateSessionAsync();
        await session.ExecuteAsync("INSERT INTO orders VALUES (1, 'embedded')");

        // Assert: open returns the database the build opened.
        engine.TryGetDatabase("sales", out SqlDatabase? open).ShouldBeTrue();
        open.ShouldBeSameAs(sales);
        (await CountAsync(session, "orders")).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Provisioning: Verify mode accepts the applied schema and refuses drift and a missing database without changing anything")]
    public async Task BuildAsync_VerifyMode_ShouldRefuseDriftWithoutDdl()
    {
        // Arrange: a database provisioned with the first schema.
        await using (var applied = await CreateBuilder("verify").AddDatabase(Sales).BuildAsync())
        {
        }

        // Act
        await using (var verified = await CreateBuilder("verify")
            .AddDatabase("sales", database =>
            {
                database.Schema(Sales);
                database.Provisioning = SqlProvisioningMode.Verify;
            })
            .BuildAsync())
        {
            verified.DeclaredDatabases.ShouldHaveSingleItem().Result.ShouldNotBeNull().WasAlreadyApplied.ShouldBeTrue();
        }

        var drift = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await CreateBuilder("verify")
            .AddDatabase("sales", database =>
            {
                database.Schema(SalesWithIndex);
                database.Provisioning = SqlProvisioningMode.Verify;
            })
            .BuildAsync());
        var missing = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await CreateBuilder("verify")
            .AddDatabase("archive", database => database.Provisioning = SqlProvisioningMode.Verify)
            .BuildAsync());

        // Assert: coded, naming the engine and the database, and nothing created or migrated.
        drift.Message.ShouldStartWith("COHSQLP003: SQL engine 'verify', database 'sales': the declared schema (hash ", Case.Sensitive);
        drift.Message.ShouldContain($"the database records schema hash {Sales.Compile().Hash}");
        missing.Message.ShouldStartWith("COHSQLP003: SQL engine 'verify', database 'archive': the database does not exist.", Case.Sensitive);
        await using var reopened = SqlDatabaseEngine.Create("verify", new SqlDatabaseEngineOptions { RootPath = _rootPath });
        var sales = await reopened.OpenDatabaseAsync("sales");
        sales.Catalog.TryGetTable("dbo", "orders", out var orders).ShouldBeTrue();
        sales.Catalog.GetIndexes(orders.ObjectId).ShouldHaveSingleItem().IsPrimaryKey.ShouldBeTrue();
        await Should.ThrowAsync<DatabaseNotFoundException>(async () => await reopened.OpenDatabaseAsync("archive"));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Provisioning: a failed provisioning disposes the engine and leaves no worker thread running")]
    public async Task BuildAsync_ProvisioningFails_ShouldDisposeTheEngineAndJoinItsWorkers()
    {
        // Arrange: an existing database created with another collation than the one declared.
        await using (var seeding = SqlDatabaseEngine.Create("seed", new SqlDatabaseEngineOptions { RootPath = _rootPath }))
        {
            await seeding.CreateDatabaseAsync("sales", Collation.CaseInsensitive);
        }

        RecordingWorker? worker = null;
        RecordingServer? server = null;
        SqlDatabaseEngine? product = null;
        var builder = CreateBuilder("fails");
        builder.AddWorker(engine => worker = new RecordingWorker(product = engine));

        // The server factory runs after every worker is attached, so the worker has pumped a pass on
        // its own thread before provisioning starts.
        builder.AddServer(engine =>
        {
            worker!.Started.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
            return server = new RecordingServer(engine);
        });
        builder.AddDatabase(Sales);

        // Act
        var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await builder.BuildAsync());

        // Assert: the refusal is coded, and the engine, its server and its worker were released
        // before the build threw, the worker's pump thread joined.
        failure.Message.ShouldStartWith(
            "COHSQLP002: SQL engine 'fails', database 'sales': the database was created with default collation 'case_insensitive', but it is declared with 'binary'.",
            Case.Sensitive);
        product.ShouldNotBeNull().State.ShouldBe(EngineState.Disposed);
        server.ShouldNotBeNull().Stops.ShouldBe(1);
        worker.ShouldNotBeNull().Disposals.ShouldBe(1);
        worker.PumpThread.ShouldNotBeNull().IsAlive.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Provisioning: a database declared with the collation it has is accepted")]
    public async Task BuildAsync_DeclaredCollationMatches_ShouldOpenTheDatabase()
    {
        // Arrange
        await using (var seeding = SqlDatabaseEngine.Create("seed", new SqlDatabaseEngineOptions { RootPath = _rootPath }))
        {
            await seeding.CreateDatabaseAsync("people", Collation.CaseInsensitive);
        }

        // Act
        await using var engine = await CreateBuilder("collated")
            .AddDatabase("people", database => database.DefaultCollation = Collation.CaseInsensitive)
            .AddDatabase("fresh", database => database.DefaultCollation = Collation.CaseInsensitive)
            .BuildAsync();

        // Assert: an existing database keeps its collation, and a new one is born on the declared one.
        engine.TryGetDatabase("people", out SqlDatabase? people).ShouldBeTrue();
        people!.Catalog.DefaultCollation.ShouldBeSameAs(Collation.CaseInsensitive);
        engine.TryGetDatabase("fresh", out SqlDatabase? fresh).ShouldBeTrue();
        fresh!.Catalog.DefaultCollation.ShouldBeSameAs(Collation.CaseInsensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Provisioning: a failed schema step names the engine, the database and the step, after compensation")]
    public async Task BuildAsync_SchemaStepFails_ShouldNameTheEngineTheDatabaseAndTheStep()
    {
        // Arrange: a child row whose parent does not exist, then a schema that adds the reference.
        await using (var first = await CreateBuilder("steps").AddDatabase(Unreferenced).BuildAsync())
        {
            first.TryGetDatabase("shop", out SqlDatabase? shop).ShouldBeTrue();
            await using var session = await shop!.CreateSessionAsync();
            await session.ExecuteAsync("INSERT INTO children VALUES (1, 99)");
        }

        // Act
        var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () =>
            await CreateBuilder("steps").AddDatabase(Referenced).BuildAsync());

        // Assert
        failure.Message.ShouldStartWith(
            "COHSQLP004: SQL engine 'steps', database 'shop': applying schema 'shop': step 1 of 1 (AddConstraint 'children.",
            Case.Sensitive);
        failure.Message.ShouldEndWith("Every completed step was compensated.", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Provisioning: a schema principal is refused before any file is touched")]
    public async Task BuildAsync_SchemaDeclaresPrincipal_ShouldRefuseBeforeAnyIo()
    {
        // Arrange
        var principals = SqlSchema.Create("sales", schema =>
        {
            schema.Table<Order>("orders", table => table.Key(order => order.Id));
            schema.Principal("reader", principal => principal.Grant(SqlPermission.Read, "orders"));
        });
        SqlDatabaseEngine? product = null;
        var builder = CreateBuilder("principals");
        builder.AddWorker(engine => new RecordingWorker(product = engine));
        builder.AddDatabase(principals);

        // Act
        var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await builder.BuildAsync());

        // Assert: refused in phase 3, before the engine (whose creation makes the root directory).
        failure.Message.ShouldBe(
            "COHSQLP001: SQL engine 'principals' cannot provision database 'sales': its schema declares principal 'reader', " +
            "and principals and grants are refused until their DDL exists. Nothing was opened or written.");
        product.ShouldBeNull();
        Directory.Exists(_rootPath).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Provisioning: a schema that does not compile is refused before any file is touched")]
    public async Task BuildAsync_SchemaDoesNotCompile_ShouldRefuseBeforeAnyIo()
    {
        // Arrange: a reference to a row type no table of the schema maps.
        var invalid = SqlSchema.Create("sales", schema => schema.Table<Order>("orders", table =>
        {
            table.Key(order => order.Id);
            table.References<Missing>(order => order.Item);
        }));

        // Act
        var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () =>
            await CreateBuilder("invalid").AddDatabase(invalid).BuildAsync());

        // Assert
        failure.Message.ShouldStartWith("COHSQLP001: SQL engine 'invalid' cannot provision database 'sales': ", Case.Sensitive);
        failure.InnerException.ShouldBeOfType<SqlSchemaValidationException>();
        Directory.Exists(_rootPath).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Provisioning: the engine refuses to drop a declared database, and drops any other")]
    public async Task DropDatabaseAsync_DeclaredDatabase_ShouldBeRefused()
    {
        // Arrange
        await using var engine = await SqlDatabaseEngine.CreateBuilder("owner").AddDatabase(Sales).BuildAsync();
        await engine.CreateDatabaseAsync("adhoc");

        // Act
        var refusal = await Should.ThrowAsync<DatabaseObjectLockedException>(async () => await engine.DropDatabaseAsync("SALES"));
        await engine.DropDatabaseAsync("adhoc");

        // Assert: the declaration owns the database (owner decision 56), the refusal names it as
        // declared and says how to drop it, and the database stays open.
        refusal.ObjectName.ShouldBe("sales");
        refusal.OwningSchema.ShouldBe("sales");
        refusal.Operation.ShouldBe("DROP DATABASE");
        refusal.Message.ShouldBe(
            "SQL engine 'owner' declares database 'sales' (SqlDatabaseEngineBuilder.AddDatabase), so DROP DATABASE is " +
            "refused. Remove the declaration from the engine builder and rebuild the engine before dropping it.");
        engine.TryGetDatabase("sales", out SqlDatabase? _).ShouldBeTrue();
        engine.TryGetDatabase("adhoc", out SqlDatabase? _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Provisioning: an imperative apply of another schema to a declared database is refused")]
    public async Task ApplySchemaAsync_DeclaredDatabase_ShouldAcceptOnlyItsDeclaredSchema()
    {
        // Arrange
        await using var engine = await CreateBuilder("imperative")
            .AddDatabase(Sales)
            .AddDatabase("scratch")
            .BuildAsync();
        engine.TryGetDatabase("sales", out SqlDatabase? sales).ShouldBeTrue();
        engine.TryGetDatabase("scratch", out SqlDatabase? scratch).ShouldBeTrue();
        SqlDatabase adhoc = await engine.CreateDatabaseAsync("adhoc");

        // Act
        var refusal = await Should.ThrowAsync<DatabaseObjectLockedException>(async () =>
            await sales!.ApplySchemaAsync(SalesWithIndex.Compile()));
        var same = await sales!.ApplySchemaAsync(Sales.Compile());
        var ensured = await scratch!.ApplySchemaAsync(SqlSchema.Compile("scratch", schema =>
            schema.Table<Order>("orders", table => table.Key(order => order.Id))));
        var free = await adhoc.ApplySchemaAsync(SqlSchema.Compile("adhoc", schema =>
            schema.Table<Order>("orders", table => table.Key(order => order.Id))));

        // Assert: the declared schema stays the only one applied; a database declared without a
        // schema, and one the builder did not declare, accept any.
        refusal.Operation.ShouldBe("APPLY SCHEMA");
        refusal.Message.ShouldBe(
            "SQL engine 'imperative' declares database 'sales' with a schema (SqlDatabaseBuilder.Schema), so " +
            "ApplySchemaAsync refuses another one: change the declaration and rebuild the engine instead.");
        same.WasAlreadyApplied.ShouldBeTrue();
        sales.Catalog.TryGetTable("dbo", "orders", out var orders).ShouldBeTrue();
        sales.Catalog.GetIndexes(orders.ObjectId).ShouldHaveSingleItem().IsPrimaryKey.ShouldBeTrue();
        ensured.WasAlreadyApplied.ShouldBeFalse();
        free.WasAlreadyApplied.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Provisioning: a destructive step and an adopted table are refused with a code before any step runs")]
    public async Task BuildAsync_PolicyRefusals_ShouldBeCodedAndChangeNothing()
    {
        // Arrange: a provisioned database with a second table, and an ad-hoc table in another.
        SqlSchema salesWithArchive = SqlSchema.Create("sales", schema =>
        {
            schema.Table<Order>("orders", table =>
            {
                table.Key(order => order.Id);
                table.Column(order => order.Item);
            });
            schema.Table<Missing>("archive", table => table.Key(missing => missing.Id));
        });
        await using (var first = await CreateBuilder("policy").AddDatabase(salesWithArchive).BuildAsync())
        {
            SqlDatabase shop = await first.CreateDatabaseAsync("shop");
            await using var session = await shop.CreateSessionAsync();
            await session.ExecuteAsync("CREATE TABLE parents (Id INT PRIMARY KEY)");
        }

        // Act: dropping the archive table is destructive, and the shop schema would adopt the table.
        var destructive = await Should.ThrowAsync<SqlSchemaMigrationException>(async () =>
            await CreateBuilder("policy").AddDatabase(Sales).BuildAsync());
        var adopt = await Should.ThrowAsync<SqlSchemaMigrationException>(async () =>
            await CreateBuilder("policy").AddDatabase(Unreferenced).BuildAsync());

        // Assert
        destructive.Message.ShouldStartWith(
            "COHSQLP005: SQL engine 'policy', database 'sales': Destructive migration step 'DropTable' for 'archive' was refused.",
            Case.Sensitive);
        destructive.Message.ShouldEndWith("Nothing was changed.", Case.Sensitive);
        adopt.Message.ShouldBe(
            "COHSQLP005: SQL engine 'policy', database 'shop': SQL schema 'shop' cannot adopt table 'parents' because it " +
            "was not created by this schema. Nothing was changed.");
        await using var reopened = SqlDatabaseEngine.Create("policy", new SqlDatabaseEngineOptions { RootPath = _rootPath });
        var sales = await reopened.OpenDatabaseAsync("sales");
        sales.Catalog.TryGetTable("dbo", "archive", out _).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Provisioning: a Verify drift names the first object that differs")]
    public async Task BuildAsync_VerifyDrift_ShouldNameTheFirstDifference()
    {
        // Arrange: the database holds the schema without the index.
        await using (var applied = await CreateBuilder("drift").AddDatabase(Sales).BuildAsync())
        {
        }

        // Act
        var drift = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await CreateBuilder("drift")
            .AddDatabase("sales", database =>
            {
                database.Schema(SalesWithIndex);
                database.Provisioning = SqlProvisioningMode.Verify;
            })
            .BuildAsync());

        // Assert
        drift.Message.ShouldContain(
            $"the database records schema hash {Sales.Compile().Hash}; first difference from the declaration: table 'orders': index '",
            Case.Sensitive);
        drift.Message.ShouldContain("' is missing.", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Provisioning: a build canceled while it applies a schema throws OperationCanceledException and converges on the next build")]
    public async Task BuildAsync_CanceledWhileApplying_ShouldThrowCanceledAndConverge()
    {
        // Arrange: a ten-table schema, so a cancellation can land inside the apply, between or in
        // the steps (a commit interrupted by the token surfaces as an aborted transaction).
        SqlSchema wide = SqlSchema.Create("wide", schema =>
        {
            schema.Table<W0>("t0", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Index(row => row.Name); });
            schema.Table<W1>("t1", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Index(row => row.Name); });
            schema.Table<W2>("t2", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Index(row => row.Name); });
            schema.Table<W3>("t3", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Index(row => row.Name); });
            schema.Table<W4>("t4", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Index(row => row.Name); });
            schema.Table<W5>("t5", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Index(row => row.Name); });
            schema.Table<W6>("t6", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Index(row => row.Name); });
            schema.Table<W7>("t7", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Index(row => row.Name); });
            schema.Table<W8>("t8", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Index(row => row.Name); });
            schema.Table<W9>("t9", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Index(row => row.Name); });
        });

        for (int attempt = 0; attempt < 24; attempt++)
        {
            string root = Path.Combine(_rootPath, $"attempt-{attempt}");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(1 + (2 * attempt)));
            SqlDatabaseEngine? product = null;
            var builder = SqlDatabaseEngine.CreateBuilder("cancel");
            builder.Options.RootPath = root;
            builder.AddWorker(engine => new RecordingWorker(product = engine));
            builder.AddDatabase(wide);

            // Act
            Exception? failure = null;
            try
            {
                await using SqlDatabaseEngine built = await builder.BuildAsync(cancellation.Token);
            }
            catch (Exception caught)
            {
                failure = caught;
            }

            // Assert: every failure is the cancellation, never a step failure, and leaves no engine;
            // the next build converges on the declared schema.
            if (failure is not null)
            {
                failure.ShouldBeAssignableTo<OperationCanceledException>($"attempt {attempt}: {failure}");
                if (product is not null)
                {
                    product.State.ShouldBe(EngineState.Disposed);
                }
            }

            var retry = SqlDatabaseEngine.CreateBuilder("cancel");
            retry.Options.RootPath = root;
            retry.AddDatabase(wide);
            await using SqlDatabaseEngine converged = await retry.BuildAsync(TestTimeout.Token());
            converged.TryGetDatabase("wide", out SqlDatabase? database).ShouldBeTrue();
            database!.Catalog.SchemaState.ShouldNotBeNull().ContentHash.ShouldBe(wide.Compile().Hash);
            database.Catalog.Tables.Count.ShouldBe(10);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Provisioning: a database declared again from its own callback is refused")]
    public void AddDatabase_ReenteredFromItsCallback_ShouldRefuseTheDuplicate()
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("reentrant");

        // Act
        var duplicate = Should.Throw<InvalidOperationException>(() =>
            builder.AddDatabase("sales", _ => builder.AddDatabase("SALES")));
        builder.AddDatabase("sales");

        // Assert: the inner declaration met the outer one, and the failed outer one was withdrawn.
        duplicate.Message.ShouldBe("SQL engine 'reentrant' already declares database 'sales'.");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Provisioning: a change to the server options after Build does not reach the server")]
    public async Task ServerOptions_ChangedAfterBuild_ShouldNotReachTheServer()
    {
        // Arrange
        SqlDatabaseServerOptions? captured = null;
        var builder = SqlDatabaseEngine.CreateBuilder("served-options");
        builder.AddServer(server =>
        {
            server.Listener = new InMemoryConnectionListener();
            server.MaxSessions = 8;
            captured = server;
        });
        var direct = new SqlDatabaseServerOptions { Listener = new InMemoryConnectionListener(), MaxSessions = 3 };

        // Act
        await using var engine = await builder.BuildAsync();
        await using var other = SqlDatabaseEngine.Create("direct", new SqlDatabaseEngineOptions());
        var created = SqlDatabaseServer.Create(other, direct);
        captured!.MaxSessions = 0;
        direct.MaxSessions = 0;

        // Assert: each server keeps its own checked copy.
        var server = engine.Servers.ShouldHaveSingleItem().ShouldBeOfType<SqlDatabaseServer>();
        server.ServerOptions.ShouldNotBeSameAs(captured);
        server.ServerOptions.MaxSessions.ShouldBe(8);
        created.ServerOptions.ShouldNotBeSameAs(direct);
        created.ServerOptions.MaxSessions.ShouldBe(3);
        await created.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Provisioning: a change to the options after Build, or after Create, does not reach the engine")]
    public async Task Options_ChangedAfterBuild_ShouldNotReachTheEngine()
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("snapshot");
        builder.Options.PageWriteBackBatchSize = 64;
        builder.Options.MaintenanceInterval = TimeSpan.FromMinutes(7);
        var options = new SqlDatabaseEngineOptions { PageWriteBackBatchSize = 64 };

        // Act
        await using var built = await builder.BuildAsync();
        await using var created = SqlDatabaseEngine.Create("created", options);
        builder.Options.PageWriteBackBatchSize = 1;
        builder.Options.MaintenanceInterval = TimeSpan.FromSeconds(1);
        options.PageWriteBackBatchSize = 1;

        // Assert: each engine keeps its own copy (the write-back worker reads the batch size on
        // every pass, so the caller's object used to reach a running engine).
        built.EngineOptions.ShouldNotBeSameAs(builder.Options);
        built.EngineOptions.PageWriteBackBatchSize.ShouldBe(64);
        built.EngineOptions.MaintenanceInterval.ShouldBe(TimeSpan.FromMinutes(7));
        created.EngineOptions.ShouldNotBeSameAs(options);
        created.EngineOptions.PageWriteBackBatchSize.ShouldBe(64);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Provisioning: the options copy keeps every option")]
    public async Task Options_Snapshot_ShouldCopyEveryOption()
    {
        // Arrange: every option away from its default. The strategy is the one the engine uses, so
        // the root path is set beside it only to show the copy keeps it.
        var clock = new Assimalign.Cohesion.Database.Tests.ManualTimeProvider();
        var strategy = new TestObjects.CrashCaptureSqlStorageStrategy();
        string rootPath = Path.Combine(_rootPath, "unused-root");
        var options = new SqlDatabaseEngineOptions
        {
            RootPath = rootPath,
            StorageStrategy = strategy,
            Durability = Assimalign.Cohesion.Database.Storage.StorageCommitDurability.None,
            GroupCommitWindow = TimeSpan.FromMilliseconds(7),
            CheckpointInterval = TimeSpan.FromMinutes(9),
            CheckpointJournalSize = 3 * 1024 * 1024,
            WorkerFailureWindow = TimeSpan.FromSeconds(42),
            WorkerFailureMinimumPasses = 5,
            JournalSizeLimit = 12 * 1024 * 1024,
            BufferPoolCapacity = 2 * 1024 * 1024,
            PageWriteBackInterval = TimeSpan.FromSeconds(3),
            PageWriteBackBatchSize = 33,
            MaintenanceInterval = TimeSpan.FromMinutes(4),
            DeferredUndoRetryDelay = TimeSpan.FromMilliseconds(250),
            TimeProvider = clock,
            ExpressionNestingLimit = 99,
        };

        // Act
        await using var engine = SqlDatabaseEngine.Create("every-option", options);
        var copy = engine.EngineOptions;

        // Assert
        engine.Name.ShouldBe("every-option");
        copy.Durability.ShouldBe(options.Durability);
        copy.GroupCommitWindow.ShouldBe(options.GroupCommitWindow);
        copy.CheckpointInterval.ShouldBe(options.CheckpointInterval);
        copy.CheckpointJournalSize.ShouldBe(options.CheckpointJournalSize);
        copy.WorkerFailureWindow.ShouldBe(options.WorkerFailureWindow);
        copy.WorkerFailureMinimumPasses.ShouldBe(options.WorkerFailureMinimumPasses);
        copy.JournalSizeLimit.ShouldBe(options.JournalSizeLimit);
        copy.BufferPoolCapacity.ShouldBe(options.BufferPoolCapacity);
        copy.PageWriteBackInterval.ShouldBe(options.PageWriteBackInterval);
        copy.PageWriteBackBatchSize.ShouldBe(options.PageWriteBackBatchSize);
        copy.MaintenanceInterval.ShouldBe(options.MaintenanceInterval);
        copy.DeferredUndoRetryDelay.ShouldBe(options.DeferredUndoRetryDelay);
        copy.TimeProvider.ShouldBeSameAs(clock);
        copy.ExpressionNestingLimit.ShouldBe(options.ExpressionNestingLimit);
        copy.RootPath.ShouldNotBeNull();
        copy.RootPath.ShouldBe(options.RootPath);
        copy.StorageStrategy.ShouldBeSameAs(strategy);

        // Every property, the internal ones included: an option added to either set fails here
        // until Snapshot copies it and this test checks it.
        typeof(SqlDatabaseEngineOptions).GetProperties().Length.ShouldBe(13);
        typeof(SqlDatabaseEngineOptions)
            .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Length.ShouldBe(16);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Provisioning: a name that is not a single file-name component is refused at the declaration")]
    [InlineData("..")]
    [InlineData("../escaped")]
    [InlineData("nested/escaped")]
    [InlineData("nested\\escaped")]
    public async Task AddDatabase_NotASingleFileNameComponent_ShouldBeRefusedAtTheCall(string name)
    {
        // Arrange
        var builder = CreateBuilder("names");
        int configured = 0;

        // Act
        var refusal = Should.Throw<ArgumentException>(() => builder.AddDatabase(name, _ => configured++));
        await using var engine = await builder.BuildAsync(TestTimeout.Token());

        // Assert: refused before its callback ran and before anything exists, so the build that
        // follows declares nothing and nothing is written outside the root.
        refusal.ParamName.ShouldBe("name");
        refusal.Message.ShouldStartWith("A database name must be a single file-name component.", Case.Sensitive);
        configured.ShouldBe(0);
        engine.DeclaredDatabases.ShouldBeEmpty();
        Directory.Exists(Path.Combine(Path.GetDirectoryName(_rootPath)!, "escaped")).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Provisioning: the engine refuses a database name that reaches outside its root path")]
    public async Task DatabaseCores_NameOutsideTheRoot_ShouldBeRefused()
    {
        // Arrange: a directory beside the engine's root, which a "../victim" drop used to delete
        // (the strategy combined the name with the root path as given, and the drop does not
        // check that the database exists first).
        string root = Path.Combine(_rootPath, "root");
        string victim = Path.Combine(_rootPath, "victim");
        Directory.CreateDirectory(victim);
        await using var engine = SqlDatabaseEngine.Create("escape", new SqlDatabaseEngineOptions { RootPath = root });

        // Act
        var create = await Should.ThrowAsync<ArgumentException>(async () => await engine.CreateDatabaseAsync("../escaped"));
        var collated = await Should.ThrowAsync<ArgumentException>(async () => await engine.CreateDatabaseAsync("../escaped", Collation.Binary));
        var open = await Should.ThrowAsync<ArgumentException>(async () => await engine.OpenDatabaseAsync(".."));
        var drop = await Should.ThrowAsync<ArgumentException>(async () => await engine.DropDatabaseAsync("../victim"));

        // Assert: nothing outside the root was created, opened or deleted.
        create.ParamName.ShouldBe("name");
        collated.ParamName.ShouldBe("name");
        open.ParamName.ShouldBe("name");
        drop.ParamName.ShouldBe("name");
        drop.Message.ShouldStartWith("A database name must be a single file-name component.", Case.Sensitive);
        Directory.Exists(Path.Combine(_rootPath, "escaped")).ShouldBeFalse();
        Directory.Exists(victim).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Provisioning: a database is declared once per engine, and a reusable schema must name it")]
    public void AddDatabase_Declarations_ShouldBeCheckedAtTheCall()
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("declarations");
        int configured = 0;
        builder.AddDatabase("sales", _ => configured++);

        // Act
        var duplicate = Should.Throw<InvalidOperationException>(() => builder.AddDatabase("SALES", _ => configured++));
        var mismatch = Should.Throw<ArgumentException>(() => builder.AddDatabase("reports", database => database.Schema(Sales)));
        var twice = Should.Throw<InvalidOperationException>(() => builder.AddDatabase("ledger", database =>
        {
            database.Schema(schema => schema.Table<Order>("orders", table => table.Key(order => order.Id)));
            database.Schema(schema => schema.Table<Order>("orders", table => table.Key(order => order.Id)));
        }));
        var mode = Should.Throw<ArgumentOutOfRangeException>(() => builder.AddDatabase("audit", database => database.Provisioning = (SqlProvisioningMode)7));

        // Assert: refused before its callback ran, case-insensitively, as database names compare.
        configured.ShouldBe(1);
        duplicate.Message.ShouldBe("SQL engine 'declarations' already declares database 'sales'.");
        mismatch.ParamName.ShouldBe("schema");
        twice.Message.ShouldBe("Database 'ledger' already declares its schema.");
        mode.ParamName.ShouldBe("value");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Provisioning: the declarations are frozen once the build began")]
    public async Task SqlDatabaseBuilder_AfterBuild_ShouldRefuseChanges()
    {
        // Arrange
        SqlDatabaseBuilder? declared = null;
        var builder = SqlDatabaseEngine.CreateBuilder("frozen").AddDatabase("sales", database => declared = database);
        await using var engine = await builder.BuildAsync();

        // Act / Assert
        Should.Throw<InvalidOperationException>(() => declared!.DefaultCollation = Collation.Binary);
        Should.Throw<InvalidOperationException>(() => declared!.Provisioning = SqlProvisioningMode.Verify);
        Should.Throw<InvalidOperationException>(() => declared!.Schema(Sales));
        Should.Throw<InvalidOperationException>(() => builder.AddDatabase("late"));
        await Should.ThrowAsync<InvalidOperationException>(async () => await builder.BuildAsync());
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Provisioning: a canceled build creates nothing")]
    public async Task BuildAsync_Canceled_ShouldCreateNothing()
    {
        // Arrange
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        SqlDatabaseEngine? product = null;
        var builder = CreateBuilder("canceled").AddDatabase(Sales);
        builder.AddWorker(engine => new RecordingWorker(product = engine));

        // Act
        await Should.ThrowAsync<OperationCanceledException>(async () => await builder.BuildAsync(canceled.Token));

        // Assert
        product.ShouldBeNull();
        Directory.Exists(_rootPath).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Provisioning: AddServer with options creates a SQL server over the engine, and disposes the listener of one it cannot create")]
    public async Task AddServer_WithOptions_ShouldCreateTheModelsServer()
    {
        // Arrange
        var listener = new InMemoryConnectionListener();
        var builder = SqlDatabaseEngine.CreateBuilder("served");
        builder.AddServer(server => server.Listener = listener);
        var refused = SqlDatabaseEngine.CreateBuilder("unserved");
        SqlDatabaseEngine? product = null;
        refused.AddWorker(engine => new RecordingWorker(product = engine));
        refused.AddServer(server => server.MaxSessions = 4);

        // Act
        await using var engine = await builder.BuildAsync();
        var failure = await Should.ThrowAsync<ArgumentException>(async () => await refused.BuildAsync());

        // Assert
        var server = engine.Servers.ShouldHaveSingleItem().ShouldBeOfType<SqlDatabaseServer>();
        server.Engine.ShouldBeSameAs(engine);
        failure.ShouldNotBeNull();
        product.ShouldNotBeNull().State.ShouldBe(EngineState.Disposed);
    }

    private static readonly SqlSchema Sales = SqlSchema.Create("sales", schema =>
        schema.Table<Order>("orders", table =>
        {
            table.Key(order => order.Id);
            table.Column(order => order.Item);
        }));

    private static readonly SqlSchema SalesWithIndex = SqlSchema.Create("sales", schema =>
        schema.Table<Order>("orders", table =>
        {
            table.Key(order => order.Id);
            table.Column(order => order.Item);
            table.Index(order => order.Item);
        }));

    private static readonly SqlSchema Unreferenced = SqlSchema.Create("shop", schema =>
    {
        schema.Table<Parent>("parents", table => table.Key(parent => parent.Id));
        schema.Table<Child>("children", table =>
        {
            table.Key(child => child.Id);
            table.Column(child => child.ParentId);
        });
    });

    private static readonly SqlSchema Referenced = SqlSchema.Create("shop", schema =>
    {
        schema.Table<Parent>("parents", table => table.Key(parent => parent.Id));
        schema.Table<Child>("children", table =>
        {
            table.Key(child => child.Id);
            table.Column(child => child.ParentId);
            table.References<Parent>(child => child.ParentId);
        });
    });

    private SqlDatabaseEngineBuilder CreateBuilder(string name)
    {
        var builder = SqlDatabaseEngine.CreateBuilder(name);
        builder.Options.RootPath = _rootPath;
        return builder;
    }

    private static async Task<long> CountAsync(SqlDatabaseSession session, string table)
    {
        await using var result = (await session.ExecuteAsync($"SELECT COUNT(*) FROM {table}"))
            .ShouldBeAssignableTo<QueryResultSet>()!;
        await foreach (var row in result.GetRowsAsync())
        {
            return row.GetValue(0).ShouldBeOfType<long>();
        }

        throw new InvalidOperationException("COUNT(*) returned no row.");
    }

    private sealed record Order(long Id, string Item);

    private sealed record Missing(long Id);

    private sealed record Parent(long Id);

    private sealed record Child(long Id, long ParentId);

    private sealed record W0(long Id, string Name);

    private sealed record W1(long Id, string Name);

    private sealed record W2(long Id, string Name);

    private sealed record W3(long Id, string Name);

    private sealed record W4(long Id, string Name);

    private sealed record W5(long Id, string Name);

    private sealed record W6(long Id, string Name);

    private sealed record W7(long Id, string Name);

    private sealed record W8(long Id, string Name);

    private sealed record W9(long Id, string Name);
}
