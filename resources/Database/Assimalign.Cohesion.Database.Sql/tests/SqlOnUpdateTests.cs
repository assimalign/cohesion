using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlOnUpdateTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "cohesion-on-update", Guid.NewGuid().ToString("N"));

    public static IEnumerable<object[]> Actions()
    {
        foreach (string action in new[] { "CASCADE", "SET NULL", "SET DEFAULT" })
        {
            for (int path = 0; path < 4; path++)
            {
                yield return [action, path, false];
                yield return [action, path, true];
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ON UPDATE: every update path executes changing actions atomically (#1723)")]
    [MemberData(nameof(Actions))]
    public async Task ExecuteAsync_ChangingReferencedKey_ShouldApplyActions(string action, int path, bool referenced)
    {
        // Arrange: a preceding non-key target and MERGE insert expose premature writes.
        await using var engine = SqlDatabaseEngine.Create("on-update", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE p(id INT PRIMARY KEY, v INT)");
        await ExecuteAsync(session, $"CREATE TABLE c(pid INT DEFAULT 1 REFERENCES p(id) ON UPDATE {action})");
        await ExecuteAsync(session, "INSERT INTO p VALUES(1,0),(2,0)");
        if (referenced) { await ExecuteAsync(session, "INSERT INTO c VALUES(2)"); }
        string sql = path switch
        {
            0 => "UPDATE p SET id=CASE WHEN id=2 THEN 20 ELSE id END,v=1",
            1 => "UPDATE p SET id=s.next_id,v=1 FROM (SELECT 1 AS old_id,1 AS next_id UNION ALL SELECT 2,20) s WHERE p.id=s.old_id",
            2 => "INSERT INTO p VALUES(1,1),(2,1) ON CONFLICT(id) DO UPDATE SET id=CASE WHEN p.id=2 THEN 20 ELSE p.id END,v=excluded.v",
            _ => "MERGE INTO p USING (SELECT 3 AS id UNION ALL SELECT 1 UNION ALL SELECT 2) s ON p.id=s.id WHEN MATCHED THEN UPDATE SET id=CASE WHEN p.id=2 THEN 20 ELSE p.id END,v=1 WHEN NOT MATCHED THEN INSERT VALUES(s.id,1)",
        };

        // Act
        await ExecuteAsync(session, sql);

        // Assert
        (await RowsAsync(session, "SELECT id,v FROM p WHERE id<>3 ORDER BY id")).ShouldBe([[1, 1], [20, 1]]);
        object? expected = action switch { "CASCADE" => 20, "SET DEFAULT" => 1, _ => null };
        (await RowsAsync(session, "SELECT pid FROM c")).ShouldBe(referenced ? [[expected]] : []);
        // Unchanged referenced keys and other-column changes execute normally.
        await ExecuteAsync(session, "UPDATE p SET id=id,v=3");
        (await RowsAsync(session, "SELECT v FROM p ORDER BY id")).ShouldBe(path == 3 ? [[3], [3], [3]] : [[3], [3]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ON UPDATE: self-referencing updates execute every action (#1723)")]
    [MemberData(nameof(Actions))]
    public async Task ExecuteAsync_SelfReference_ShouldApplyActions(string action, int path, bool referenced)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("on-update-self", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, $"CREATE TABLE t(id INT PRIMARY KEY,pid INT REFERENCES t(id) ON UPDATE {action})");
        await ExecuteAsync(session, referenced ? "INSERT INTO t VALUES(1,1)" : "INSERT INTO t VALUES(1,NULL)");
        string sql = path switch
        {
            0 => "UPDATE t SET id=2",
            1 => "UPDATE t SET id=s.next_id FROM (SELECT 2 AS next_id) s",
            2 => "INSERT INTO t VALUES(1,NULL) ON CONFLICT(id) DO UPDATE SET id=2",
            _ => "MERGE INTO t USING (SELECT 1 AS id) s ON t.id=s.id WHEN MATCHED THEN UPDATE SET id=2",
        };

        // Act
        await ExecuteAsync(session, sql);

        // Assert
        (await RowsAsync(session, "SELECT id,pid FROM t")).ShouldBe(referenced && action == "CASCADE" ? [[2, 2]] : [[2, null]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ON UPDATE: changing actions still execute when another parent supplies the old key (#1723)")]
    [InlineData("CASCADE")]
    [InlineData("SET NULL")]
    [InlineData("SET DEFAULT")]
    public async Task ExecuteAsync_FinalParentSubstitution_ShouldApplyChangingAction(string action)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("on-update-swap", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE p(id INT PRIMARY KEY)");
        await ExecuteAsync(session, $"CREATE TABLE c(pid INT REFERENCES p(id) ON UPDATE {action})");
        await ExecuteAsync(session, "INSERT INTO p VALUES(1),(2)");
        await ExecuteAsync(session, "INSERT INTO c VALUES(1)");

        // Act
        await ExecuteAsync(session, "UPDATE p SET id=3-id");

        // Assert
        (await RowsAsync(session, "SELECT id FROM p ORDER BY id")).ShouldBe([[1], [2]]);
        (await RowsAsync(session, "SELECT pid FROM c")).ShouldBe(action == "CASCADE" ? [[2]] : [[null]]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - ON UPDATE: changing only compiled action metadata replaces the FK and reconciles (#1723)")]
    public async Task ApplySchemaAsync_ChangedUpdateAction_ShouldReplaceConstraint()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("on-update-migration", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await database.ApplySchemaAsync(Schema(CompiledSchemaReferentialAction.NoAction), CancellationToken.None);
        var desired = Schema(CompiledSchemaReferentialAction.SetDefault);

        // Act
        var result = await database.ApplySchemaAsync(desired, CancellationToken.None);
        await database.VerifySchemaAsync(desired, CancellationToken.None);
        var repeat = await database.ApplySchemaAsync(desired, CancellationToken.None);

        // Assert
        result.OperationCount.ShouldBe(2);
        repeat.WasAlreadyApplied.ShouldBeTrue();
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        (await RowsAsync(session, "SELECT UPDATE_RULE FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS")).ShouldBe([["SET DEFAULT"]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ON UPDATE: every action persists through rename, metadata views and reopen (#1723)")]
    [InlineData("CASCADE", SqlCatalogReferentialAction.Cascade)]
    [InlineData("SET NULL", SqlCatalogReferentialAction.SetNull)]
    [InlineData("SET DEFAULT", SqlCatalogReferentialAction.SetDefault)]
    [InlineData("RESTRICT", SqlCatalogReferentialAction.Restrict)]
    [InlineData("NO ACTION", SqlCatalogReferentialAction.NoAction)]
    public async Task OpenAsync_CatalogAction_ShouldRetainMetadata(string action, SqlCatalogReferentialAction expected)
    {
        // Arrange
        await using (var engine = CreateEngine())
        {
            var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await ExecuteAsync(session, "CREATE TABLE p(id INT PRIMARY KEY)");
            await ExecuteAsync(session, $"CREATE TABLE c(pid INT, CONSTRAINT fk FOREIGN KEY(pid) REFERENCES p(id) ON UPDATE {action} ON DELETE SET NULL)");
            await ExecuteAsync(session, "ALTER TABLE p RENAME TO parent");
            await ExecuteAsync(session, "ALTER TABLE c RENAME COLUMN pid TO parent_id");
            await ExecuteAsync(session, "CREATE VIEW action_rules AS SELECT UPDATE_RULE,DELETE_RULE FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_NAME='fk'");
        }

        // Act
        await using var reopened = CreateEngine();
        var opened = await reopened.OpenDatabaseAsync("app", CancellationToken.None);
        await using var query = await opened.CreateSessionAsync(CancellationToken.None);

        // Assert
        opened.Catalog.RecordSpaceFormatVersion.ShouldBe(33);
        opened.Catalog.TryGetTable("dbo", "c", out var child).ShouldBeTrue();
        child.Constraints.Single().OnUpdate.ShouldBe(expected);
        child.Constraints.Single().OnDelete.ShouldBe(SqlCatalogReferentialAction.SetNull);
        child.Constraints.Single().ReferencedTable.ShouldBe("parent");
        child.Constraints.Single().Columns.ShouldBe(["parent_id"]);
        (await RowsAsync(query, "SELECT UPDATE_RULE,DELETE_RULE FROM action_rules")).ShouldBe([[action, "SET NULL"]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ON UPDATE: compiled public declarations provision and survive reopen without drift (#1723)")]
    [InlineData(CompiledSchemaReferentialAction.Restrict, "RESTRICT")]
    [InlineData(CompiledSchemaReferentialAction.Cascade, "CASCADE")]
    [InlineData(CompiledSchemaReferentialAction.NoAction, "NO ACTION")]
    [InlineData(CompiledSchemaReferentialAction.SetNull, "SET NULL")]
    [InlineData(CompiledSchemaReferentialAction.SetDefault, "SET DEFAULT")]
    public async Task ApplySchemaAsync_UpdateAction_ShouldPersistAndVerify(CompiledSchemaReferentialAction action, string rule)
    {
        // Arrange
        var schema = Schema(action);
        string artifact = Path.Combine(_rootPath, "schema.json");
        await SqlCompiledSchemaSerializer.WriteAsync(artifact, schema, CancellationToken.None);
        await using (var engine = CreateEngine())
        {
            var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
            await database.ApplySchemaAsync(schema, CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            (await RowsAsync(session, "SELECT UPDATE_RULE FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS")).ShouldBe([[rule]]);
        }

        // Act
        var loaded = await SqlCompiledSchemaSerializer.ReadAsync(artifact, CancellationToken.None);
        await using var reopened = CreateEngine();
        var opened = await reopened.OpenDatabaseAsync("app", CancellationToken.None);
        await opened.VerifySchemaAsync(loaded, CancellationToken.None);
        var result = await opened.ApplySchemaAsync(loaded, CancellationToken.None);

        // Assert
        loaded.Tables[0].Constraints.Single().OnUpdate.ShouldBe(action);
        loaded.Hash.ShouldBe(schema.Hash);
        result.WasAlreadyApplied.ShouldBeTrue();
        result.OperationCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - ON UPDATE: declared scripts carry actions through persisted views and reconciliation (#1723)")]
    public async Task BuildAsync_DeclaredScript_ShouldRetainUpdateClause()
    {
        // Arrange
        SqlDatabaseEngineBuilder Builder()
        {
            var builder = SqlDatabaseEngine.CreateBuilder("on-update");
            builder.Options.RootPath = _rootPath;
            builder.AddDatabase("app", declaration => declaration.Script("001", "CREATE TABLE p(id INT PRIMARY KEY); CREATE TABLE c(pid INT REFERENCES p(id) ON DELETE CASCADE ON UPDATE SET DEFAULT); CREATE VIEW rules AS SELECT UPDATE_RULE FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS;"));
            return builder;
        }
        await using (var first = await Builder().BuildAsync(CancellationToken.None)) { }

        // Act
        await using var reopened = await Builder().BuildAsync(CancellationToken.None);
        var database = await reopened.OpenDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Assert
        (await RowsAsync(session, "SELECT UPDATE_RULE FROM rules")).ShouldBe([["SET DEFAULT"]]);
        database.Catalog.TryGetTable("dbo", "c", out var child).ShouldBeTrue();
        child.Constraints.Single().OnUpdate.ShouldBe(SqlCatalogReferentialAction.SetDefault);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - ON UPDATE: format 29 with foreign keys is refused without modifying files (#1723)")]
    public async Task OpenAsync_Format29ForeignKey_ShouldRefuseWithoutWrites()
    {
        // Arrange
        await using (var engine = CreateEngine())
        {
            var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await ExecuteAsync(session, "CREATE TABLE p(id INT PRIMARY KEY)");
            await ExecuteAsync(session, "CREATE TABLE c(pid INT REFERENCES p(id))");
            await database.Catalog.SetRecordSpaceFormatVersionAsync(29, CancellationToken.None);
        }
        using (var storage = new FileSystemSqlStorageStrategy(_rootPath).OpenStorage("app", catalog: true))
        {
            (Assimalign.Cohesion.Database.Storage.PageId PageId, int SlotIndex)? location = null;
            long objectId = 0;
            using (var iterator = storage.GetUnitIterator())
            {
                while (iterator.MoveNext())
                {
                    var unit = iterator.Current;
                    var reader = new DatabaseKeyReader(unit.Data.Span);
                    if (reader.ReadInt32() != 1) { continue; }
                    long id = reader.ReadInt64();
                    _ = reader.ReadString(out _);
                    if (reader.ReadString(out _) != "c") { continue; }
                    location = (unit.PageId, unit.SlotIndex);
                    objectId = id;
                    break;
                }
            }
            var target = location.ShouldNotBeNull();
            var old = new DatabaseKeyWriter();
            old.AppendInt32(1).AppendInt64(objectId).AppendString("dbo", Collation.Binary).AppendString("c", Collation.Binary)
                .AppendInt32(1).AppendString("pid", Collation.Binary).AppendInt8((sbyte)DatabaseType.Int32)
                .AppendInt32(-1).AppendInt32(-1).AppendInt32(-1).AppendBoolean(true).AppendNull().AppendInt32(0)
                .AppendInt8((sbyte)DatabaseObjectOwner.Adhoc).AppendNull().AppendInt32(7).AppendInt32(1)
                .AppendString("fk_old", Collation.Binary).AppendInt8((sbyte)SqlCatalogConstraintKind.Reference)
                .AppendInt32(1).AppendString("pid", Collation.Binary).AppendString("dbo", Collation.Binary)
                .AppendString("p", Collation.Binary).AppendInt32(1).AppendString("id", Collation.Binary)
                .AppendNull().AppendInt8((sbyte)SqlCatalogReferentialAction.NoAction)
                .AppendInt32(-1).AppendInt32(0).AppendNull().AppendInt32(0).AppendInt32(0)
                .AppendBoolean(false).AppendNull().AppendBoolean(false);
            using var transaction = storage.BeginTransaction();
            storage.UpdateRow(transaction, target.PageId, target.SlotIndex, old.ToArray());
            transaction.Commit();
        }
        var before = Directory.GetFiles(_rootPath, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);

        // Act
        await using var reopened = CreateEngine();
        var error = await Should.ThrowAsync<DatabaseException>(async () => await reopened.OpenDatabaseAsync("app", CancellationToken.None));

        // Assert
        error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS06041");
        error.Message.ShouldContain("data-storage format 29", Case.Sensitive);
        error.Message.ShouldContain("supports only format 33", Case.Sensitive);
        Directory.GetFiles(_rootPath, "*", SearchOption.AllDirectories).ShouldBe(before.Keys, ignoreOrder: true);
        foreach (var file in before) { File.ReadAllBytes(file.Key).ShouldBe(file.Value); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ON UPDATE: v8 FK applied and pending identities fail before apply, verify or open (#1723)")]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task ProvisionAsync_Format8ForeignKey_ShouldRefuseBeforeIdentityComparison(bool pending, int path)
    {
        // Arrange
        var schema = Schema(CompiledSchemaReferentialAction.NoAction);
        string document = schema.CanonicalDocument.Replace(SqlCompiledSchema.CurrentFormat, "cohesion/database-schema/v8", StringComparison.Ordinal)
            .Replace(",\"onUpdate\":2", "", StringComparison.Ordinal);
        string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(document)));
        await using (var engine = CreateEngine())
        {
            var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
            await database.ApplySchemaAsync(schema, CancellationToken.None);
            if (pending)
            {
                await database.Catalog.SavePendingMigrationAsync(new(hash, document,
                    [new("AddIndex", "c", "ix_pid", 1)], 1), CancellationToken.None);
            }
            else
            {
                await database.Catalog.SaveSchemaStateAsync(new(hash, document, database.Catalog.DdlGeneration), CancellationToken.None);
            }
            ulong generation = database.Catalog.DdlGeneration;
            if (path != 2)
            {
                // Act
                var error = await Should.ThrowAsync<SqlSchemaValidationException>(async () =>
                {
                    if (path == 0) { await database.ApplySchemaAsync(schema, CancellationToken.None); }
                    else { await database.VerifySchemaAsync(schema, CancellationToken.None); }
                });

                // Assert
                AssertFormat8(error);
                database.Catalog.DdlGeneration.ShouldBe(generation);
                database.Catalog.Tables.Count.ShouldBe(2);
            }
        }
        if (path == 2)
        {
            // Act
            await using var reopened = CreateEngine();
            var error = await Should.ThrowAsync<SqlSchemaValidationException>(async () => await reopened.OpenDatabaseAsync("app", CancellationToken.None));

            // Assert
            AssertFormat8(error);
        }
    }

    private static void AssertFormat8(SqlSchemaValidationException error)
    {
        error.Errors.ShouldHaveSingleItem().Declaration.ShouldBe("schema.format");
        error.Errors[0].Code.ShouldBe(SqlSchemaValidationErrorCode.InvalidDocument);
        error.Message.ShouldContain("cohesion/database-schema/v8", Case.Sensitive);
        error.Message.ShouldContain("cohesion/database-schema/v11", Case.Sensitive);
        error.Message.ShouldContain("Rebuild", Case.Sensitive);
    }

    private static SqlCompiledSchema Schema(CompiledSchemaReferentialAction action) => new(SqlCompiledSchema.CurrentFormat, "app", false, [],
        [new("c", "Child", [new("pid", DatabaseType.Int32, true)], null, [],
            [new("fk", CompiledSchemaConstraintKind.Reference, ["pid"], "p", ["id"], OnUpdate: action)]),
         new("p", "Parent", [new("id", DatabaseType.Int32, false)], new("pk", ["id"]), [], [])], []);

    private SqlDatabaseEngine CreateEngine() => SqlDatabaseEngine.Create("on-update", new() { RootPath = _rootPath });
    private static async Task ExecuteAsync(SqlDatabaseSession session, string sql)
        => (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).Status.ShouldBe(DatabaseResultStatus.Success);
    private static async Task<object?[][]> RowsAsync(SqlDatabaseSession session, string sql)
    {
        var result = (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
        }
        return rows.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath)) { Directory.Delete(_rootPath, recursive: true); }
    }
}
