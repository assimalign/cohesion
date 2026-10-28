using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GraphMergeSecondPassTests
{
    private readonly ITestOutputHelper _output;

    public GraphMergeSecondPassTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Graph] - MERGE imports into an empty indexed label despite zero label and type estimates (#1735)")]
    [InlineData(IsolationLevel.ReadCommitted, false)]
    [InlineData(IsolationLevel.Snapshot, false)]
    [InlineData(IsolationLevel.ReadCommitted, true)]
    [InlineData(IsolationLevel.Snapshot, true)]
    public async Task Execute_EmptyIndexedImport_ShouldSeekBeforeScans(IsolationLevel isolation, bool relationship)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("second", new());
        var db = await engine.CreateDatabaseAsync("patterns", token);
        await using var session = await db.CreateSessionAsync(token);
        var schema = GraphSchema.Open(db, session);
        await schema.SaveLabelAsync(new(Guid.NewGuid(), "Person"), token);
        await schema.CreateIndexAsync("Person", "by_id", "id", token);
        await schema.SaveRelationshipTypeAsync(new(Guid.NewGuid(), "R"), token);
        // With a nonempty label, the empty relationship type used to displace the seek too.
        if (relationship) { await session.ExecuteAsync("CREATE (:Person {id:-1})", cancellationToken: token); }
        await using var transaction = await session.BeginTransactionAsync(isolation, token);
        var parameters = Rows(10_000);
        string pattern = relationship ? "(n:Person {id:r.id})-[:R]->()" : "(n:Person {id:r.id})";

        // Act
        try
        {
            var result = await session.ExecuteAsync("UNWIND $rows AS r MERGE " + pattern + " RETURN count(*)", parameters, token);

            // Assert
            result.AffectedCount.ShouldBe(relationship ? 30_000 : 10_000);
            (await GraphMergeExecutionTests.Read(result)).Single()[0].ShouldBe(10_000L);
            session.LastStatementMetrics!.Consumed.ShouldBeLessThanOrEqualTo(300_000);
            await transaction.CommitAsync(token);
        }
        finally { _output.WriteLine($"empty import: isolation={isolation}; relationship={relationship}; rows=10000; work={session.LastStatementMetrics?.Consumed}"); }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - EXPLAIN renders a row-dependent MERGE seek as GQL text (#1735)")]
    public async Task Execute_ExplainRowSeek_ShouldDescribeExpression()
    {
        // Arrange: a nonempty label also exposes the formatting defect before seek ranking is fixed.
        await using var engine = GraphDatabaseEngine.Create("second", new());
        var db = await engine.CreateDatabaseAsync("patterns", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE (:Person {id:1})", cancellationToken: CancellationToken.None);
        await GraphSchema.Open(db, session).CreateIndexAsync("Person", "by_id", "id", CancellationToken.None);

        // Act
        var rows = await GraphMergeExecutionTests.Read(await session.ExecuteAsync(
            "EXPLAIN UNWIND $rows AS r MERGE (n:Person {id:r.id})", Rows(1), CancellationToken.None));
        string text = string.Join("\n", rows.SelectMany(row => row).Select(value => Convert.ToString(value)));

        // Assert
        text.ShouldContain("value=r.id", Case.Sensitive);
        text.ShouldNotContain("GqlPropertyExpression", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - indexed overlay probes retain only matching entries and charge their lookup (#1735)")]
    public async Task StreamAnchorAsync_UnrelatedOverlay_ShouldUseValueLookup()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("second", new());
        var db = await engine.CreateDatabaseAsync("patterns", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE (:Person {id:0})", cancellationToken: CancellationToken.None);
        await GraphSchema.Open(db, session).CreateIndexAsync("Person", "by_id", "id", CancellationToken.None);

        // Act
        await db.RunAsync(session, async operation =>
        {
            for (int i = 1; i <= 10_000; i++)
            { await operation.Updates.CreateNodeAsync(["Person"], new Dictionary<string, object?> { ["id"] = (long)i }, CancellationToken.None); }
            // Initialize the value view once; its construction must charge each visited change.
            await Probe(new());
            var budget = new GraphPlanExecutor.ExpansionBudget(memoryLimit: 65_536);
            await Probe(budget);

            // Assert: a miss has one charged value lookup, independent of the 10000 unrelated nodes.
            _output.WriteLine($"overlay miss: nodes=10000; work={budget.Consumed}; peak={budget.Memory.PeakRetainedBytes}");
            budget.Consumed.ShouldBeInRange(1, 4);
            return true;

            async ValueTask Probe(GraphPlanExecutor.ExpansionBudget budget)
            {
                await foreach (var node in operation.Updates.StreamAnchorAsync(new(0, "Person", "id", -1L), budget, CancellationToken.None))
                { node.Id.ShouldBe(0UL); }
            }
        }, CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - a constant indexed property cannot hide a MERGE row seek from an empty type (#1735)")]
    public async Task Execute_LeadingConstantIndex_ShouldStillChooseRowSeek()
    {
        // Arrange: the constant seek costs one while the empty type costs zero.
        await using var engine = GraphDatabaseEngine.Create("second", new());
        var db = await engine.CreateDatabaseAsync("patterns", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE (:Person {group:1,id:-1})", cancellationToken: CancellationToken.None);
        var schema = GraphSchema.Open(db, session);
        await schema.CreateIndexAsync("Person", "by_group", "group", CancellationToken.None);
        await schema.CreateIndexAsync("Person", "by_id", "id", CancellationToken.None);
        await schema.SaveRelationshipTypeAsync(new(Guid.NewGuid(), "R"), CancellationToken.None);

        // Act
        var rows = await GraphMergeExecutionTests.Read(await session.ExecuteAsync(
            "EXPLAIN UNWIND $rows AS r MERGE (n:Person {group:1,id:r.id})-[:R]->()", Rows(1), CancellationToken.None));
        string text = string.Join("\n", rows.SelectMany(row => row).Select(value => Convert.ToString(value)));

        // Assert
        text.ShouldContain("by_id", Case.Sensitive);
        text.ShouldContain("value=r.id", Case.Sensitive);
        text.ShouldNotContain("RelationshipTypeScan", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - MERGE creates 100000 indexed nodes with constant work per row (#1735)")]
    [InlineData(IsolationLevel.ReadCommitted)]
    [InlineData(IsolationLevel.Snapshot)]
    public async Task Execute_LargeCreatingUpsert_ShouldKeepConstantWorkPerRow(IsolationLevel isolation)
    {
        // Arrange: two small inputs generate 100000 rows without exceeding the per-value
        // 65536-element parameter limit. Both execution budgets keep their engine defaults.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("second", new());
        var db = await engine.CreateDatabaseAsync("patterns", token);
        await using var session = await db.CreateSessionAsync(token);
        var schema = GraphSchema.Open(db, session);
        await schema.SaveLabelAsync(new(Guid.NewGuid(), "Person"), token);
        await schema.CreateIndexAsync("Person", "by_id", "id", token);
        await using var transaction = await session.BeginTransactionAsync(isolation, token);

        // Act
        var parameters = new Dictionary<string, object?>
        {
            ["batches"] = Enumerable.Range(0, 1000).Select(i => (object?)(long)i).ToArray(),
            ["offsets"] = Enumerable.Range(0, 100).Select(i => (object?)(long)i).ToArray(),
        };
        var result = await session.ExecuteAsync("UNWIND $batches AS b UNWIND $offsets AS i MERGE (n:Person {id:b*100+i}) RETURN count(*)", parameters, token);
        var metrics = session.LastStatementMetrics!;

        // Assert
        _output.WriteLine($"creating upsert: isolation={isolation}; rows=100000; work={metrics.Consumed}; units/row={metrics.Consumed / 100000d}; peak={metrics.Memory.PeakRetainedBytes}");
        result.AffectedCount.ShouldBe(100_000);
        (await GraphMergeExecutionTests.Read(result)).Single()[0].ShouldBe(100_000L);
        metrics.Consumed.ShouldBeLessThanOrEqualTo(900_000);
        metrics.Memory.ActiveLeases.ShouldBe(0);
        await transaction.CommitAsync(token);
    }

    private static Dictionary<string, object?> Rows(int count) => new()
    {
        ["rows"] = Enumerable.Range(0, count).Select(i => new Dictionary<string, object?> { ["id"] = (long)i }).ToArray(),
    };

    [Fact(DisplayName = "Cohesion Test [Graph] - label-scan overlay probes charge rejected labels (#1735)")]
    public async Task StreamAnchorAsync_UnmatchedLabels_ShouldChargeComparisons()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("second", new());
        var db = await engine.CreateDatabaseAsync("patterns", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await db.RunAsync(session, async operation =>
        {
            for (int i = 0; i < 1000; i++)
            { await operation.Updates.CreateNodeAsync(["Other"], new Dictionary<string, object?>(), CancellationToken.None); }
            var budget = new GraphPlanExecutor.ExpansionBudget();

            // Act
            await foreach (var node in operation.Updates.StreamAnchorAsync(new(0, "Person", null, null), budget, CancellationToken.None))
            { node.Id.ShouldBe(0UL); }

            // Assert
            budget.Consumed.ShouldBe(1000);
            return true;
        }, CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - MERGE overlay value changes suppress old index entries and preserve numeric equality (#1735)")]
    public async Task Execute_ChangedIndexedValue_ShouldMatchOnlyCurrentOverlayValue()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("second", new());
        var db = await engine.CreateDatabaseAsync("patterns", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE (:Person {id:1})", cancellationToken: CancellationToken.None);
        await GraphSchema.Open(db, session).CreateIndexAsync("Person", "by_id", "id", CancellationToken.None);

        // Act: the first match moves 1 to 2; the second row must see it under a different CLR tag.
        var result = await session.ExecuteAsync("UNWIND [1,2.0,1] AS i MERGE (n:Person {id:i}) ON MATCH SET n.id=2 RETURN count(*)", cancellationToken: CancellationToken.None);

        // Assert: one replacement, one new node for the old value; the numeric 2.0 matched.
        result.AffectedCount.ShouldBe(2);
        (await GraphMergeExecutionTests.Read(result)).Single()[0].ShouldBe(3L);
        (await GraphMergeExecutionTests.Rows(session, "MATCH (n:Person) RETURN n.id ORDER BY n.id")).Select(row => row[0]).ShouldBe([1L, 2L]);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - bound MERGE diagnostic registry describes node patterns (#1735)")]
    public void Registry_BoundMergeDiagnostic_ShouldDescribeNodePatterns()
    {
        // Arrange
        string registry = Path.GetFullPath(Path.Combine(TestDirectory(), "..", "..", "Assimalign.Cohesion.Database", "docs", "ERROR-CODES.md"));

        // Act
        string entry = File.ReadLines(registry).Single(line => line.Contains("| CG03005 |", StringComparison.Ordinal));

        // Assert
        entry.ShouldContain("bound MERGE node", Case.Sensitive);
        entry.ShouldNotContain("endpoints", Case.Sensitive);
    }

    private static string TestDirectory([CallerFilePath] string source = "") => Path.GetDirectoryName(source)!;

    [Fact(DisplayName = "Cohesion Test [Graph] - a disposed MERGE read after teardown retains the ended-statement code (#1735)")]
    public async Task MatchAsync_DisposedReadAfterTeardown_ShouldReportEnded()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("second", new());
        var db = await engine.CreateDatabaseAsync("patterns", token);
        await using var session = await db.CreateSessionAsync(token);
        var identity = new GraphMergePatternIdentity([new(null, ["Person"], new Dictionary<string, object?> { ["id"] = 1L })], []);
        var budget = new GraphPlanExecutor.ExpansionBudget();
        int probes = 0;

        // Act: a read that resumes after teardown can fail before the next active-state check.
        var failure = await Should.ThrowAsync<DatabaseException>(db.RunAsync(session, operation =>
        {
            operation.QueryBudget = budget;
            return GraphMergeConcurrency.MatchAsync<ulong>(db, operation, identity, async (_, _) =>
            {
                probes++;
                await session.DisposeAsync();
                throw new ObjectDisposedException("ended probe");
            }, token);
        }, token).AsTask());

        // Assert
        failure.Diagnostics[0].Code.ShouldBe("CK04002");
        probes.ShouldBe(1);
        budget.Memory.RetainedBytes.ShouldBe(0);
        budget.Memory.ActiveLeases.ShouldBe(0);
        var checker = await db.Coordinator.BeginAsync(IsolationLevel.Snapshot, token);
        try
        {
            db.Coordinator.LockManager.TryAcquire(checker.Sequence, identity.Resource, LockMode.Exclusive).ShouldBeTrue();
            db.Coordinator.LockManager.TryAcquire(checker.Sequence, LockResource.Object(ulong.MaxValue - 3), LockMode.Exclusive).ShouldBeTrue();
        }
        finally { await db.Coordinator.RollbackAsync(checker, token); }
        await using var other = await db.CreateSessionAsync(token);
        await other.ExecuteAsync("CREATE (:Released)", cancellationToken: token);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - MERGE teardown inside parent and cached-tracker windows reports ended statement (#1735)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchAsync_DisposeInsideTrackerWindow_ShouldReportEnded(bool cachedTracker)
    {
        // Arrange: pause teardown after the session closes and before its ended sentinel.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("second", new());
        var db = await engine.CreateDatabaseAsync("patterns", token);
        await using var session = await db.CreateSessionAsync(token);
        var identity = new GraphMergePatternIdentity([new(null, ["Person"], new Dictionary<string, object?> { ["id"] = 1L })], []);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? teardown = null;
        int hooks = 0, probes = 0;
        var budget = new GraphPlanExecutor.ExpansionBudget();
        db.BeforeSnapshotPinRelease = _ =>
        {
            entered.TrySetResult();
            release.Task.WaitAsync(token).GetAwaiter().GetResult();
        };

        // Act
        var pending = db.RunAsync(session, async operation =>
        {
            operation.QueryBudget = budget;
            try
            {
                return await GraphMergeConcurrency.MatchAsync(db, operation, identity,
                    (_, _) => { probes++; return ValueTask.FromResult(new GraphMergeRead<ulong>([], [])); }, token,
                    beforeTracker: cachedTracker ? DisposeAsync : null,
                    beforeParentCheck: cachedTracker ? null : DisposeAsync);
            }
            catch (Exception error) { observed.TrySetResult(error); throw; }
        }, token).AsTask();
        try
        {
            var error = await observed.Task.WaitAsync(token);
            if (!cachedTracker) { teardown!.IsCompleted.ShouldBeFalse(); }
            release.TrySetResult();
            await teardown!;
            await Should.ThrowAsync<DatabaseException>(pending);

            // Assert: release survives the failure before the caller's operation exits.
            error.ShouldBeAssignableTo<DatabaseException>().Diagnostics[0].Code.ShouldBe("CK04002");
            hooks.ShouldBe(1);
            probes.ShouldBe(0);
            budget.Memory.ActiveLeases.ShouldBe(0);
            budget.Memory.RetainedBytes.ShouldBe(0);
            var checker = await db.Coordinator.BeginAsync(IsolationLevel.Snapshot, token);
            try
            {
                db.Coordinator.LockManager.TryAcquire(checker.Sequence, identity.Resource, LockMode.Exclusive).ShouldBeTrue();
                db.Coordinator.LockManager.TryAcquire(checker.Sequence, LockResource.Object(ulong.MaxValue - 3), LockMode.Exclusive).ShouldBeTrue();
                await using var other = await db.CreateSessionAsync(token);
                await other.ExecuteAsync("CREATE (:Released)", cancellationToken: token);
            }
            finally { await db.Coordinator.RollbackAsync(checker, token); }
        }
        finally
        {
            release.TrySetResult();
            db.BeforeSnapshotPinRelease = null;
            if (teardown is not null) { await teardown; }
        }

        async ValueTask DisposeAsync()
        {
            hooks++;
            teardown = Task.Run(async () => await session.DisposeAsync(), token);
            await entered.Task.WaitAsync(token);
            if (cachedTracker) { release.TrySetResult(); await teardown; }
        }
    }
}
