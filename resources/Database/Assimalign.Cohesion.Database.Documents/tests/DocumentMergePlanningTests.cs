using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentMergePlanningTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - MERGE: EXPLAIN snapshots retain source access and ordered actions without reading")]
    [InlineData("KEY s.k", "KeyLookup")]
    [InlineData("PRIMARY KEY s.k", "KeyLookup")]
    [InlineData("META(t).id = s.k", "KeyJoin")]
    public async Task Explain_BothForms_ShouldDescribeWithoutReadingAsync(string on, string access)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("merge-explain", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        string text = "MERGE INTO targets t USING sources s ON " + on +
            " WHEN MATCHED THEN UPDATE SET t.n = t.n + s.n UNSET t.old WHERE s.n > 0" +
            " WHEN MATCHED THEN DELETE WHERE t.gone" +
            " WHEN NOT MATCHED THEN INSERT " + (access == "KeyLookup" ? "s" : "(KEY s.k, VALUE s)") +
            " WHERE t IS MISSING RETURNING RAW t";
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session, "EXPLAIN " + text);
        // Assert
        rows.Select(r => r.GetString(2)).ShouldBe(new[] { "Merge", access, "MergeUpdate", "MergeDelete", "MergeInsert", "ReturningValue", "Select", "Project", "CollectionScan" });
        rows.Select(r => r.IsNull(1) ? (int?)null : r.GetInt32(1)).ShouldBe(new int?[] { null, 0, 0, 0, 0, 0, 0, 6, 7 });
        rows[4].GetString(5).ShouldBe("filter=True; scope=source; target=MISSING");
        rows[6].GetString(3).ShouldBe("sources");
        session.LastQueryDocumentsRead.ShouldBe(0);
        session.LastQueryKeyLookups.ShouldBe(0);
        session.LastQuerySubqueriesExecuted.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - MERGE: collection derived and expression sources plan without evaluation")]
    [InlineData("sources s", "CollectionScan")]
    [InlineData("(SELECT RAW s FROM sources s WHERE s.n > 0) s", "DerivedSource")]
    [InlineData("[{k:'a',n:2}] AS s", "ExpressionSource")]
    [InlineData("1 / 0 AS s", "ExpressionSource")]
    [InlineData("(SELECT RAW s FROM sources s UNION ALL SELECT RAW t FROM targets t) AS s", "UnionAll")]
    public async Task Explain_SourceShapes_ShouldPlanWithoutReadingAsync(string source, string expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("merge-sources", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session, "EXPLAIN MERGE INTO targets t USING " + source + " ON KEY s.k WHEN MATCHED THEN DELETE");
        // Assert
        rows.ShouldContain(row => row.GetString(2) == expected);
        rows.ShouldContain(row => row.GetString(2) == "Merge");
        session.LastQueryDocumentsRead.ShouldBe(0);
        session.LastQueryKeyLookups.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - MERGE: binding errors and read-only targets precede execution")]
    [InlineData("MERGE INTO targets t USING sources s ON KEY t.k WHEN MATCHED THEN DELETE", "CD01040")]
    [InlineData("MERGE INTO targets t USING sources s ON KEY META(t).id WHEN MATCHED THEN DELETE", "CD01040")]
    [InlineData("MERGE INTO targets t USING sources s ON KEY (SELECT RAW t.k FROM [0] AS x) WHEN MATCHED THEN DELETE", "CD01040")]
    [InlineData("MERGE INTO targets t USING [1] AS s ON KEY META(unbound).id WHEN MATCHED THEN DELETE", "CD01040")]
    [InlineData("MERGE INTO targets t USING sources s ON KEY META().id WHEN MATCHED THEN DELETE", "CK00001")]
    [InlineData("MERGE INTO targets t USING sources s ON META(t).id = t.k WHEN MATCHED THEN DELETE", "CK00001")]
    [InlineData("MERGE INTO targets t USING sources s ON META(t).id = (SELECT RAW t.k FROM [0] AS x) WHEN MATCHED THEN DELETE", "CK00001")]
    [InlineData("MERGE INTO targets t USING sources s ON KEY k WHEN MATCHED THEN DELETE", "CK00001")]
    [InlineData("MERGE INTO targets t USING sources s ON KEY s.k WHEN MATCHED THEN DELETE RETURNING s.n", "CK00001")]
    [InlineData("MERGE INTO targets t USING sources s ON KEY s.k WHEN MATCHED THEN DELETE RETURNING (SELECT RAW s.n FROM [0] AS x)", "CK00001")]
    [InlineData("MERGE INTO targets t USING sources s ON KEY s.k WHEN MATCHED THEN UPDATE SET s.n = 1", "CD02016")]
    [InlineData("MERGE INTO targets t USING sources s ON KEY s.k WHEN MATCHED THEN UPDATE SET t.a = 1, t.a.b = 2", "CD01017")]
    [InlineData("MERGE INTO targets t USING sources s ON KEY s.k WHEN MATCHED THEN UPDATE UNSET t.a[0]", "CD02016")]
    [InlineData("MERGE INTO COHESION_SCHEMA.INDEXES t USING sources s ON KEY s.k WHEN MATCHED THEN DELETE", "CD04004")]
    public async Task Explain_InvalidScope_ShouldGiveOneCodedErrorAsync(string text, string code)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("merge-scope-errors", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("EXPLAIN " + text, cancellationToken: CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        session.LastQueryDocumentsRead.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - MERGE: the planner retains source-only lookup and MISSING target insert scopes")]
    public async Task Plan_ActionScopes_ShouldRetainBindingIdentityAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("merge-scopes", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        var merge = Parse("MERGE INTO targets USING sources ON KEY META(sources).id WHEN MATCHED THEN UPDATE SET targets.n = sources.n + targets.n WHERE targets.n > sources.n" +
            " WHEN MATCHED THEN DELETE WHERE META(targets).id = META(sources).id WHEN NOT MATCHED THEN INSERT {a:targets, b:sources} WHERE targets IS MISSING RETURNING targets");
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        // Act
        var nested = new DocumentPlanner(database.Catalog, transaction.Context.Snapshot, null).Plan(merge).ShouldBeOfType<DocumentNestedPlan>();
        var plan = nested.Root.ShouldBeOfType<DocumentMergePlan>();
        // Assert
        plan.SourceName.ShouldBe("sources");
        plan.TargetName.ShouldBe("targets");
        plan.LookupScope.TargetName.ShouldBeNull();
        plan.MatchedScope.TargetName.ShouldBe("targets");
        plan.MatchedScope.TargetIsMissing.ShouldBeFalse();
        plan.InsertScope.TargetName.ShouldBe("targets");
        plan.InsertScope.TargetIsMissing.ShouldBeTrue();
        plan.Update.ShouldNotBeNull().Targets.ShouldHaveSingleItem().Segments.ShouldHaveSingleItem().Name.ShouldBe("n");
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - MERGE: nested action expressions capture both source and target")]
    public async Task Explain_ActionSubqueries_ShouldBindBothSidesAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("merge-captures", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session, "EXPLAIN MERGE INTO targets t USING sources s ON KEY s.k" +
            " WHEN MATCHED THEN UPDATE SET t.n = (SELECT RAW t.n + s.n FROM [0] AS x)" +
            " WHEN NOT MATCHED THEN INSERT (SELECT RAW {t:t,s:s} FROM [0] AS x) WHERE t IS MISSING");
        // Assert
        rows.Count(row => row.GetString(2) == "SubqueryExpression").ShouldBe(2);
        rows.Where(row => row.GetString(2) == "SubqueryExpression").ShouldAllBe(row => row.GetString(5)!.Contains("correlation=s,t", StringComparison.Ordinal));
        session.LastQueryDocumentsRead.ShouldBe(0);
        session.LastQuerySubqueriesExecuted.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - MERGE: direct AST cannot bypass declaration or action validation")]
    [InlineData(true, "CD01023")]
    [InlineData(false, "CD01039")]
    public async Task Plan_DirectAst_ShouldValidateShapeAsync(bool duplicate, string code)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("merge-ast", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        var merge = new OqlMergeExpression("targets", duplicate ? "s" : "t", new("sources", "s"), new OqlLiteralExpression("a"), true,
            delete: duplicate ? new OqlMergeDeleteAction() : null);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(new DocumentQueryRequest(new OqlQueryStatement(merge)), CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - MERGE: execution refuses before source reads and preserves all documents")]
    [InlineData("sources s", "KEY s.k", "s")]
    [InlineData("(SELECT RAW s FROM sources s) s", "META(t).id = s.k", "(KEY s.k, VALUE s)")]
    [InlineData("[1] AS s", "KEY 'a'", "s")]
    public async Task Execute_ValidMerge_ShouldRefuseWithoutReadingOrWritingAsync(string source, string on, string insert)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("merge-refusal", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("MERGE INTO targets t USING " + source + " ON " + on +
            " WHEN MATCHED THEN DELETE WHEN NOT MATCHED THEN INSERT " + insert, cancellationToken: CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05022");
        session.LastQueryDocumentsRead.ShouldBe(0);
        session.LastQueryKeyLookups.ShouldBe(0);
        session.LastQuerySubqueriesExecuted.ShouldBe(0);
        (await DocumentSubqueryTests.RowsAsync(session, "SELECT RAW targets.n FROM targets")).ShouldHaveSingleItem().GetValue(0).ShouldBe(1m);
    }

    private static OqlMergeExpression Parse(string text) => DocumentQueryRequest.FromOql(text).Statement.OqlExpression.ShouldBeOfType<OqlMergeExpression>();

    [Theory(DisplayName = "Cohesion Test [Documents] - MERGE: direct lookup paths and metadata require a named binding")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Plan_UnnamedLookupRoot_ShouldRefuseBindingAsync(bool metadata)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("merge-unnamed-root", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        var path = new OqlPathExpression([new OqlPathSegment(null, 0)]);
        OqlExpression key = metadata ? new OqlMetaExpression([path], "id") : path;
        var merge = new OqlMergeExpression("targets", "t", new("sources", "s"), key, true, delete: new OqlMergeDeleteAction());
        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(new DocumentQueryRequest(new OqlQueryStatement(merge)), CancellationToken.None));
        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD01040");
        session.LastQueryDocumentsRead.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - MERGE: expression and derived metadata retain their visible binding")]
    [InlineData("[1] AS s")]
    [InlineData("(SELECT RAW sources FROM sources) s")]
    public async Task Explain_NonStoredMetadata_ShouldBindWithoutEvaluatingAsync(string source)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("merge-source-meta", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session, "EXPLAIN MERGE INTO targets t USING " + source +
            " ON KEY META(s).id WHEN MATCHED THEN DELETE RETURNING META().id, n");
        // Assert
        rows.ShouldContain(row => row.GetString(2) == "KeyLookup");
        rows.ShouldContain(row => row.GetString(2) == "Returning");
        session.LastQueryDocumentsRead.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - MERGE: an overdeep direct AST refuses on a small stack and the session remains usable")]
    public async Task Execute_DirectAstOnSmallStack_ShouldCodeFailureAsync()
    {
        // Arrange: text has its own bound; programmatic trees still need the runtime backstop.
        await using var engine = DocumentDatabaseEngine.Create("merge-stack", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        OqlExpression key = new OqlLiteralExpression("a");
        for (int index = 0; index < 10000; index++) { key = new OqlUnaryExpression("+", key); }
        var request = new DocumentQueryRequest(new OqlQueryStatement(new OqlMergeExpression("targets", "t", new("sources", "s"), key, true,
            delete: new OqlMergeDeleteAction())));
        Exception? failure = null;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var thread = new Thread(() =>
        {
            try { session.ExecuteAsync(request, cancellation.Token).AsTask().GetAwaiter().GetResult(); }
            catch (Exception error) { failure = error; } // Observe the public boundary's exact failure.
        }, 256 * 1024) { IsBackground = true };
        // Act
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30)).ShouldBeTrue();
        // Assert
        failure.ShouldBeOfType<DatabaseException>().Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05021");
        (await DocumentSubqueryTests.RowsAsync(session, "SELECT RAW 7 FROM [0] AS z")).ShouldHaveSingleItem().GetValue(0).ShouldBe(7m);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - MERGE: implicit names retain inner keyspace resolution while explicit aliases capture")]
    [InlineData("", "none")]
    [InlineData(" AS targets", "targets")]
    public async Task Explain_ImplicitAliasProvenance_ShouldPreserveNestedFromAsync(string alias, string correlation)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("merge-provenance", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session, "EXPLAIN MERGE INTO targets" + alias + " USING sources ON KEY sources.k" +
            " WHEN MATCHED THEN UPDATE SET targets.n = (SELECT RAW d.n FROM targets d)");
        // Assert
        rows.Single(row => row.GetString(2) == "SubqueryExpression").GetString(5).ShouldNotBeNull().ShouldContain("correlation=" + correlation, Case.Sensitive);
        session.LastQuerySubqueriesExecuted.ShouldBe(0);
    }

    private static async Task SeedAsync(DocumentDatabaseSession session)
    {
        await session.CreateCollectionAsync("sources", CancellationToken.None);
        await session.CreateCollectionAsync("targets", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO targets (KEY, VALUE) VALUES ('a', {n:1})", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO sources (KEY, VALUE) VALUES ('a', {k:'a',n:2})", cancellationToken: CancellationToken.None);
    }
}
