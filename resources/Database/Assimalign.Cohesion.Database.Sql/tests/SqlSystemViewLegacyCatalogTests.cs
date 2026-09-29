using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>Verifies metadata projection for catalogs predating physical primary-key indexes.</summary>
public sealed class SqlSystemViewLegacyCatalogTests
{
    /// <summary>A stored primary-key declaration remains visible without inventing a physical index.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - System views: Should report legacy primary keys without inventing indexes or duplicate names")]
    public async Task LegacyPrimaryKey_ShouldReportConstraintAndReferenceWithoutInventingAnIndex()
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "system-legacy" });
        var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("app", cancellationToken: CancellationToken.None);
        await database.Catalog.CreateTableAsync("dbo", "legacy",
        [
            new SqlCatalogColumn("tenant_id", new DatabaseTypeInfo(DatabaseType.Int32), false),
            new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32), false),
            new SqlCatalogColumn("parent_id", new DatabaseTypeInfo(DatabaseType.Int32)),
            new SqlCatalogColumn("parent_tenant", new DatabaseTypeInfo(DatabaseType.Int32)),
        ], ["tenant_id", "id"], cancellationToken: CancellationToken.None);
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);

        (await RowsAsync(session,
            "SELECT CONSTRAINT_NAME, CONSTRAINT_TYPE FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS WHERE TABLE_NAME = 'legacy'"))
            .Single().ShouldBe(new object?[] { "PrimaryKey_legacy", "PRIMARY KEY" });
        var columns = await RowsAsync(session,
            "SELECT COLUMN_NAME, ORDINAL_POSITION FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE WHERE CONSTRAINT_NAME = 'PrimaryKey_legacy' ORDER BY ORDINAL_POSITION");
        columns.Count.ShouldBe(2);
        columns[0].ShouldBe(new object?[] { "tenant_id", 1L });
        columns[1].ShouldBe(new object?[] { "id", 2L });
        (await RowsAsync(session, "SELECT INDEX_NAME FROM COHESION_SCHEMA.INDEXES WHERE TABLE_NAME = 'legacy'"))
            .ShouldBeEmpty();
        (await RowsAsync(session,
            "SELECT OBJECT_NAME FROM COHESION_SCHEMA.OBJECT_OWNERSHIP WHERE TABLE_NAME = 'legacy' AND OBJECT_TYPE = 'INDEX'"))
            .ShouldBeEmpty();

        // The FK binder accepts a reordered target column set. The referenced
        // constraint must resolve to the same declaration, including for legacy PKs.
        await session.ExecuteAsync(
            "ALTER TABLE legacy ADD CONSTRAINT fk_legacy FOREIGN KEY(parent_id, parent_tenant) REFERENCES legacy(id, tenant_id) ON DELETE CASCADE",
            cancellationToken: CancellationToken.None);
        (await RowsAsync(session,
            "SELECT UNIQUE_CONSTRAINT_CATALOG, UNIQUE_CONSTRAINT_SCHEMA, UNIQUE_CONSTRAINT_NAME, DELETE_RULE FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_NAME = 'fk_legacy'"))
            .Single().ShouldBe(new object?[] { "app", "dbo", "PrimaryKey_legacy", "CASCADE" });
        (await RowsAsync(session, "SELECT INDEX_NAME FROM COHESION_SCHEMA.INDEXES WHERE TABLE_NAME = 'legacy'"))
            .ShouldBeEmpty();

        // A separately declared unique index stays a separate constraint even
        // when it happens to enforce the primary key's declared column set and
        // already occupies the legacy fallback name.
        await session.ExecuteAsync("CREATE UNIQUE INDEX PrimaryKey_legacy ON legacy(tenant_id, id)", cancellationToken: CancellationToken.None);
        var keys = await RowsAsync(session,
            "SELECT CONSTRAINT_NAME, CONSTRAINT_TYPE FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS WHERE CONSTRAINT_TYPE <> 'FOREIGN KEY' ORDER BY CONSTRAINT_NAME");
        keys.Count.ShouldBe(2);
        keys[0].ShouldBe(new object?[] { "PrimaryKey_legacy", "UNIQUE" });
        keys[1].ShouldBe(new object?[] { "PrimaryKey_legacy_1", "PRIMARY KEY" });
        (await RowsAsync(session,
            "SELECT DISTINCT INDEX_NAME, IS_PRIMARY_KEY FROM COHESION_SCHEMA.INDEXES WHERE TABLE_NAME = 'legacy'"))
            .Single().ShouldBe(new object?[] { "PrimaryKey_legacy", "NO" });
        (await RowsAsync(session,
            "SELECT UNIQUE_CONSTRAINT_NAME FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_NAME = 'fk_legacy'"))
            .Single().ShouldBe(new object?[] { "PrimaryKey_legacy_1" });
        columns = await RowsAsync(session,
            "SELECT COLUMN_NAME, ORDINAL_POSITION FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE WHERE CONSTRAINT_NAME = 'PrimaryKey_legacy_1' ORDER BY ORDINAL_POSITION");
        columns.Count.ShouldBe(2);
        columns[0].ShouldBe(new object?[] { "tenant_id", 1L });
        columns[1].ShouldBe(new object?[] { "id", 2L });

        // Actual check/FK names also reserve their identifiers; case-insensitive
        // collision detection deterministically advances to the next suffix.
        await session.ExecuteAsync("ALTER TABLE legacy ADD CONSTRAINT primarykey_LEGACY_1 CHECK (id > 0)", cancellationToken: CancellationToken.None);
        (await RowsAsync(session,
            "SELECT CONSTRAINT_NAME FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS WHERE CONSTRAINT_TYPE = 'PRIMARY KEY'"))
            .Single().ShouldBe(new object?[] { "PrimaryKey_legacy_2" });
        (await RowsAsync(session,
            "SELECT UNIQUE_CONSTRAINT_NAME FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_NAME = 'fk_legacy'"))
            .Single().ShouldBe(new object?[] { "PrimaryKey_legacy_2" });
        var usages = await RowsAsync(session,
            "SELECT CONSTRAINT_NAME, COLUMN_NAME, ORDINAL_POSITION FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE WHERE CONSTRAINT_NAME LIKE 'PrimaryKey_legacy%' ORDER BY CONSTRAINT_NAME, ORDINAL_POSITION");
        usages.Count.ShouldBe(4);
        usages[0].ShouldBe(new object?[] { "PrimaryKey_legacy", "tenant_id", 1L });
        usages[1].ShouldBe(new object?[] { "PrimaryKey_legacy", "id", 2L });
        usages[2].ShouldBe(new object?[] { "PrimaryKey_legacy_2", "tenant_id", 1L });
        usages[3].ShouldBe(new object?[] { "PrimaryKey_legacy_2", "id", 2L });
    }

    private static async Task<List<object?[]>> RowsAsync(IDatabaseSession session, string sql)
    {
        await using var result = (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<QueryResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
        }
        return rows;
    }
}
