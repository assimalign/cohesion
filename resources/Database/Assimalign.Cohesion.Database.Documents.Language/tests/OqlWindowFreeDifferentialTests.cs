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
    [Fact(DisplayName = "Cohesion Test [OQL] - Sampled window-free corpus preserves base acceptance and stable rendering (#1805)")]
    public void Parse_WindowFreeCorpus_ShouldPreserveBaseAcceptance()
    {
        // Arrange: the bitmap is sliced from the executed b5602544d recording.
        string root = DataDirectory();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Expectations.json")));
        byte[] acceptance = ReadBitmap("Acceptance.bin.gz");
        using var reservedCalls = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "ReservedCalls.json")));
        int[] reserved = reservedCalls.RootElement.EnumerateArray().Select(value => value.GetInt32()).ToArray();
        var parser = new OqlQueryParser(true);
        int sources = 0, candidates = 0, accepted = 0, excluded = 0;
        int ordinal = 0;
        var elapsed = Stopwatch.StartNew();
        var work = new List<(string Text, int Bit, int Ordinal, int Count)>();
        foreach (string source in ReadStatements("Statements.jsonl.gz"))
        {
            int count = OqlWindowFreeCorpus.Candidates(source).Count();
            work.Add((source, candidates, ordinal, count));
            int skipped = reserved.Count(index => index >= ordinal && index < ordinal + count);
            candidates += count - skipped;
            ordinal += count;
            excluded += skipped;
            sources++;
        }
        // Act / Assert: bounded independent parsers keep the Release corpus within CI's
        // budget. Each source owns a disjoint bitmap range and each worker owns its parser.
        Parallel.ForEach(work, new ParallelOptions { MaxDegreeOfParallelism = 6 },
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
        int supplemental = 0;
        var nodes = new Stack<OqlExpression>();
        foreach (string text in ReadStatements("RejectedRenames.jsonl.gz"))
        {
            if (OqlWindowFreeCorpus.HasReservedCall(text)) { continue; }
            Check(text, parser, nodes);
            supplemental++;
        }
        sources.ShouldBe(manifest.RootElement.GetProperty("Sources").GetInt32());
        candidates.ShouldBe(manifest.RootElement.GetProperty("Candidates").GetInt32());
        accepted.ShouldBe(manifest.RootElement.GetProperty("Accepted").GetInt32());
        excluded.ShouldBe(manifest.RootElement.GetProperty("ExcludedReservedCandidates").GetInt32());
        supplemental.ShouldBe(441);
        _output.WriteLine($"Sources={sources}; candidates={candidates}; base-accepted={accepted}; reserved-call exclusions={excluded}; supplemental={supplemental}; acceptance/window/round-trip differences=0; seconds={elapsed.Elapsed.TotalSeconds:F3}");
    }
#endif

    private static void Check(string text, OqlQueryParser parser, Stack<OqlExpression> nodes)
    {
        var statement = (OqlQueryStatement)parser.Parse(text);
        if (statement.Diagnostics.Any()) { statement.Diagnostics.ShouldBeEmpty(text); }
        nodes.Clear();
        nodes.Push(statement.OqlExpression);
        while (nodes.TryPop(out var node))
        {
            if (node is OqlWindowExpression or OqlWindowDefinition) { false.ShouldBeTrue(text); }
            if (node is OqlLiteralExpression or OqlPathExpression or OqlParameterExpression or OqlStarExpression or OqlMissingExpression)
            { continue; }
            // Avoid an iterator allocation for the frequent scalar shapes; all other
            // nodes, including future additions, use the renderer's complete child walk.
            if (node is OqlBinaryExpression binary) { nodes.Push(binary.Left); nodes.Push(binary.Right); continue; }
            if (node is OqlUnaryExpression unary) { nodes.Push(unary.Operand); continue; }
            if (node is OqlCallExpression call) { foreach (var argument in call.Arguments) { nodes.Push(argument); } continue; }
            if (node is OqlArrayExpression array) { foreach (var element in array.Elements) { nodes.Push(element); } continue; }
            foreach (var child in OqlMergeRenderer.Children(node)) { nodes.Push(child); }
        }
        if (OqlWindowFreeCorpus.Canonical(statement.OqlExpression) is not string canonical) { return; }
        var reparsed = (OqlQueryStatement)parser.Parse(canonical);
        if (reparsed.Diagnostics.Any()) { reparsed.Diagnostics.ShouldBeEmpty(canonical); }
        string? rendered = OqlWindowFreeCorpus.Canonical(reparsed.OqlExpression);
        if (rendered != canonical) { rendered.ShouldBe(canonical, text); }
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
