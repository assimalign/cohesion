using System;
using System.IO;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlSchemaConstraintProvisioningTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "cohesion-b2-schema", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CompiledConstraints_ShouldProvisionEnforceAndSurviveRestart()
    {
        var schema = CreateSchema();
        await using (var engine = CreateEngine())
        {
            var database = await engine.CreateDatabaseAsync("app");
            var provisioner = database.ShouldBeAssignableTo<IDatabaseSchemaProvisioner>();
            (await provisioner.ApplySchemaAsync(schema)).WasAlreadyApplied.ShouldBeFalse();
            (await provisioner.ApplySchemaAsync(schema)).WasAlreadyApplied.ShouldBeTrue();
            await using var session = await database.CreateSessionAsync();

            var orphan = await Should.ThrowAsync<SqlConstraintViolationException>(async () =>
                await session.ExecuteAsync("INSERT INTO a_child VALUES (1, 99, 1, 'a')"));
            orphan.ConstraintName.ShouldBe("fk_parent");
            orphan.Table.ShouldBe("dbo.a_child");
            await session.ExecuteAsync("INSERT INTO z_parent VALUES (7)");
            await session.ExecuteAsync("INSERT INTO a_child VALUES (1, 7, 2, 'a')");
            var invalidQuantity = await Should.ThrowAsync<SqlConstraintViolationException>(async () =>
                await session.ExecuteAsync("INSERT INTO a_child VALUES (2, 7, 0, 'b')"));
            invalidQuantity.ConstraintName.ShouldBe("ck_qty");
            var duplicate = await Should.ThrowAsync<SqlConstraintViolationException>(async () =>
                await session.ExecuteAsync("INSERT INTO a_child VALUES (2, 7, 1, 'a')"));
            duplicate.ConstraintName.ShouldBe("uq_email");
            await Should.ThrowAsync<DatabaseObjectLockedException>(async () =>
                await session.ExecuteAsync("ALTER TABLE a_child DROP CONSTRAINT fk_parent"));
        }

        await using (var reopenedEngine = CreateEngine())
        {
            var database = await reopenedEngine.OpenDatabaseAsync("app");
            (await database.ShouldBeAssignableTo<IDatabaseSchemaProvisioner>().ApplySchemaAsync(schema))
                .WasAlreadyApplied.ShouldBeTrue();
            await using var session = await database.CreateSessionAsync();
            (await CountAsync(session, "a_child")).ShouldBe(1);
            await Should.ThrowAsync<SqlConstraintViolationException>(async () =>
                await session.ExecuteAsync("UPDATE a_child SET parent_id = 999 WHERE id = 1"));
            await Should.ThrowAsync<SqlConstraintViolationException>(async () =>
                await session.ExecuteAsync("UPDATE a_child SET qty = -1 WHERE id = 1"));
            await session.ExecuteAsync("DELETE FROM z_parent WHERE id = 7");
            (await CountAsync(session, "a_child")).ShouldBe(0);
        }
    }

    [Fact]
    public async Task CompiledCyclicReferences_ShouldProvisionAfterAllTablesExist()
    {
        var left = new CompiledSchemaTable("a_left", "Tests.Left",
            [new("id", DatabaseType.Int32, false), new("right_id", DatabaseType.Int32, true)],
            new CompiledSchemaKey("pk_left", ["id"]), [],
            [new CompiledSchemaConstraint("fk_right", CompiledSchemaConstraintKind.Reference,
                ["right_id"], "z_right", ["id"], OnDelete: CompiledSchemaReferentialAction.Cascade)]);
        var right = new CompiledSchemaTable("z_right", "Tests.Right",
            [new("id", DatabaseType.Int32, false), new("left_id", DatabaseType.Int32, true)],
            new CompiledSchemaKey("pk_right", ["id"]), [],
            [new CompiledSchemaConstraint("fk_left", CompiledSchemaConstraintKind.Reference,
                ["left_id"], "a_left", ["id"], OnDelete: CompiledSchemaReferentialAction.Cascade)]);
        var schema = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "app", EngineModel.Sql, false,
            [], [left, right], [], [], [], []);

        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("app");
        await database.ShouldBeAssignableTo<IDatabaseSchemaProvisioner>().ApplySchemaAsync(schema);
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("INSERT INTO a_left VALUES (1, NULL)");
        await session.ExecuteAsync("INSERT INTO z_right VALUES (2, 1)");
        await session.ExecuteAsync("UPDATE a_left SET right_id = 2 WHERE id = 1");
        await session.ExecuteAsync("DELETE FROM a_left WHERE id = 1");
        (await CountAsync(session, "a_left")).ShouldBe(0);
        (await CountAsync(session, "z_right")).ShouldBe(0);
    }

    [Fact]
    public void Renderer_ShouldRenderForeignKeyThatPreviouslyFailedClosed()
    {
        string sql = SqlSchemaStatementRenderer.CreateTable(CreateSchema().Tables[0]);
        sql.ShouldContain("CONSTRAINT fk_parent FOREIGN KEY (parent_id) REFERENCES dbo.z_parent (id) ON DELETE CASCADE");
        sql.ShouldContain("CONSTRAINT ck_qty CHECK (qty > 0)");
        Should.NotThrow(() => SqlQueryRequest.FromSql(sql));
    }

    private static SqlCompiledSchema CreateSchema()
        => new(SqlCompiledSchema.CurrentFormat, "app", EngineModel.Sql, false, [],
            [
                new CompiledSchemaTable("a_child", "Tests.Child",
                    [new("id", DatabaseType.Int32, false), new("parent_id", DatabaseType.Int32, false),
                     new("qty", DatabaseType.Int32, false), new("email", DatabaseType.String, false)],
                    new CompiledSchemaKey("pk_child", ["id"]),
                    [new CompiledSchemaIndex("uq_email", ["email"], true)],
                    [new CompiledSchemaConstraint("fk_parent", CompiledSchemaConstraintKind.Reference,
                        ["parent_id"], "z_parent", ["id"], OnDelete: CompiledSchemaReferentialAction.Cascade),
                     new CompiledSchemaConstraint("ck_qty", CompiledSchemaConstraintKind.Check,
                        ["qty"], null, [], new CompiledSchemaExpression("qty > 0"))]),
                new CompiledSchemaTable("z_parent", "Tests.Parent", [new("id", DatabaseType.Int32, false)],
                    new CompiledSchemaKey("pk_parent", ["id"]), [], []),
            ], [], [], [], []);

    private SqlDatabaseEngine CreateEngine()
        => SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "schema-constraints", RootPath = _rootPath });

    private static async Task<long> CountAsync(IDatabaseSession session, string table)
    {
        await using var result = (await session.ExecuteAsync($"SELECT COUNT(*) FROM {table}"))
            .ShouldBeAssignableTo<QueryResultSet>();
        await foreach (var row in result.GetRowsAsync())
        {
            return row.GetValue(0).ShouldBeOfType<long>();
        }
        throw new InvalidOperationException("COUNT returned no row.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }
}
