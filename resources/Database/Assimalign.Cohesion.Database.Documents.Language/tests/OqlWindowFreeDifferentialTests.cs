using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Documents.Language.Internal;
using Assimalign.Cohesion.Database.Documents.Language.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

[Collection("OqlWindowCompatibility")]
public sealed class OqlWindowFreeDifferentialTests
{
    private readonly ITestOutputHelper _output;
    public OqlWindowFreeDifferentialTests(ITestOutputHelper output) { _output = output; }

#if !DEBUG
    private const int sourceRangeSize = 512;
    private static readonly Lazy<(List<(string Text, int Bit, int Ordinal, int Count)> Work, int[] Reserved)> _corpus = new(CreateWork);

    public static IEnumerable<object[]> SourceRanges()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(DataDirectory(), "Expectations.json")));
        int sources = manifest.RootElement.GetProperty("Sources").GetInt32();
        for (int start = 0; start < sources; start += sourceRangeSize)
        { yield return [start, Math.Min(sourceRangeSize, sources - start)]; }
    }

    private static (List<(string Text, int Bit, int Ordinal, int Count)> Work, int[] Reserved) CreateWork()
    {
        using var reservedCalls = JsonDocument.Parse(File.ReadAllText(Path.Combine(DataDirectory(), "ReservedCalls.json")));
        int[] reserved = reservedCalls.RootElement.EnumerateArray().Select(value => value.GetInt32()).ToArray();
        var work = new List<(string Text, int Bit, int Ordinal, int Count)>();
        int candidates = 0, ordinal = 0;
        foreach (string source in ReadStatements("Statements.jsonl.gz"))
        {
            int count = OqlWindowFreeCorpus.CandidateCount(source);
            work.Add((source, candidates, ordinal, count));
            int skipped = reserved.Count(index => index >= ordinal && index < ordinal + count);
            candidates += count - skipped;
            ordinal += count;
        }
        return (work, reserved);
    }

    [Theory(DisplayName = "Cohesion Test [OQL] - Sampled window-free source range preserves base acceptance and stable rendering (#1805)")]
    [MemberData(nameof(SourceRanges))]
    public void Parse_WindowFreeSourceRange_ShouldPreserveBaseAcceptance(int start, int count)
    {
        // Arrange: source ranges own disjoint portions of the b5602544d recording.
        var elapsed = Stopwatch.StartNew();
        var (work, reserved) = _corpus.Value;
        byte[] acceptance = ReadBitmap("Acceptance.bin.gz");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(DataDirectory(), "Expectations.json")));
        var last = work[^1];
        int total = last.Ordinal + last.Count - reserved.Length;
        work.Count.ShouldBe(manifest.RootElement.GetProperty("Sources").GetInt32());
        total.ShouldBe(manifest.RootElement.GetProperty("Candidates").GetInt32());
        reserved.Length.ShouldBe(manifest.RootElement.GetProperty("ExcludedReservedCandidates").GetInt32());
        acceptance.Sum(value => System.Numerics.BitOperations.PopCount((uint)value))
            .ShouldBe(manifest.RootElement.GetProperty("Accepted").GetInt32());
        int end = start + count;
        int firstBit = work[start].Bit, endBit = end == work.Count ? total : work[end].Bit;
        int expected = 0, accepted = 0;
        for (int bit = firstBit; bit < endBit; bit++)
        { if ((acceptance[bit / 8] & (1 << (bit % 8))) != 0) { expected++; } }
        // Act / Assert: each worker owns its parser and traversal stack.
        Parallel.ForEach(work.GetRange(start, count), new ParallelOptions { MaxDegreeOfParallelism = 6 },
            () => (Parser: new OqlQueryParser(true), Nodes: new Stack<OqlExpression>(), Accepted: 0),
            (source, _, worker) =>
            {
                int bit = source.Bit, index = source.Ordinal, variants = 0;
                int nextReserved = Array.BinarySearch(reserved, index);
                if (nextReserved < 0) { nextReserved = ~nextReserved; }
                foreach (string text in OqlWindowFreeCorpus.Candidates(source.Text))
                {
                    bool reservedCall = nextReserved < reserved.Length && index == reserved[nextReserved];
                    index++; variants++;
                    if (reservedCall) { nextReserved++; continue; }
                    bool required = (acceptance[bit / 8] & (1 << (bit % 8))) != 0;
                    bit++;
                    if (!required) { continue; }
                    Check(text, worker.Parser, worker.Nodes);
                    worker.Accepted++;
                }
                if (variants != source.Count) { variants.ShouldBe(source.Count); }
                return worker;
            }, worker => Interlocked.Add(ref accepted, worker.Accepted));
        accepted.ShouldBe(expected);
        _output.WriteLine($"Sources=[{start},{end}); candidates={endBit - firstBit}; base-accepted={accepted}; acceptance/window/round-trip differences=0; seconds={elapsed.Elapsed.TotalSeconds:F3}");
    }

    [Fact(DisplayName = "Cohesion Test [OQL] - Base-accepted renames of rejected sources preserve acceptance and stable rendering (#1805)")]
    public void Parse_RejectedSourceRenames_ShouldPreserveBaseAcceptance()
    {
        // Arrange
        var parser = new OqlQueryParser(true);
        var nodes = new Stack<OqlExpression>();
        int supplemental = 0;
        // Act
        foreach (string text in ReadStatements("RejectedRenames.jsonl.gz"))
        {
            if (OqlWindowFreeCorpus.HasReservedCall(text)) { continue; }
            Check(text, parser, nodes);
            supplemental++;
        }
        // Assert
        supplemental.ShouldBe(441);
    }
#endif

    private static void Check(string text, OqlQueryParser parser, Stack<OqlExpression> nodes)
    {
        var statement = (OqlQueryStatement)parser.Parse(text);
        if (statement.Diagnostics.Any()) { statement.Diagnostics.ShouldBeEmpty(text); }
        var window = FindNode(statement.OqlExpression, nodes, static node => node is OqlWindowExpression or OqlWindowDefinition);
        if (window is not null) { window.ShouldBeNull(text); }
        if (OqlWindowFreeCorpus.Canonical(statement.OqlExpression) is not string canonical) { return; }
        var reparsed = (OqlQueryStatement)parser.Parse(canonical);
        if (reparsed.Diagnostics.Any()) { reparsed.Diagnostics.ShouldBeEmpty(canonical); }
        string? rendered = OqlWindowFreeCorpus.Canonical(reparsed.OqlExpression);
        if (rendered != canonical) { rendered.ShouldBe(canonical, text); }
    }

    private static OqlExpression? FindNode(OqlExpression expression, Stack<OqlExpression> nodes, Predicate<OqlExpression> match)
    {
        nodes.Clear();
        nodes.Push(expression);
        while (nodes.TryPop(out var node))
        {
            if (match(node)) { return node; }
            if (node is OqlLiteralExpression or OqlPathExpression or OqlParameterExpression or OqlStarExpression or OqlMissingExpression)
            { continue; }
            // Avoid an iterator allocation for frequent scalar shapes. Statement
            // wrappers own their children; the renderer covers query/scalar children.
            if (node is OqlBinaryExpression binary) { nodes.Push(binary.Left); nodes.Push(binary.Right); continue; }
            if (node is OqlUnaryExpression unary) { nodes.Push(unary.Operand); continue; }
            if (node is OqlCallExpression call) { foreach (var argument in call.Arguments) { nodes.Push(argument); } continue; }
            if (node is OqlArrayExpression array) { foreach (var element in array.Elements) { nodes.Push(element); } continue; }
            if (node is OqlExplainExpression explain) { nodes.Push(explain.Statement); continue; }
            if (node is OqlInsertExpression insert)
            {
                if (insert.Select is not null) { nodes.Push(insert.Select); }
                foreach (var pair in insert.Values) { nodes.Push(pair.Key); nodes.Push(pair.Value); }
                if (insert.Returning is not null) { nodes.Push(insert.Returning); }
                continue;
            }
            if (node is OqlUpsertExpression upsert)
            {
                if (upsert.Select is not null) { nodes.Push(upsert.Select); }
                foreach (var pair in upsert.Values) { nodes.Push(pair.Key); nodes.Push(pair.Value); }
                if (upsert.Returning is not null) { nodes.Push(upsert.Returning); }
                continue;
            }
            if (node is OqlUpdateExpression update)
            {
                foreach (var assignment in update.Assignments) { nodes.Push(assignment.Value); }
                if (update.Predicate is not null) { nodes.Push(update.Predicate); }
                if (update.Returning is not null) { nodes.Push(update.Returning); }
                continue;
            }
            if (node is OqlDeleteExpression delete)
            {
                if (delete.Predicate is not null) { nodes.Push(delete.Predicate); }
                if (delete.Returning is not null) { nodes.Push(delete.Returning); }
                continue;
            }
            foreach (var child in OqlMergeRenderer.Children(node)) { nodes.Push(child); }
        }
        return null;
    }

    [Fact(DisplayName = "Cohesion Test [OQL] - Window-free traversal reaches SELECT below INSERT and UNION (#1805)")]
    public void Walk_InsertSelectCompound_ShouldReachSelect()
    {
        // Arrange
        const string text = "INSERT INTO target (KEY k) SELECT 'k' AS k FROM c GROUP BY LOWER(c.name) over UNION SELECT 'j' AS k FROM d";
        var statement = (OqlQueryStatement)new OqlQueryParser().Parse(text);
        statement.Diagnostics.ShouldBeEmpty();
        // Act
        var select = FindNode(statement.OqlExpression, new Stack<OqlExpression>(),
            static node => node is OqlSelectExpression query && query.GroupAliases.Contains("over"));
        // Assert
        select.ShouldNotBeNull().ShouldBeOfType<OqlSelectExpression>().GroupAliases.ShouldHaveSingleItem().ShouldBe("over");
    }

    [Theory(DisplayName = "Cohesion Test [OQL] - Window-free traversal detects window nodes throughout EXPLAIN and DML (#1805)")]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)]
    [InlineData(10)] [InlineData(11)] [InlineData(12)] [InlineData(13)] [InlineData(14)]
    public void Walk_ExplainAndDmlChildren_ShouldFindWindow(int site)
    {
        // Arrange: direct ASTs exercise even positions the text parser already refuses.
        var query = ((OqlQueryStatement)new OqlQueryParser().Parse("SELECT RANK() OVER () AS n FROM c")).OqlExpression.ShouldBeOfType<OqlSelectExpression>();
        var window = query.Projections[0].Expression;
        var value = new OqlLiteralExpression("k");
        var returning = new OqlReturningExpression([new OqlProjection(window, "n")]);
        var path = ((OqlQueryStatement)new OqlQueryParser().Parse("SELECT c.k FROM c")).OqlExpression.ShouldBeOfType<OqlSelectExpression>().Projections[0].Expression.ShouldBeOfType<OqlPathExpression>();
        OqlExpression[] roots =
        [
            new OqlExplainExpression(query, null),
            new OqlInsertExpression("c", null, [], query, "n", null),
            new OqlInsertExpression("c", null, [(window, value)], null, null, null),
            new OqlInsertExpression("c", null, [(value, window)], null, null, null),
            new OqlInsertExpression("c", null, [], null, null, null, returning: returning),
            new OqlUpsertExpression("c", null, [], query, "n", null),
            new OqlUpsertExpression("c", null, [(window, value)], null, null, null),
            new OqlUpsertExpression("c", null, [(value, window)], null, null, null),
            new OqlUpsertExpression("c", null, [], null, null, null, returning: returning),
            new OqlUpdateExpression("c", null, [new OqlUpdateAssignment(path, window)], [], null),
            new OqlUpdateExpression("c", null, [], [], window),
            new OqlUpdateExpression("c", null, [], [], null, returning: returning),
            new OqlDeleteExpression("c", null, window),
            new OqlDeleteExpression("c", null, null, returning: returning),
            new OqlExplainExpression(new OqlInsertExpression("c", null, [], query, "n", null), null),
        ];
        // Act
        var found = FindNode(roots[site], new Stack<OqlExpression>(), static node => node is OqlWindowExpression or OqlWindowDefinition);
        // Assert
        found.ShouldBeSameAs(window);
    }

    internal static byte[] ReadBitmap(string name)
    {
        using var input = new GZipStream(File.OpenRead(Path.Combine(DataDirectory(), name)), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    internal static IEnumerable<string> ReadStatements(string name)
    {
        using var input = new StreamReader(new GZipStream(File.OpenRead(Path.Combine(DataDirectory(), name)), CompressionMode.Decompress));
        while (input.ReadLine() is string line)
        {
            using var source = JsonDocument.Parse(line);
            yield return source.RootElement.GetString()!;
        }
    }

    internal static string DataDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "resources", "Database")))
        { directory = directory.Parent; }
        return Path.Combine(directory.ShouldNotBeNull().FullName, "resources", "Database",
            "Assimalign.Cohesion.Database.Documents.Language", "tests", "TestObjects", "WindowFreeCompatibility");
    }
}
