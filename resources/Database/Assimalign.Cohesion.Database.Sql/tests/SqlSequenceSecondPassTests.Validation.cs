using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSequenceSecondPassTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Persisted sequence refusal: each expression node is visited once")]
    [InlineData(64)][InlineData(256)]
    public void RefusePersisted_DeepView_ShouldVisitEachNodeOnce(int depth)
    {
        // Arrange
        string expression = string.Join(" + ", Enumerable.Repeat("1", depth + 1));
        var parsed = (SqlQueryStatement)new SqlQueryParser(new SqlQueryParserOptions { ExpressionNestingLimit = 1024 }).Parse("CREATE VIEW v AS SELECT " + expression + " AS value");
        parsed.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
        var view = (SqlCreateViewExpression)parsed.SqlExpression;
        int expected = SqlViewQuery.Nodes(view.Query).Count();
        int visits = 0;
        SqlSequenceExecution.PersistedNodeVisited = () => visits++;
        try
        {
            // Act
            SqlSequenceExecution.RefusePersistedStatement(view);

            // Assert
            _output.WriteLine($"depth={depth}; nodes={expected}; refusal visits={visits}");
            visits.ShouldBe(expected);
        }
        finally { SqlSequenceExecution.PersistedNodeVisited = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - NEXT VALUE FOR: RETURNING refuses before any sequence or row mutation")]
    [InlineData("INSERT INTO t VALUES (NEXT VALUE FOR s) RETURNING NEXT VALUE FOR s")]
    [InlineData("UPDATE t SET id = NEXT VALUE FOR s RETURNING id, NEXT VALUE FOR s")]
    [InlineData("DELETE FROM t RETURNING NEXT VALUE FOR s")]
    [InlineData("UPDATE t SET id = NEXT VALUE FOR s FROM u WHERE t.id=u.k RETURNING NEXT VALUE FOR s")]
    [InlineData("DELETE FROM t USING u WHERE t.id=u.k RETURNING NEXT VALUE FOR s")]
    [InlineData("MERGE INTO t USING u ON t.id=u.k WHEN MATCHED THEN UPDATE SET id=NEXT VALUE FOR s RETURNING NEXT VALUE FOR s")]
    public async Task Plan_NextValueReturning_ShouldRefuseAtPlanning(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("second-pass", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE SEQUENCE s");
        await Exec(session, "CREATE TABLE t(id BIGINT)");
        await Exec(session, "CREATE TABLE u(k BIGINT)");
        await Exec(session, "INSERT INTO t VALUES (10),(20),(30)");
        await Exec(session, "INSERT INTO u VALUES (10),(20),(30)");

        // Act / Assert
        var error = await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync(sql, cancellationToken: CancellationToken.None).AsTask());
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03076");
        (await Rows(session, "SELECT nextval('s')"))[0][0].ShouldBe(1L);
        (await Rows(session, "SELECT id FROM t ORDER BY id")).Select(r => r[0]).ShouldBe(new object?[] { 10L, 20L, 30L });
    }

}
