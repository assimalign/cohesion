using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentMergeRegressionTests
{
    private readonly ITestOutputHelper _output;
    public DocumentMergeRegressionTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Documents] - MERGE: null source or insert value fails at construction before the session")]
    [InlineData(true)] [InlineData(false)]
    public async Task Execute_NullRequiredMember_ShouldFailAtConstructionAsync(bool source)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("merge-null-regression", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        // Act / Assert
        await Should.ThrowAsync<ArgumentNullException>(async () =>
        {
            var merge = new OqlMergeExpression("targets", "t", source ? null! : new("sources", "s"), new OqlLiteralExpression("a"), true,
                delete: new OqlMergeDeleteAction(), insert: source ? null : new OqlMergeInsertAction(null, null!));
            await session.ExecuteAsync(new DocumentQueryRequest(new OqlQueryStatement(merge)), CancellationToken.None);
        });
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - MERGE: subquery descriptions belong to their key action or returning operator")]
    public async Task Explain_NestedExpressions_ShouldRetainOwnerAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("merge-explain-parents", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session, "EXPLAIN MERGE INTO targets t USING sources s ON KEY (SELECT RAW s.k FROM [0] AS z)" +
            " WHEN MATCHED THEN UPDATE SET t.n = (SELECT RAW s.n FROM [0] AS z)" +
            " WHEN MATCHED THEN DELETE WHERE (SELECT RAW t.n FROM [0] AS z)" +
            " WHEN NOT MATCHED THEN INSERT (SELECT RAW s FROM [0] AS z)" +
            " RETURNING (SELECT RAW t.n FROM [0] AS z) AS n");
        // Assert
        var operators = rows.ToDictionary(row => row.GetInt32(0), row => row.GetString(2));
        rows.Where(row => row.GetString(2) == "SubqueryExpression").Select(row => operators[row.GetInt32(1)])
            .Order().ShouldBe(new[] { "KeyLookup", "MergeDelete", "MergeInsert", "MergeUpdate", "Returning" }.Order());
        var ancestors = new Stack<int>();
        foreach (var row in rows)
        {
            if (!row.IsNull(1))
            {
                int parent = row.GetInt32(1);
                while (ancestors.TryPeek(out int ancestor) && ancestor != parent) { ancestors.Pop(); }
                ancestors.ShouldNotBeEmpty("A depth-first description never reopens a completed subtree.");
            }
            ancestors.Push(row.GetInt32(0));
        }
    }
    [Theory(DisplayName = "Cohesion Test [Documents] - MERGE: action predicates never count target index ranges")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Plan_TwoCandidateIndexes_ShouldSkipTargetAccessAsync(bool explain)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("merge-index-regression", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await session.ExecuteAsync("CREATE INDEX by_status ON targets (status)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX by_region ON targets (region)", cancellationToken: CancellationToken.None);
        // Prove the counter observes the same two candidates for ordinary UPDATE.
        await DocumentSubqueryTests.RowsAsync(session, "EXPLAIN UPDATE targets t SET t.n = 1 WHERE t.status = 'open' AND t.region = 'eu'");
        long before = database.Catalog.IndexRangesCounted;
        before.ShouldBeGreaterThanOrEqualTo(2);
        string text = "MERGE INTO targets t USING sources s ON KEY s.k WHEN MATCHED THEN UPDATE SET t.n = 1 WHERE t.status = 'open' AND t.region = 'eu'";
        // Act
        if (explain) { await DocumentSubqueryTests.RowsAsync(session, "EXPLAIN " + text); }
        else { (await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(text, cancellationToken: CancellationToken.None))).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05022"); }
        // Assert
        database.Catalog.IndexRangesCounted.ShouldBe(before);
        session.LastQueryDocumentsRead.ShouldBe(0);
        session.LastQueryKeyLookups.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - MERGE: every unbound named value root uses the capability refusal")]
    [InlineData("ON KEY k.x WHEN MATCHED THEN DELETE")]
    [InlineData("ON KEY s.k WHEN MATCHED THEN UPDATE SET t.a = address.city")]
    [InlineData("ON KEY s.k WHEN MATCHED THEN DELETE WHERE address.city")]
    public async Task Plan_UnboundNamedRoot_ShouldRefuseConsistentlyAsync(string suffix)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("merge-unbound-regression", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("EXPLAIN MERGE INTO targets t USING sources s " + suffix, cancellationToken: CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK00001");
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - MERGE: keys actions and returning never evaluate before execution support")]
    [InlineData(false, "ON KEY 1/0 WHEN MATCHED THEN DELETE")]
    [InlineData(true, "ON KEY 1/0 WHEN MATCHED THEN DELETE")]
    [InlineData(false, "ON KEY s.k WHEN MATCHED THEN UPDATE SET t.n = 1/0")]
    [InlineData(true, "ON KEY s.k WHEN MATCHED THEN UPDATE SET t.n = 1/0")]
    [InlineData(false, "ON KEY s.k WHEN MATCHED THEN DELETE WHERE t.x = 1/0")]
    [InlineData(true, "ON KEY s.k WHEN MATCHED THEN DELETE WHERE t.x = 1/0")]
    [InlineData(false, "ON KEY tick('a') WHEN MATCHED THEN UPDATE SET t.n = tick(1) WHERE tick(TRUE) WHEN MATCHED THEN DELETE WHERE tick(TRUE) WHEN NOT MATCHED THEN INSERT tick(s) RETURNING tick(t.n)")]
    [InlineData(true, "ON KEY tick('a') WHEN MATCHED THEN UPDATE SET t.n = tick(1) WHERE tick(TRUE) WHEN MATCHED THEN DELETE WHERE tick(TRUE) WHEN NOT MATCHED THEN INSERT tick(s) RETURNING tick(t.n)")]
    public async Task Execute_ActionExpressions_ShouldNeverEvaluateAsync(bool explain, string suffix)
    {
        // Arrange
        int calls = 0;
        var builder = DocumentDatabaseEngine.CreateBuilder("merge-no-evaluation");
        builder.Functions.Add(DocumentScalarFunction.Create<DocumentValue, DocumentValue>("tick", value => { Interlocked.Increment(ref calls); return value; }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        string text = "MERGE INTO targets t USING sources s " + suffix;
        // Act
        if (explain) { await DocumentSubqueryTests.RowsAsync(session, "EXPLAIN " + text); }
        else { (await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(text, cancellationToken: CancellationToken.None))).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05022"); }
        // Assert
        calls.ShouldBe(0);
        session.LastQueryDocumentsRead.ShouldBe(0);
        session.LastQueryKeyLookups.ShouldBe(0);
        session.LastQuerySubqueriesExecuted.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - MERGE: refusal aborts the entire explicit transaction under #1225")]
    public async Task Execute_ExplicitTransaction_ShouldAbortPriorWritesAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("merge-transaction-regression", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO targets (KEY, VALUE) VALUES ('pending', {n:9})", cancellationToken: CancellationToken.None);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("MERGE INTO targets t USING sources s ON KEY s.k WHEN MATCHED THEN DELETE", cancellationToken: CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05022");
        error.Message.ShouldNotContain("OQ-M2", Case.Sensitive);
        transaction.State.ShouldBe(TransactionState.Faulted);
        await using (var observer = await database.CreateSessionAsync(CancellationToken.None))
        { (await DocumentSubqueryTests.RowsAsync(observer, "SELECT RAW targets.n FROM targets")).ShouldHaveSingleItem().GetValue(0).ShouldBe(1m); }
        await transaction.RollbackAsync(CancellationToken.None);
        session.CurrentTransaction.ShouldBeNull();
        (await DocumentSubqueryTests.RowsAsync(session, "SELECT RAW targets.n FROM targets")).ShouldHaveSingleItem().GetValue(0).ShouldBe(1m);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - MERGE: recursive positions below the traversal threshold survive a one-MiB stack")]
    [InlineData("source", false)] [InlineData("source", true)]
    [InlineData("set", false)] [InlineData("set", true)]
    [InlineData("where", false)] [InlineData("where", true)]
    [InlineData("key", false)] [InlineData("key", true)]
    [InlineData("returning", false)] [InlineData("returning", true)]
    public async Task Plan_NearGuardThreshold_ShouldSurviveSmallStackAsync(string position, bool explain)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("merge-stack-regression", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        var merge = MergeAtDepth(position, 127);
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (explain)
                {
                    new DocumentExpressionEvaluator(null, null, CancellationToken.None).ValidateInputs(merge);
                    DocumentPlanner.Describe(new DocumentPlanner(database.Catalog, transaction.Context.Snapshot, null, estimateSinglePath: true).Plan(merge));
                }
                else { session.ExecuteAsync(new DocumentQueryRequest(new OqlQueryStatement(merge)), CancellationToken.None).AsTask().GetAwaiter().GetResult(); }
            }
            catch (Exception error) { failure = error; } // Capture public-boundary failures from the explicit stack.
        }, 1024 * 1024) { IsBackground = true };
        // Act
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30)).ShouldBeTrue();
        // Assert
        if (explain) { failure.ShouldBeNull(); }
        else { failure.ShouldBeAssignableTo<DatabaseException>().ShouldNotBeNull().Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05022"); }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - MERGE: traversal threshold minus one remains stack-safe in every recursive position")]
    [InlineData("source")] [InlineData("set")] [InlineData("where")] [InlineData("key")] [InlineData("returning")]
    public async Task Plan_RuntimeGuardBoundary_ShouldRemainCodedAsync(string position)
    {
        // Arrange: measure the guarded input traversal on the same explicit stack. No
        // platform-specific frame-size assumption or elapsed-time assertion is involved.
        int lower = 1, upper = 16384;
        Run(() => new DocumentExpressionEvaluator(null, null).ValidateInputs(MergeAtDepth(position, upper)))
            .ShouldBeOfType<InsufficientExecutionStackException>();
        while (upper - lower > 1)
        {
            int middle = (lower + upper) / 2;
            Exception? probe = Run(() => new DocumentExpressionEvaluator(null, null).ValidateInputs(MergeAtDepth(position, middle)));
            if (probe is null) { lower = middle; }
            else { probe.ShouldBeOfType<InsufficientExecutionStackException>(); upper = middle; }
        }
        await using var engine = DocumentDatabaseEngine.Create("merge-runtime-stack", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        var merge = MergeAtDepth(position, lower);
        // Act: EXPLAIN's exact planning/description stages (the language-owned EXPLAIN
        // constructor is internal); execute the code-built statement through the session.
        Exception? explain = Run(() => DocumentPlanner.Describe(new DocumentPlanner(database.Catalog, transaction.Context.Snapshot, null, estimateSinglePath: true).Plan(merge)));
        Exception? execute = Run(() => session.ExecuteAsync(new DocumentQueryRequest(new OqlQueryStatement(merge)), CancellationToken.None).AsTask().GetAwaiter().GetResult());
        // Assert
        string? explainCode = explain is null ? null : ((DatabaseException)database.TranslateFailure(explain)).Diagnostics.ShouldHaveSingleItem().Code;
        string? executeCode = execute.ShouldBeAssignableTo<DatabaseException>().ShouldNotBeNull().Diagnostics.ShouldHaveSingleItem().Code;
        if (explainCode is not null) { explainCode.ShouldBe("CD05021"); }
        new[] { "CD05021", "CD05022" }.ShouldContain(executeCode);
        _output.WriteLine($"position={position}; guarded threshold={upper}; tested depth={lower}; EXPLAIN={explainCode ?? "success"}; execution={executeCode}");

        static Exception? Run(Action action)
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception error) { failure = error; } // Observe the exact failure on this stack.
            }, 1024 * 1024) { IsBackground = true };
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(30)).ShouldBeTrue();
            return failure;
        }
    }

    private static OqlMergeExpression MergeAtDepth(string position, int depth)
    {
        OqlExpression deep = position == "source" ? new OqlLiteralExpression(1) : Path(position == "returning" ? "t" : "s", "k");
        for (int index = 1; index < depth; index++)
        { deep = position == "where" ? new OqlBinaryExpression(deep, "AND", new OqlLiteralExpression(true)) : new OqlUnaryExpression("IS NULL", deep); }
        return new OqlMergeExpression("targets", "t", position == "source" ? new("", "s", deep) : new("sources", "s"),
            position == "key" ? deep : Path("s", "k"), true,
            update: new([new(Path("t", "n"), position == "set" ? deep : new OqlLiteralExpression(1))], [], position == "where" ? deep : null),
            returning: position == "returning" ? new([new(deep, "n")]) : null);
    }

    private static OqlPathExpression Path(string root, string member) => new([new(root, null), new(member, null)]);

    private static async Task SeedAsync(DocumentDatabaseSession session)
    {
        await session.CreateCollectionAsync("sources", CancellationToken.None);
        await session.CreateCollectionAsync("targets", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO targets (KEY, VALUE) VALUES ('a', {n:1,status:'open',region:'eu'})", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO sources (KEY, VALUE) VALUES ('a', {k:'a',n:2})", cancellationToken: CancellationToken.None);
    }
}
