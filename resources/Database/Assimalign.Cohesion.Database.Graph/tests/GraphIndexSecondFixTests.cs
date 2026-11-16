using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Protocol;
using Assimalign.Cohesion.Database.Graph.Storage;
using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GraphIndexSecondFixTests
{
    private readonly ITestOutputHelper _output;
    public GraphIndexSecondFixTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Graph] - Abandoned DROP receipts and invalid revisions refuse every file unchanged (#1644)")]
    [InlineData(true, false)] [InlineData(false, false)]
    [InlineData(true, true)] [InlineData(false, true)]
    public async Task OpenDatabaseAsync_CorruptCrashImage_ShouldPreserveEveryFileAsync(bool text, bool revision)
    {
        // Arrange: a child exits without cleanup while the DROP or node update is uncommitted.
        string root = TemporaryRoot();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await CreateCrashImageAsync(root, text, revision, timeout.Token);
            string directory = Path.Combine(root, "image");
            var before = Directory.GetFiles(directory).ToDictionary(path => path, File.ReadAllBytes);

            // Act
            await using var engine = GraphDatabaseEngine.Create("receipt-open", new() { RootPath = root });
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await engine.OpenDatabaseAsync("image", timeout.Token));
            await engine.DisposeAsync();

            // Assert: the preflight anticipates recovery clearing an abandoned deleter.
            failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CK06003");
            failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CG06018");
            Directory.GetFiles(directory).Order().ShouldBe(before.Keys.Order());
            foreach (var (path, bytes) in before) { File.ReadAllBytes(path).ShouldBe(bytes, path); }
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Recovered live trees refuse negative receipts at store attachment (#1644)")]
    [InlineData(true)] [InlineData(false)]
    public async Task Open_RecoveredLiveReceipt_ShouldRefuseBeforeCleanupAsync(bool text)
    {
        // Arrange: bypass engine preflight to exercise the check after logical recovery.
        string root = TemporaryRoot();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            ulong id = await CreateCrashImageAsync(root, text, revision: false, timeout.Token);
            using var storage = OpenImage(root);
            await using var coordinator = new TransactionCoordinator(storage, storage.WriteAheadJournal, storage.Records);
            coordinator.AnalyzeAndScrub(timeout.Token);
            var pages = storage.GetOwnerIndexPages(id).ToArray();
            pages.ShouldNotBeEmpty();

            // Act / Assert: no owner-page cleanup can precede the recovered-definition check.
            Should.Throw<StorageCorruptionException>(() => GraphStore.Open(storage, coordinator));
            storage.GetOwnerIndexPages(id).ShouldBe(pages);
            await VerifyAfterReceiptRepairAsync(storage, coordinator, id, text, timeout.Token);
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Purge refuses a live tree regardless of its registration root sign (#1644)")]
    [InlineData(true)] [InlineData(false)]
    public async Task RunVersionPurgePass_RecoveredLiveReceipt_ShouldKeepEveryOwnerPageAsync(bool text)
    {
        // Arrange: a caller that attached before scrub still needs the defensive maintenance check.
        string root = TemporaryRoot();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            ulong id = await CreateCrashImageAsync(root, text, revision: false, timeout.Token);
            using var storage = OpenImage(root);
            GraphStore store = null!;
            await using var coordinator = new TransactionCoordinator(storage, storage.WriteAheadJournal, storage.Records,
                (bound, token) => store.PruneIndexesAsync(bound, token));
            store = GraphStore.Open(storage, coordinator); // The abandoned DROP is still tombstoned.
            coordinator.AnalyzeAndScrub(timeout.Token); // It is now live again.
            var pages = storage.GetOwnerIndexPages(id).ToArray();
            pages.ShouldNotBeEmpty();

            // Act / Assert: repeated maintenance attempts must free nothing.
            for (int pass = 0; pass < 16; pass++)
            {
                var failure = Should.Throw<StorageCorruptionException>(() => coordinator.RunVersionPurgePass(timeout.Token));
                failure.Code.ShouldBe("CK06003");
                storage.GetOwnerIndexPages(id).ShouldBe(pages);
                store.IndexOwnerPagesReclaimed.ShouldBe(0);
            }
            await VerifyAfterReceiptRepairAsync(storage, coordinator, id, text, timeout.Token);
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - A later text index cannot take an earlier scan anchor's tie or grant (#1644)")]
    public async Task Execute_LaterTextAnchor_ShouldPreserveThreeUnitScanGrantAsync()
    {
        // Arrange: End has extra incoming Y postings, but Start supplies the first label-count tie.
        await using var engine = GraphDatabaseEngine.Create("anchor-grant", GraphTextIndexTests.Quiet());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (a:Start {k:1})-[:X]->(b:Mid)-[:Y]->(n:End {k:2,p:'abcdef'}),(:Extra)-[:Y]->(n),(:Extra)-[:Y]->(n)", cancellationToken: CancellationToken.None);
        const string query = "MATCH (a:Start)-[:X]->(b)-[:Y]->(n:End) WHERE n.p CONTAINS 'abc' RETURN a.k,n.k";
        var request = GraphQueryRequest.FromGql(query, null, new GraphQueryLimits { WorkUnits = 3 });
        var scan = await RowsAsync(session, request);
        session.LastStatementMetrics!.Physical.ShouldBe(3);
        var before = Anchor(await GraphTextIndexTests.RowsAsync(session, "EXPLAIN " + query));

        // Act
        await session.ExecuteAsync("CREATE TEXT INDEX t FOR (n:End) ON (n.p)", cancellationToken: CancellationToken.None);
        var seek = await RowsAsync(session, request);
        var work = session.LastStatementMetrics!.Physical;
        var after = Anchor(await GraphTextIndexTests.RowsAsync(session, "EXPLAIN " + query));

        // Assert
        seek.Select(row => string.Join("|", row)).ShouldBe(scan.Select(row => string.Join("|", row)));
        seek.ShouldHaveSingleItem().ShouldBe(new object?[] { 1L, 2L });
        work.ShouldBe(3);
        after.Position.ShouldBe(before.Position);
        after.Operator.ShouldBe("LabelScan");
        after.Position.ShouldBe("node=0");
        _output.WriteLine($"bounded EXPLAIN: {after.Operator}; {after.Position}; object={after.Label}; work={work}");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Seeded multi-node text access preserves anchor positions and scan work plus one (#1644)")]
    public async Task Execute_SeededMultiNodeStatements_ShouldKeepScanAnchorsAndWorkAsync()
    {
        // Arrange: vary both label-count winners, ties, edge directions and predicate positions.
        const int seed = 726113;
        var random = new Random(seed);
        int seeks = 0, pairs = 0;
        await using var engine = GraphDatabaseEngine.Create("anchor-seed", GraphTextIndexTests.Quiet());
        for (int graph = 0; graph < 12; graph++)
        {
            var database = await engine.CreateDatabaseAsync("g" + graph, CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await session.ExecuteAsync("INSERT (a:A {k:1,p:'abcdef'})-[:X]->(:B {k:2,p:'abcdef'})-[:Y]->(c:C {k:3,p:'abcdef'}),(:D)-[:Y]->(c),(:D)-[:Y]->(c)", cancellationToken: CancellationToken.None);
            foreach (string label in new[] { "A", "B", "C" })
            {
                int count = random.Next(5);
                for (int i = 0; i < count; i++)
                { await session.ExecuteAsync($"INSERT (:{label} {{k:{10 + i},p:'xyzabc'}})", cancellationToken: CancellationToken.None); }
            }
            string[] paths = ["(a:A)-[:X]->(b:B)-[:Y]->(c:C)", "(c:C)<-[:Y]-(b:B)<-[:X]-(a:A)", "(a:A)-[:X]->(b:B)", "(b:B)-[:Y]->(c:C)"];
            var statements = new List<string>();
            foreach (string path in paths)
            {
                foreach (string variable in new[] { "a", "b", "c" }.Where(variable => path.Contains("(" + variable + ":", StringComparison.Ordinal)))
                {
                    statements.Add($"MATCH {path} WHERE {variable}.p CONTAINS 'abc' RETURN {variable}.k ORDER BY {variable}.k");
                    statements.Add($"MATCH {path} WHERE {variable}.p='abcdef' RETURN {variable}.k ORDER BY {variable}.k");
                    statements.Add($"MATCH {path.Replace(variable + ":" + variable.ToUpperInvariant(), variable + ":" + variable.ToUpperInvariant() + " {p:'abcdef'}", StringComparison.Ordinal)} RETURN {variable}.k ORDER BY {variable}.k");
                }
            }
            var expected = new List<(List<object?[]> Rows, long Work, (string Position, string Operator, object? Label) Anchor)>();
            foreach (string query in statements)
            {
                var rows = await GraphTextIndexTests.RowsAsync(session, query);
                long work = session.LastStatementMetrics!.Physical;
                expected.Add((rows, work, Anchor(await GraphTextIndexTests.RowsAsync(session, "EXPLAIN " + query))));
            }
            foreach (string label in new[] { "A", "B", "C" })
            { await session.ExecuteAsync($"CREATE TEXT INDEX t{label} FOR (n:{label}) ON (n.p)", cancellationToken: CancellationToken.None); }

            // Act / Assert: compare anchor position even when the text range happens to contain every row.
            for (int i = 0; i < statements.Count; i++)
            {
                var actual = await GraphTextIndexTests.RowsAsync(session, statements[i]);
                actual.Select(row => string.Join("|", row)).ShouldBe(expected[i].Rows.Select(row => string.Join("|", row)), $"seed={seed}; graph={graph}; {statements[i]}");
                session.LastStatementMetrics!.Physical.ShouldBeLessThanOrEqualTo(expected[i].Work + 1);
                var anchor = Anchor(await GraphTextIndexTests.RowsAsync(session, "EXPLAIN " + statements[i]));
                anchor.Position.ShouldBe(expected[i].Anchor.Position, statements[i]);
                anchor.Label.ShouldBe(expected[i].Anchor.Label);
                if (anchor.Operator == "TextIndexSeek") { seeks++; }
                pairs++;
            }
        }
        seeks.ShouldBeGreaterThan(0);
        _output.WriteLine($"seed={seed}; statement pairs={pairs}; confirmed text seeks={seeks}; anchor differences=0; work excess over scan+1=0");
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Exact build validation retains scan-first errors and refuses before allocating pages (#1644)")]
    [InlineData(true, 9, "CG05004", 10, 10)]
    [InlineData(true, 10, "CG02009", 10, 10)]
    [InlineData(true, 11, "CG02009", 10, 10)]
    [InlineData(true, 20, "CG02009", 10, 10)]
    [InlineData(false, 9, "CG05004", 10, 10)]
    [InlineData(false, 10, "CG05004", 11, 10)]
    [InlineData(false, 11, "CG05004", 12, 10)]
    [InlineData(false, 19, "CG05004", 20, 10)]
    [InlineData(false, 20, null, 20, 10)]
    public async Task Execute_ExactBuild_ShouldPreserveValidationOrderAsync(bool oversized, long grant, string? code, long work, long postings)
    {
        // Arrange: the final value crosses the exact-key limit while fitting in a node record.
        await using var engine = GraphDatabaseEngine.Create("exact-order", GraphTextIndexTests.Quiet());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        for (int i = 0; i < 10; i++)
        {
            await session.ExecuteAsync("INSERT (:N {p:$pattern})", GraphTextIndexTests.Bind(oversized && i == 9 ? new string('x', 1100) : "short"), CancellationToken.None);
        }
        long pages = database.DataStorage.PageManager.PageCount;
        long visited = database.Store.LabelEntriesVisited;
        var owners = GraphIndexReclamationReviewTests.IndexOwners(database).Keys.Order().ToArray();
        var request = GraphQueryRequest.FromGql("CREATE INDEX e FOR (n:N) ON (n.p)", null, new GraphQueryLimits { WorkUnits = grant });

        // Act
        DatabaseException? failure = null;
        try { await session.ExecuteAsync(request, CancellationToken.None); }
        catch (DatabaseException error) { failure = error; }

        // Assert
        if (code is null) { failure.ShouldBeNull(); }
        else
        {
            failure.ShouldNotBeNull().Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
            database.DataStorage.PageManager.PageCount.ShouldBe(pages);
            GraphIndexReclamationReviewTests.IndexOwners(database).Keys.Order().ShouldBe(owners);
            (await GraphSchema.Open(database, session).GetIndexesAsync(CancellationToken.None)).ShouldBeEmpty();
        }
        session.LastStatementMetrics!.Physical.ShouldBe(work);
        (database.Store.LabelEntriesVisited - visited).ShouldBe(postings);
        _output.WriteLine($"oversized={oversized}; grant={grant}; code={failure?.Diagnostics[0].Code ?? "success"}; work={work}; label_postings={postings}; new_pages={(code is null ? "success" : (database.DataStorage.PageManager.PageCount - pages).ToString())}");
    }

    private static async Task<ulong> CreateCrashImageAsync(string root, bool text, bool revision, CancellationToken token)
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "GraphRecoveryFixture", "Assimalign.Cohesion.Database.Graph.RecoveryFixture.dll");
        string host = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var start = new ProcessStartInfo(host)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(fixture);
        start.ArgumentList.Add("index-" + (revision ? "revision-" : "receipt-") + (text ? "text" : "exact"));
        start.ArgumentList.Add(root);
        using var process = Process.Start(start).ShouldNotBeNull();
        Task<string> output = process.StandardOutput.ReadToEndAsync(token);
        Task<string> errors = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); } }
        process.ExitCode.ShouldBe(0, await errors);
        string line = (await output).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Single(value => value.StartsWith("index-owner=", StringComparison.Ordinal));
        return ulong.Parse(line.AsSpan("index-owner=".Length), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task VerifyAfterReceiptRepairAsync(GraphStorage storage, TransactionCoordinator coordinator, ulong id, bool text, CancellationToken token)
    {
        MutateRecord(storage, 1, bytes =>
        {
            if (BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(18)) != id) { return false; }
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(26), -BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(26)));
            return true;
        });
        var store = GraphStore.Open(storage, coordinator);
        var context = await coordinator.BeginAsync(IsolationLevel.Snapshot, token);
        try
        {
            store.GetIndexes(context.Snapshot).ShouldHaveSingleItem().IsText.ShouldBe(text);
            Catalog.GraphCatalog.Open(storage, coordinator).FindIndex("t", context.Snapshot).ShouldNotBeNull().IsText.ShouldBe(text);
            store.StreamLabels(["Other"], context.Snapshot, token).ShouldHaveSingleItem().Properties["p"].ShouldBe("keep");
            if (text)
            {
                int count = 0;
                await foreach (var node in store.StreamTextIndexAsync("N", "p", "abc", "CONTAINS", context.Snapshot, token, () => { }, _ => { }))
                { node.Properties["p"].ShouldBe("oldabc"); count++; }
                count.ShouldBe(1);
            }
            else { (await store.SearchIndexAsync("N", "p", "oldabc", context.Snapshot, token)).ShouldHaveSingleItem(); }
        }
        finally { await coordinator.RollbackAsync(context, token); }
    }

    private static void MutateRecord(GraphStorage storage, ulong owner, Func<byte[], bool> mutate)
    {
        using var iterator = storage.GetUnitIterator(owner);
        while (iterator.MoveNext())
        {
            var record = iterator.Current; byte[] bytes = record.Data.ToArray();
            if (!mutate(bytes)) { continue; }
            using var bracket = storage.BeginTransaction();
            storage.UpdateEntry(bracket, record.PageId, record.SlotIndex, bytes); bracket.Commit(); return;
        }
        throw new InvalidOperationException("Expected physical index record.");
    }

    private static GraphStorage OpenImage(string root)
    {
        string directory = Path.Combine(root, "image");
        return GraphStorage.Open(File.Open(Path.Combine(directory, "graph.dat"), FileMode.Open, FileAccess.ReadWrite),
            File.Open(Path.Combine(directory, "graph.log"), FileMode.Open, FileAccess.ReadWrite),
            File.Open(Path.Combine(directory, "graph.bak"), FileMode.Open, FileAccess.ReadWrite), checkpointOnOpen: false);
    }

    private static string TemporaryRoot() => Path.Combine(Path.GetTempPath(), "cohesion-ix-second-fix-" + Guid.NewGuid().ToString("N"));

    private static (string Position, string Operator, object? Label) Anchor(List<object?[]> rows)
    {
        var row = rows.Single(row => row[2] is "LabelScan" or "TextIndexSeek" or "IndexSeek" or "RelationshipTypeScan");
        return (row[5]!.ToString()!.Split(';')[0], (string)row[2]!, row[3]);
    }

    private static async Task<List<object?[]>> RowsAsync(GraphDatabaseSession session, GraphQueryRequest request)
    {
        await using var result = (await session.ExecuteAsync(request, CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }
}
