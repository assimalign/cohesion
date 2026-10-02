using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

/// <summary>
/// Label expressions and predicates have no fixed length or nesting limit (#1139 follow-up, owner
/// decision 2026-10-02: "do what Neo4j does"), through the production GraphDatabaseServer and
/// Graph.Client over Connections.InMemory: 10,000-name chains in MATCH, WHERE and INSERT return the
/// expected rows, and a statement nested deeper than the server thread's stack is a COHDBG007
/// execution failure that keeps the pooled session, where Neo4j reports its transient
/// StackOverFlowError and keeps the connection.
/// </summary>
public sealed class GraphLabelChainWireTests
{
    private const int chainLength = 10_000;

    private static readonly string[] labels = Enumerable.Range(0, 100).Select(i => "L" + i.ToString(CultureInfo.InvariantCulture)).ToArray();

    [Fact(DisplayName = "Cohesion Test [Graph.Client] - Label chains: 10,000-name chains in MATCH, WHERE and INSERT return the expected rows")]
    public async Task QueryAsync_TenThousandNameChains_ShouldReturnExpectedRows()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync("CREATE (:" + string.Join("&", labels) + " {name: 'all'}), (:L0 {name: 'one'}), (:X {name: 'x'})",
            cancellationToken: harness.Token);

        // Act
        long colons = await connection.ExecuteAsync("INSERT (:" + Chain(":") + " {name: 'colons'})", cancellationToken: harness.Token);
        long ampersands = await connection.ExecuteAsync("INSERT (:" + Chain("&") + " {name: 'ampersands'})", cancellationToken: harness.Token);
        var any = await connection.QueryAsync("MATCH (n:" + Chain("|") + ") RETURN n.name AS name", cancellationToken: harness.Token);
        var every = await connection.QueryAsync("MATCH (n:" + Chain("&") + ") RETURN n.name", cancellationToken: harness.Token);
        var labeled = await connection.QueryAsync("MATCH (n) WHERE n IS NOT LABELED " + Chain("|") + " RETURN n.name",
            cancellationToken: harness.Token);
        var predicates = await connection.QueryAsync("MATCH (n) WHERE " +
            string.Join(" AND ", Enumerable.Range(0, chainLength).Select(i => "n:" + labels[i % labels.Length])) + " RETURN n.name",
            cancellationToken: harness.Token);

        // Assert
        colons.ShouldBe(1);
        ampersands.ShouldBe(1);
        any.Columns.Select(column => column.Name).ShouldBe(["name"]);
        any.Select(row => (string?)row[0]).ShouldBe(["all", "one", "colons", "ampersands"]);
        every.Select(row => (string?)row[0]).ShouldBe(["all", "colons", "ampersands"]);
        labeled.ShouldHaveSingleItem()[0].ShouldBe("x");
        predicates.Select(row => (string?)row[0]).ShouldBe(["all", "colons", "ampersands"]);
        connection.IsOpen.ShouldBeTrue();
    }

    /// <summary>
    /// A 10,000-name INSERT disjunction cannot label a node: a coded execution failure, with a
    /// message cut to a readable length, that writes nothing and keeps the session.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Graph.Client] - Label chains: a 10,000-name INSERT disjunction is a COHDBG001 execution failure that keeps the session")]
    public async Task ExecuteAsync_TenThousandNameInsertDisjunction_ShouldFailAndKeepSession()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync("CREATE (:L0 {name: 'one'})", cancellationToken: harness.Token);
        Guid sessionId = harness.Server.Context.Sessions.ShouldHaveSingleItem().Id;

        // Act
        var error = await Should.ThrowAsync<GraphClientException>(async () =>
            await connection.ExecuteAsync("INSERT (:" + Chain("|") + ")", cancellationToken: harness.Token));

        // Assert
        error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        error.Message.ShouldContain("COHDBG001", Case.Sensitive);
        error.Message.Length.ShouldBeLessThan(1_000);
        connection.IsOpen.ShouldBeTrue();
        (await connection.QueryAsync("MATCH (n) RETURN n.name", cancellationToken: harness.Token))
            .ShouldHaveSingleItem()[0].ShouldBe("one");
        harness.Server.Context.Sessions.ShouldHaveSingleItem().Id.ShouldBe(sessionId);
    }

    /// <summary>
    /// 100,000 nested groups are deeper than any server thread's stack. The parse stops with
    /// GQL0009, which the server reports as the COHDBG007 execution failure (the text is within the
    /// language; the thread is too small for it), and the same pooled session serves the next query.
    /// </summary>
    /// <param name="template">The statement, with the nested text at <c>{0}</c>.</param>
    /// <param name="open">The text that opens one level.</param>
    /// <param name="leaf">The innermost text.</param>
    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Label chains: nesting past the server's stack is a COHDBG007 execution failure that keeps the session")]
    [InlineData("MATCH (n:{0}) RETURN n.name", "!(", "L0")]
    [InlineData("MATCH (n) WHERE n:{0} RETURN n.name", "(L1|", "L0")]
    [InlineData("MATCH (n) WHERE {0} RETURN n.name", "(n.name = 'one' AND ", "n.name = 'one'")]
    [InlineData("INSERT (:{0})", "(", "L0")]
    public async Task QueryAsync_NestingPastTheStack_ShouldFailAsCohdbg007AndKeepSession(string template, string open, string leaf)
    {
        // Arrange
        const int depth = 100_000;
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync("CREATE (:L0 {name: 'one'})", cancellationToken: harness.Token);
        Guid sessionId = harness.Server.Context.Sessions.ShouldHaveSingleItem().Id;
        string gql = string.Format(CultureInfo.InvariantCulture, template,
            string.Concat(Enumerable.Repeat(open, depth)) + leaf + new string(')', depth));

        // Act
        var error = await Should.ThrowAsync<GraphClientException>(async () =>
            await connection.QueryAsync(gql, cancellationToken: harness.Token));

        // Assert
        error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        error.Message.ShouldContain("COHDBG007", Case.Sensitive);
        connection.IsOpen.ShouldBeTrue();
        (await connection.QueryAsync("MATCH (n:L0|%) RETURN n.name", cancellationToken: harness.Token))
            .ShouldHaveSingleItem()[0].ShouldBe("one");
        harness.Server.Context.Sessions.ShouldHaveSingleItem().Id.ShouldBe(sessionId);
    }

    private static string Chain(string separator)
        => string.Join(separator, Enumerable.Range(0, chainLength).Select(i => labels[i % labels.Length]));
}
