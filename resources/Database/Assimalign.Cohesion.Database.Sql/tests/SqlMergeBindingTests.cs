using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlMergeBindingTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - MERGE: unsafe FULL associations refuse during planning, including EXPLAIN (#1715)")]
    [InlineData("t.id > s.id")]
    [InlineData("TRUE")]
    [InlineData("t.id = s.id OR t.id = 1")]
    [InlineData("t.id = s.id AND t.id / 0 = 1")]
    [InlineData("t.id = s.id AND EXISTS(SELECT 1)")]
    [InlineData("t.id = s.id AND ABS(s.v) = 1")]
    public async Task Plan_UnsafeFullAssociation_ShouldRefuseBeforeReadsAsync(string condition)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("merge-full-refusal", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SetupAsync(session);
        string sql = "MERGE INTO t USING s ON " + condition +
            " WHEN NOT MATCHED THEN INSERT VALUES(s.id,s.v) WHEN NOT MATCHED BY SOURCE THEN DELETE";

        foreach (string prefix in new[] { "", "EXPLAIN " })
        {
            // Act
            var failure = await Should.ThrowAsync<DatabaseException>(async () =>
                await session.ExecuteAsync(prefix + sql, cancellationToken: CancellationToken.None));

            // Assert
            var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
            diagnostic.Code.ShouldBe("CS05070");
            session.LastStatementMetrics!.RecordsExamined.ShouldBe(0);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - MERGE: unreachable diagnostics identify the complete later WHEN clause (#1715)")]
    [InlineData("MATCHED", "DELETE", false)]
    [InlineData("MATCHED", "DELETE", true)]
    [InlineData("NOT MATCHED", "DO NOTHING", false)]
    [InlineData("NOT MATCHED", "DO NOTHING", true)]
    [InlineData("NOT MATCHED BY SOURCE", "DELETE", false)]
    [InlineData("NOT MATCHED BY SOURCE", "DELETE", true)]
    public async Task Plan_UnreachableWhen_ShouldLocateClauseAsync(string kind, string action, bool conditional)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("merge-when-location", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SetupAsync(session);
        string prefix = "MERGE INTO t USING s ON t.id = s.id WHEN " + kind + " THEN " + action + " ";
        string clause = "WHEN " + kind + (conditional ? " AND TRUE" : "") + " THEN DO NOTHING";
        string sql = prefix + clause + " RETURNING t.id;";

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));

        // Assert
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CS05071");
        diagnostic.Start.ShouldBe(prefix.Length);
        diagnostic.End.ShouldBe(prefix.Length + clause.Length);
        sql[diagnostic.Start!.Value..diagnostic.End!.Value].ShouldBe(clause);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - MERGE: preserved sides determine the association tree (#1715)")]
    [InlineData("WHEN MATCHED THEN DELETE", SqlJoinType.Inner)]
    [InlineData("WHEN NOT MATCHED THEN INSERT (id,v) VALUES(s.id,s.v)", SqlJoinType.RightOuter)]
    [InlineData("WHEN NOT MATCHED BY SOURCE THEN DELETE", SqlJoinType.LeftOuter)]
    [InlineData("WHEN NOT MATCHED THEN INSERT DEFAULT VALUES WHEN NOT MATCHED BY SOURCE THEN DELETE", SqlJoinType.FullOuter)]
    [InlineData("WHEN NOT MATCHED THEN DO NOTHING WHEN NOT MATCHED BY SOURCE THEN DO NOTHING", SqlJoinType.Inner)]
    public async Task Plan_MatchKinds_ShouldChooseAssociationTreeAsync(string actions, SqlJoinType kind)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("merge-shape", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SetupAsync(session);
        string sql = "MERGE INTO t USING s ON t.id=s.id " + actions + " RETURNING t.id,s.v,merge_action() AS action,UPPER(merge_action()) AS upper_action";

        // Act
        var plan = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql(sql).Statement.SqlExpression).ShouldBeOfType<SqlMergePlan>();
        await using var description = (await session.ExecuteAsync("EXPLAIN " + sql, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var details = new List<string?>();
        await foreach (var row in description.GetRowsAsync(CancellationToken.None)) { details.Add(row.GetString(5)); }

        // Assert
        var source = plan.Source;
        source.TargetBinding.ShouldBe(0);
        source.Associations.Bindings.Select(binding => binding.Offset).ShouldBe([0, 2]);
        source.Associations.Tree!.Step!.Kind.ShouldBe(kind);
        source.Associations.Tree.Left!.Binding.ShouldBe(kind == SqlJoinType.RightOuter ? 1 : 0);
        plan.ReturningActionOrdinal.ShouldBe(4);
        plan.Returning!.Select(projection => projection.Type).ShouldBe([DatabaseType.Int32, DatabaseType.Int32, DatabaseType.String, DatabaseType.String]);
        plan.Returning![0].IsNullable.ShouldBeFalse();
        plan.Returning![2].Value.ShouldBeOfType<SqlBoundSlot>().Ordinal.ShouldBe(4);
        var inputRow = new object?[] { 1, 2, 3, 4, "UPDATE" };
        SqlExpressionEvaluator.ForExecution(null).Evaluate(plan.Returning![3].Value!, inputRow).ShouldBe("UPDATE");
        details.ShouldContain(detail => detail != null && detail.Contains("actions=written_order"));
        details.Count(detail => detail != null && detail.StartsWith("order=")).ShouldBe(plan.Actions.Count);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - MERGE: invalid names, types and action contexts fail binding on empty tables (#1715)")]
    [InlineData("ON t.id=s.id WHEN MATCHED THEN UPDATE SET v=id", "CS01015")]
    [InlineData("ON t.id=s.id WHEN NOT MATCHED AND t.id>0 THEN DO NOTHING", "CS01015")]
    [InlineData("ON t.id=s.id WHEN NOT MATCHED THEN INSERT (v) VALUES(t.v)", "CS01015")]
    [InlineData("ON t.id=s.id WHEN NOT MATCHED BY SOURCE AND s.id>0 THEN DELETE", "CS01015")]
    [InlineData("ON t.id=s.id WHEN NOT MATCHED BY SOURCE THEN UPDATE SET v=s.v", "CS01015")]
    [InlineData("ON t.id=s.id WHEN MATCHED THEN UPDATE SET missing=1", "CS01015")]
    [InlineData("ON t.id=s.id WHEN MATCHED THEN UPDATE SET v=1,V=2", "CS01019")]
    [InlineData("ON t.id=s.id WHEN NOT MATCHED THEN INSERT (v,V) VALUES(1,2)", "CS01019")]
    [InlineData("ON t.id=s.id WHEN NOT MATCHED THEN INSERT (id,v) VALUES(1)", "CS05013")]
    [InlineData("ON t.id=s.id WHEN MATCHED THEN DELETE WHEN MATCHED AND s.id>0 THEN DO NOTHING", "CS05071")]
    [InlineData("ON t.id=s.id WHEN NOT MATCHED THEN DO NOTHING WHEN NOT MATCHED BY TARGET THEN INSERT DEFAULT VALUES", "CS05071")]
    [InlineData("ON 1 WHEN MATCHED THEN DELETE", "CS02003")]
    [InlineData("ON t.id=s.id WHEN MATCHED AND s.v THEN DELETE", "CS02003")]
    [InlineData("ON t.id=s.id WHEN MATCHED THEN UPDATE SET v=X'01'", "CS02003")]
    [InlineData("ON t.id=s.id WHEN NOT MATCHED THEN INSERT(v) VALUES(X'01')", "CS02003")]
    [InlineData("ON t.id=s.id WHEN MATCHED THEN UPDATE SET v=SUM(s.v)", "CS05017")]
    [InlineData("ON t.id=s.id WHEN MATCHED THEN UPDATE SET v=@missing", "CS01020")]
    [InlineData("ON t.id=s.id WHEN MATCHED THEN DELETE RETURNING missing", "CS01015")]
    [InlineData("ON t.id=s.id WHEN MATCHED THEN DELETE RETURNING merge_action(1)", "CS01006")]
    [InlineData("ON t.id=s.id WHEN MATCHED AND merge_action()='DELETE' THEN DELETE", "CS01053")]
    [InlineData("ON t.id=s.id WHEN NOT MATCHED AND EXISTS(SELECT t.id) THEN DO NOTHING", "CS01015")]
    [InlineData("ON t.id=s.id WHEN NOT MATCHED BY SOURCE AND EXISTS(SELECT s.id) THEN DO NOTHING", "CS01015")]
    public async Task Plan_InvalidMerge_ShouldReportLocatedCodeAsync(string tail, string code)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("merge-invalid", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SetupAsync(session);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("MERGE INTO t USING s " + tail, cancellationToken: CancellationToken.None));

        // Assert
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(code);
        diagnostic.Start.ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - MERGE: safe FULL associations bind and execution refuses without writes (#1715)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_BoundMerge_ShouldRefuseBeforeActionsAsync(bool transaction)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("merge-refusal", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SetupAsync(session);
        await session.ExecuteAsync("INSERT INTO t VALUES(1,7)", cancellationToken: CancellationToken.None);
        if (transaction) { await session.ExecuteAsync("BEGIN", cancellationToken: CancellationToken.None); }
        const string sql = "MERGE INTO t USING (SELECT id,v FROM s) AS b ON t.id=b.id WHEN MATCHED THEN UPDATE SET v=1/0 WHEN NOT MATCHED THEN INSERT DEFAULT VALUES WHEN NOT MATCHED BY SOURCE THEN DELETE RETURNING merge_action()";

        // Act
        await using var explanation = (await session.ExecuteAsync("EXPLAIN " + sql, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));
        if (transaction) { await session.ExecuteAsync("COMMIT", cancellationToken: CancellationToken.None); }
        await using var result = (await session.ExecuteAsync("SELECT v FROM t", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK00001");
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { row.GetInt32(0).ShouldBe(7); }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - MERGE: merge_action is forbidden outside MERGE RETURNING (#1715)")]
    public async Task Plan_ActionFunctionOutsideMerge_ShouldRejectContextAsync()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("merge-context", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT merge_action()", cancellationToken: CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS01053");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - MERGE: binding never invokes action callbacks (#1715)")]
    [InlineData("WHEN MATCHED THEN UPDATE SET v=merge_probe(1)")]
    [InlineData("WHEN NOT MATCHED THEN INSERT(v) VALUES(merge_probe(1))")]
    [InlineData("WHEN NOT MATCHED BY SOURCE THEN UPDATE SET v=merge_probe(1)")]
    public async Task Plan_ImmutableActionCallback_ShouldNotInvokeAsync(string action)
    {
        // Arrange
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("merge-callback");
        builder.Functions.Add(SqlScalarFunction.Create("merge_probe", (int value) => { calls++; return value; }, SqlFunctionVolatility.Immutable));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SetupAsync(session);
        string sql = "MERGE INTO t USING s ON t.id=s.id " + action + " RETURNING merge_probe(1),merge_action()";

        // Act
        await session.ExecuteAsync("EXPLAIN " + sql, cancellationToken: CancellationToken.None);
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));

        // Assert
        calls.ShouldBe(0);
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK00001");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - MERGE: source trees, predicates, aliases and metadata bind (#1715)")]
    [InlineData("s AS b", "t.id=b.id")]
    [InlineData("(SELECT id,v FROM s) AS b", "t.id=b.id")]
    [InlineData("(s RIGHT JOIN s AS u ON s.id=u.id) AS b(id,v,otherid,otherv)", "t.id=b.id")]
    [InlineData("(s FULL JOIN s AS u USING(id)) AS b(id,v,otherv)", "t.id=b.id")]
    [InlineData("s AS b JOIN (s AS u JOIN s AS z ON EXISTS(SELECT u.id WHERE u.id=z.id)) ON b.id=u.id", "t.id=b.id")]
    public async Task Plan_ReadSourceForms_ShouldBindAndExplainAsync(string source, string on)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("merge-sources", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SetupAsync(session);
        string sql = "MERGE INTO t USING " + source + " ON " + on +
            " WHEN MATCHED AND EXISTS(SELECT b.id WHERE b.id=t.id) THEN DELETE" +
            " WHEN NOT MATCHED THEN INSERT DEFAULT VALUES WHEN NOT MATCHED BY SOURCE THEN DELETE RETURNING t.id,b.id,merge_action()";

        // Act
        await using var explanation = (await session.ExecuteAsync("EXPLAIN " + sql, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();

        // Assert
        var operators = new List<string?>();
        await foreach (var row in explanation.GetRowsAsync(CancellationToken.None)) { operators.Add(row.GetString(2)); }
        operators.ShouldContain("Merge");
        operators.Count(name => name == "MergeAction").ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - MERGE: ordered actions retain conditional subquery ownership (#1715)")]
    public async Task Plan_WhenSubqueries_ShouldStayWithTheirActionAsync()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("merge-action-query", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SetupAsync(session);
        const string sql = "MERGE INTO t USING s ON t.id=s.id WHEN MATCHED AND EXISTS(SELECT 1/0) THEN DELETE WHEN MATCHED THEN DO NOTHING";

        // Act
        var plan = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql(sql).Statement.SqlExpression).ShouldBeOfType<SqlMergePlan>();

        // Assert: no eager statement-level subquery wrapper owns the later action's work.
        plan.Actions[0].Queries.ShouldHaveSingleItem();
        plan.Actions[1].Queries.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - MERGE: action intrinsic does not resolve application overloads (#1715)")]
    public async Task Plan_ActionNameCollision_ShouldKeepIntrinsicBindingAsync()
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("merge-intrinsic");
        builder.Functions.Add(SqlScalarFunction.Create("merge_action", (int value) => value, SqlFunctionVolatility.Immutable));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SetupAsync(session);
        const string sql = "MERGE INTO t USING s ON t.id=s.id WHEN MATCHED THEN DELETE RETURNING merge_action(),UPPER(merge_action())";

        // Act
        await using var description = (await session.ExecuteAsync("EXPLAIN " + sql, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));

        // Assert
        description.ShouldBeAssignableTo<DatabaseResultSet>();
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK00001");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - MERGE: absent sources nullify merged USING metadata (#1715)")]
    public async Task Plan_SourceUsingMetadata_ShouldReflectAbsentSourceAsync()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("merge-using-metadata", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE dest(k INT NOT NULL)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE s(id INT NOT NULL)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE u(id INT NOT NULL)", cancellationToken: CancellationToken.None);
        const string sql = "MERGE INTO dest USING s JOIN u USING(id) ON dest.k=s.id WHEN NOT MATCHED BY SOURCE THEN DELETE RETURNING *";

        // Act
        var plan = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql(sql).Statement.SqlExpression).ShouldBeOfType<SqlMergePlan>();
        var returning = plan.Returning.ShouldNotBeNull();

        // Assert
        returning.Select(column => column.Name).ShouldBe(["k", "id"]);
        returning[0].IsNullable.ShouldBeFalse();
        returning[1].IsNullable.ShouldBeTrue();
        plan.Source.Associations.Columns.Skip(1).ShouldAllBe(column => column.IsNullable);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - MERGE: hashless LEFT and normalized RIGHT associations remain available (#1715)")]
    [InlineData("WHEN NOT MATCHED BY SOURCE THEN DELETE", SqlJoinType.LeftOuter)]
    [InlineData("WHEN NOT MATCHED THEN INSERT VALUES(s.id,s.v)", SqlJoinType.RightOuter)]
    public async Task Plan_HashlessOneSidedAssociation_ShouldBindAndExplainAsync(string action, SqlJoinType kind)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("merge-one-sided", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SetupAsync(session);
        string sql = "MERGE INTO t USING s ON t.id>s.id " + action;

        // Act
        var plan = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql(sql).Statement.SqlExpression).ShouldBeOfType<SqlMergePlan>();
        await using var explanation = (await session.ExecuteAsync("EXPLAIN " + sql, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var details = new List<string?>();
        await foreach (var row in explanation.GetRowsAsync(CancellationToken.None)) { details.Add(row.GetString(5)); }

        // Assert
        var tree = plan.Source.Associations.Tree!;
        tree.Step!.Kind.ShouldBe(kind);
        tree.Step.Hash.ShouldBeNull();
        tree.Left!.Binding.ShouldBe(kind == SqlJoinType.RightOuter ? 1 : 0);
        details.ShouldContain(detail => detail != null && detail.Contains(kind + "; NestedLoop"));
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK00001");
    }

    private static async Task SetupAsync(SqlDatabaseSession session)
    {
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, v INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE s (id INT NOT NULL, v INT)", cancellationToken: CancellationToken.None);
    }
}
