using System;
using System.Linq;
using System.Threading;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public sealed class GqlCallParserTests
{
    private readonly ITestOutputHelper _output;

    public GqlCallParserTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - CALL scopes and nested composites retain semantic AST and render")]
    [InlineData("CALL { RETURN 1 AS y } RETURN y", false, false, -1)]
    [InlineData("CALL () { RETURN 1 AS y } RETURN y", false, false, 0)]
    [InlineData("LET x=1 CALL (x) { RETURN x+1 AS y } RETURN y", false, false, 1)]
    [InlineData("LET x=1 CALL (*) { RETURN x+1 AS y } RETURN y", false, true, 0)]
    [InlineData("OPTIONAL CALL () { RETURN 1 AS y } RETURN y", true, false, 0)]
    [InlineData("LET a=1,b=2 CALL (a,b) { (RETURN a AS y UNION RETURN b AS y) NEXT RETURN y } RETURN y", false, false, 2)]
    [InlineData("CALL { CALL { RETURN {call:1}.call AS z } RETURN z AS y } RETURN y", false, false, -1)]
    public void Parse_InlineCall_ShouldPreserveScopeAndRoundTrip(string source, bool optional, bool all, int imports)
    {
        // Arrange / Act
        var parsed = (GqlQueryStatement)new GqlQueryParser().Parse(source);
        // Assert
        parsed.Diagnostics.ShouldBeEmpty();
        var call = parsed.GqlExpression.Stages.OfType<GqlCallStage>().ShouldHaveSingleItem();
        call.Optional.ShouldBe(optional); call.ImportAll.ShouldBe(all); (call.Imports?.Count ?? -1).ShouldBe(imports);
        call.Location!.Start.ShouldBe(source.IndexOf(optional ? "OPTIONAL" : "CALL", StringComparison.Ordinal));
        var rendered = parsed.GqlExpression.ToString();
        var again = (GqlQueryStatement)new GqlQueryParser().Parse(rendered);
        again.Diagnostics.ShouldBeEmpty();
        again.GqlExpression.ToString().ShouldBe(rendered);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - CALL named procedures, batching and nested capabilities stay refused")]
    [InlineData("CALL db.labels() YIELD label RETURN label")]
    [InlineData("OPTIONAL CALL proc() YIELD x RETURN x")]
    [InlineData("CALL () { RETURN 1 AS x } IN TRANSACTIONS RETURN x")]
    [InlineData("CALL { RETURN 1 AS x UNION (CALL named()) } RETURN x")]
    [InlineData("CALL { MATCH (n) FOREACH (x IN [1] | SET n.x=x) RETURN 1 AS y } RETURN y")]
    [InlineData("CALL { RETURN 1 AS x YIELD x } RETURN x")]
    public void Parse_ExcludedCall_ShouldReportCapability(string source)
    {
        // Arrange / Act
        var parsed = new GqlQueryParser().Parse(source);
        // Assert
        parsed.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK00001");
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - nested CALL bodies share source text and render only their own tree (#1807)")]
    public void Parse_NestedBodies_ShouldShareStatementText()
    {
        // Arrange
        string source = Nested(100, "/* retained filler */ RETURN 1 AS y");
        var parsed = (GqlQueryStatement)new GqlQueryParser().Parse(source);
        parsed.Diagnostics.ShouldBeEmpty();
        var body = parsed.GqlExpression;
        string retainedSource = body.Text!;
        // Act / Assert
        for (int i = 0; i < 100; i++)
        {
            body = body.Stages.OfType<GqlCallStage>().Single().Body;
            ReferenceEquals(body.Text, retainedSource).ShouldBeTrue();
        }
        body.ToString().ShouldBe("RETURN 1 AS \"y\"");
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - CALL allocation does not multiply retained comments by depth (#1807)")]
    public void Parse_NestedComments_ShouldAllocateLinearly()
    {
        // Arrange
        string leaf = "/*" + new string('x', 256 * 1024) + "*/ RETURN 1 AS y";
        string small = Nested(10, leaf), large = Nested(100, leaf);
        Measure(small); Measure(large);
        // Act
        long low = Measure(small), high = Measure(large);
        // Assert: filler dominates the small case; cloning it at every level exceeds this bound.
        _output.WriteLine($"CALL parse allocations: depth 10={low}; depth 100={high}; ratio={(double)high/low:F3}");
        high.ShouldBeLessThan(low * 4);

        static long Measure(string source)
        {
            long minimum = long.MaxValue;
            for (int round = 0; round < 3; round++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                var parsed = new GqlQueryParser().Parse(source);
                long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                parsed.Diagnostics.ShouldBeEmpty();
                GC.KeepAlive(parsed);
                minimum = Math.Min(minimum, bytes);
            }
            return minimum;
        }
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - parser-built NEXT bodies keep parsed provenance (#1807)")]
    [InlineData("CALL () { RETURN 1 AS x NEXT RETURN x AS y } RETURN y")]
    [InlineData("(RETURN 1 AS x NEXT RETURN x AS y) UNION RETURN 2 AS y")]
    public void Parse_NextOperands_ShouldRetainStatementText(string source)
    {
        // Arrange / Act
        var parsed = (GqlQueryStatement)new GqlQueryParser().Parse(source);
        // Assert
        parsed.Diagnostics.ShouldBeEmpty();
        var body = parsed.GqlExpression.Composite?.Left ?? parsed.GqlExpression.Stages.OfType<GqlCallStage>().Single().Body;
        ReferenceEquals(body.Text, parsed.GqlExpression.Text).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - CALL capability scan reports one stack diagnostic (#1807)")]
    public void Parse_CallPastStack_ShouldReportOneComplexityDiagnostic()
    {
        // Arrange
        string source = Nested(10000, "RETURN 1 AS y");
        GqlQueryStatement? parsed = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { parsed = (GqlQueryStatement)new GqlQueryParser().Parse(source); }
            catch (Exception exception) { error = exception; }
        }, 512 * 1024);
        // Act
        thread.Start(); thread.Join(TimeSpan.FromSeconds(30)).ShouldBeTrue();
        // Assert
        error.ShouldBeNull();
        parsed!.Diagnostics.Count().ShouldBe(1);
        parsed.Diagnostics.Single().Code.ShouldBe("CG00009");
    }

    private static string Nested(int depth, string leaf) => string.Concat(Enumerable.Repeat("CALL () { ", depth)) +
        leaf + string.Concat(Enumerable.Repeat(" } RETURN y", depth));

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - malformed CALL syntax is located")]
    [InlineData("CALL (x,) { RETURN 1 AS y } RETURN y")]
    [InlineData("CALL (*) RETURN 1 AS y")]
    [InlineData("CALL (x { RETURN 1 AS y } RETURN y")]
    [InlineData("CALL { RETURN 1 AS y RETURN y")]
    public void Parse_MalformedCall_ShouldReportSyntax(string source)
    {
        // Arrange / Act
        var parsed = new GqlQueryParser().Parse(source);
        // Assert
        parsed.Diagnostics.ShouldNotBeEmpty();
        parsed.Diagnostics.All(diagnostic => diagnostic.Start is not null && diagnostic.Code == "CG00002").ShouldBeTrue(
            string.Join("; ", parsed.Diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message)));
    }
}
