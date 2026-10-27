using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlDmlSourceBindingTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - DML sources: bare RETURNING star expands visible target and source columns in namespace order (#1714)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Plan_ReturningStar_ShouldPreserveVisibleColumnsAndMetadata(bool delete, bool usingJoin)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("dml-star", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, v INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE s (label VARCHAR(23) COLLATE case_insensitive NOT NULL, id INT NOT NULL)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE u (id INT NOT NULL, amount DECIMAL(9,2) NOT NULL)", cancellationToken: CancellationToken.None);
        string sources = usingJoin ? "s LEFT JOIN u USING (id), s AS q" : "s, u, s AS q";
        string sql = (delete ? "DELETE FROM t USING " : "UPDATE t SET v=s.id FROM ") + sources + " RETURNING *";

        // Act
        var plan = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql(sql).Statement.SqlExpression);
        var returning = (delete ? plan.ShouldBeOfType<SqlDeletePlan>().Returning : plan.ShouldBeOfType<SqlUpdatePlan>().Returning).ShouldNotBeNull();
        await using var explanation = (await session.ExecuteAsync("EXPLAIN " + sql, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var details = new List<string?>();
        await foreach (var row in explanation.GetRowsAsync(CancellationToken.None)) { details.Add(row.GetString(5)); }

        // Assert: USING exposes the merged key first and hides the physical right key.
        string[] names = usingJoin ? ["id", "v", "id", "label", "amount", "label", "id"]
            : ["id", "v", "label", "id", "id", "amount", "label", "id"];
        returning.Select(column => column.Name).ShouldBe(names);
        var source = (delete ? ((SqlDeletePlan)plan).Source : ((SqlUpdatePlan)plan).Source).ShouldNotBeNull();
        source.Associations.Columns.Count.ShouldBe(8);
        source.VisibleOrdinals.ShouldBe(usingJoin ? [0, 1, 3, 2, 5, 6, 7] : [0, 1, 2, 3, 4, 5, 6, 7]);
        if (usingJoin) { returning[2].Value.ShouldBeOfType<SqlBoundColumn>().Ordinal.ShouldBe(3); }
        returning.Select(column => column.Type).ShouldBe(usingJoin
            ? [DatabaseType.Int32, DatabaseType.Int32, DatabaseType.Int32, DatabaseType.String, DatabaseType.Decimal, DatabaseType.String, DatabaseType.Int32]
            : [DatabaseType.Int32, DatabaseType.Int32, DatabaseType.String, DatabaseType.Int32, DatabaseType.Int32, DatabaseType.Decimal, DatabaseType.String, DatabaseType.Int32]);
        var label = returning[usingJoin ? 3 : 2];
        label.MaxLength.ShouldBe(23);
        label.IsNullable.ShouldBeFalse();
        label.Collation.Resolve(null).ShouldBeSameAs(Collation.CaseInsensitive);
        var amount = returning[usingJoin ? 4 : 5];
        amount.IsNullable.ShouldBe(usingJoin);
        details.ShouldContain(detail => detail != null && detail.Contains($"returning={names.Length}; columns={string.Join(", ", names)}"));
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - DML sources: SET and RETURNING bind to physical source slots and retain target identity (#1714)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Plan_SourceColumns_ShouldRetainTargetIdentity(bool delete)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("dml-binding", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT, v INT NOT NULL)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE s (id INT, label VARCHAR(23))", cancellationToken: CancellationToken.None);
        string sql = delete
            ? "DELETE FROM t AS x USING s LEFT JOIN t AS y ON s.id=y.id WHERE x.id=s.id RETURNING *, s.label AS source_label, y.v"
            : "UPDATE t AS x SET v=y.v FROM s LEFT JOIN t AS y ON s.id=y.id WHERE x.id=s.id RETURNING *, s.label AS source_label, y.v";

        // Act
        var plan = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql(sql).Statement.SqlExpression);
        var source = delete ? plan.ShouldBeOfType<SqlDeletePlan>().Source : plan.ShouldBeOfType<SqlUpdatePlan>().Source;
        var returning = delete ? ((SqlDeletePlan)plan).Returning : ((SqlUpdatePlan)plan).Returning;

        // Assert
        source.ShouldNotBeNull();
        source.TargetBinding.ShouldBe(0);
        var join = source.Associations;
        join.Bindings.Count.ShouldBe(3);
        join.Bindings[0].Offset.ShouldBe(0);
        join.Bindings[0].Reference.Alias.ShouldBe("x");
        join.Bindings[2].Reference.Alias.ShouldBe("y");
        join.Bindings[0].Source.Table!.ObjectId.ShouldBe(join.Bindings[2].Source.Table!.ObjectId);
        join.Bindings.Select(binding => binding.Offset).ShouldBe([0, 2, 4]);
        join.Projections.Count.ShouldBe(6);
        join.Where.ShouldNotBeNull();
        returning.ShouldNotBeNull();
        returning.Select(column => column.Name).ShouldBe(["id", "v", "id", "label", "id", "v", "source_label", "v"]);
        returning[1].IsNullable.ShouldBeFalse();
        returning[3].ColumnOrdinal.ShouldBe(3);
        returning[3].Type.ShouldBe(DatabaseType.String);
        returning[3].MaxLength.ShouldBe(23);
        returning[5].ColumnOrdinal.ShouldBe(5);
        returning[5].IsNullable.ShouldBeTrue();
        returning[6].ColumnOrdinal.ShouldBe(3);
        returning[7].ColumnOrdinal.ShouldBe(5);
        if (!delete)
        {
            var assignment = ((SqlUpdatePlan)plan).Assignments.ShouldHaveSingleItem();
            assignment.Ordinal.ShouldBe(1);
            assignment.Value.ShouldBeOfType<SqlBoundColumn>().Ordinal.ShouldBe(5);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - DML sources: invalid namespaces fail before execution, even over empty inputs (#1714)")]
    [InlineData("UPDATE t SET v = id FROM s", "CS01015")]
    [InlineData("UPDATE t SET v = missing FROM s", "CS01015")]
    [InlineData("UPDATE t SET v = s.missing FROM s", "CS01015")]
    [InlineData("UPDATE t SET v = s.v FROM s WHERE id=1", "CS01015")]
    [InlineData("UPDATE t AS x SET v=s.v FROM s WHERE t.id=s.id", "CS01015")]
    [InlineData("UPDATE t SET v=s.v FROM s AS q", "CS01015")]
    [InlineData("UPDATE t SET v=s.v FROM s RETURNING id", "CS01015")]
    [InlineData("UPDATE t SET v=s.v FROM s RETURNING missing.id", "CS01015")]
    [InlineData("UPDATE t SET v=1 FROM t", "CS01019")]
    [InlineData("UPDATE t AS x SET v=1 FROM s AS x", "CS01019")]
    [InlineData("UPDATE t SET v=1 FROM s JOIN s AS q ON t.id=q.id", "CS01015")]
    [InlineData("UPDATE t SET v=1 FROM s, s AS q JOIN s AS z ON s.id=z.id", "CS01015")]
    [InlineData("UPDATE t SET v=q.v FROM (SELECT t.v) AS q", "CS01015")]
    [InlineData("UPDATE t SET v=1 FROM absent", "CS01014")]
    [InlineData("DELETE FROM t USING s WHERE id=1", "CS01015")]
    [InlineData("DELETE FROM t USING s RETURNING id", "CS01015")]
    [InlineData("DELETE FROM t AS x USING s WHERE t.id=s.id", "CS01015")]
    [InlineData("DELETE FROM t USING s RETURNING s.missing", "CS01015")]
    public async Task Plan_InvalidSources_ShouldReportBindingCode(string sql, string code)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("dml-names", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT, v INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE s (id INT, v INT)", cancellationToken: CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));

        // Assert
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(code);
        diagnostic.Start.ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - DML sources: bound forms explain sources and execute (#1714)")]
    [InlineData("UPDATE t SET v=s.v FROM s WHERE t.id=s.id RETURNING s.v")]
    [InlineData("DELETE FROM t USING s WHERE t.id=s.id RETURNING s.v")]
    [InlineData("UPDATE t SET v=q.v FROM (SELECT id,v FROM s) q WHERE t.id=q.id")]
    [InlineData("WITH q AS (SELECT id,v FROM s) UPDATE t SET v=q.v FROM q WHERE t.id=q.id")]
    [InlineData("WITH q AS (SELECT id,v FROM s) DELETE FROM t USING q WHERE t.id=q.id")]
    [InlineData("UPDATE t SET v=s.v FROM s WHERE EXISTS (SELECT 1 WHERE s.id=t.id)")]
    [InlineData("DELETE FROM t USING s JOIN t AS q ON EXISTS (SELECT 1 WHERE s.id=q.id) WHERE t.id=s.id")]
    [InlineData("UPDATE t SET v=1 FROM INFORMATION_SCHEMA.TABLES AS s WHERE s.TABLE_NAME='t'")]
    public async Task ExecuteAsync_BoundSourceForms_ShouldExecuteAndExplain(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("dml-refusal", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT, v INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE s (id INT, v INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1,10)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO s VALUES (1,20)", cancellationToken: CancellationToken.None);

        // Act
        (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(1);
        await using var description = (await session.ExecuteAsync("EXPLAIN " + sql, cancellationToken: CancellationToken.None))
            .ShouldBeAssignableTo<DatabaseResultSet>();
        var targets = new List<string?>();
        await foreach (var row in description.GetRowsAsync(CancellationToken.None)) { targets.Add(row.GetString(3)); }

        // Assert
        targets.ShouldContain("dbo.t");
        targets.ShouldContain(sql.Contains("INFORMATION_SCHEMA") ? "INFORMATION_SCHEMA.TABLES" : "dbo.s");
        await using var result = (await session.ExecuteAsync("SELECT v FROM t", cancellationToken: CancellationToken.None))
            .ShouldBeAssignableTo<DatabaseResultSet>();
        var values = new List<int>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { values.Add(row.GetInt32(0)); }
        values.ShouldBe(sql.Contains("DELETE") ? [] : sql.Contains("INFORMATION_SCHEMA") ? [1] : [20]);
    }
}
