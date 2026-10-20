using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection("Graph wire diagnostics")]
public sealed class GraphWireDiagnosticTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - Wire: empty statements carry GQL0001 and their complete span")]
    [InlineData("", false)]
    [InlineData(" \n  ", false)]
    [InlineData("", true)]
    [InlineData(" \n  ", true)]
    public async Task Execute_EmptyStatement_ShouldCarryLocatedParseFailureAsync(string text, bool paths)
    {
        // Arrange
        await using var harness = await GraphServerHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();
        var session = harness.Server.Sessions.Single().DatabaseSession.ShouldNotBeNull();
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        DatabaseException? captured = null;
        void Capture(object? sender, FirstChanceExceptionEventArgs args)
        {
            if (args.Exception is DatabaseParseException failure && failure.Message == "A graph statement must not be empty.")
            {
                Interlocked.CompareExchange(ref captured, failure, null);
            }
        }
        AppDomain.CurrentDomain.FirstChanceException += Capture;
        ProtocolErrorMessage error;

        // Act
        try
        {
            await client.SendAsync(paths ? GraphProtocolMessageType.ExecutePaths : GraphProtocolMessageType.Execute,
                GraphProtocolExecuteMessage.Create(text).Encode());
            var frame = await client.ExpectAsync(ProtocolMessageType.Error);
            error = ProtocolErrorMessage.Decode(frame.Payload.Span);
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= Capture; }

        // Assert
        AssertDiagnostic(captured.ShouldNotBeNull(), "GQL0001", Location.Create(1, 1, 0, text.Length));
        error.Code.ShouldBe(ProtocolErrorCode.ParseFailure);
        error.Message.ShouldBe("GQL0001: A graph statement must not be empty.");
        transaction.State.ShouldBe(Transactions.TransactionState.Active);
        await client.SendAsync(ProtocolMessageType.Ping);
        await client.ExpectAsync(ProtocolMessageType.Pong);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Wire: scalar exchanges reject nodes, relationships and paths with a query span")]
    [InlineData("MATCH (n) RETURN n")]
    [InlineData("MATCH (a)-[r]->(b) RETURN r")]
    [InlineData("MATCH p = (a)->(b) RETURN p")]
    public async Task Execute_PlannedEntityProjection_ShouldCarryLocatedRejectionAsync(string text)
    {
        // Arrange
        await using var harness = await GraphServerHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();
        var session = (GraphDatabaseServerSession)harness.Server.Sessions.Single();
        var location = GraphQueryRequest.FromGql(text).Statement.GqlExpression.Location!;

        // Act
        var request = ParseScalarRequest(session, text, null);
        request.ScalarsOnly.ShouldBeTrue();
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.DatabaseSession!.ExecuteAsync(request, CancellationToken.None));
        await client.SendAsync(GraphProtocolMessageType.Execute, GraphProtocolExecuteMessage.Create(text).Encode());
        var frame = await client.ExpectAsync(ProtocolMessageType.Error);
        var error = ProtocolErrorMessage.Decode(frame.Payload.Span);

        // Assert
        AssertDiagnostic(failure, "COHDBG001", location);
        error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        error.Message.ShouldBe("COHDBG001: " + failure.Message);
        await client.SendAsync(ProtocolMessageType.Ping);
        await client.ExpectAsync(ProtocolMessageType.Pong);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Wire: an unexpected path result carries the exchange text span")]
    public async Task WritePathsAsync_UnexpectedResult_ShouldCarryExchangeSpanAsync()
    {
        // Arrange
        const string text = "MATCH (n) RETURN n";
        await using var harness = await GraphServerHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();
        await client.SendAsync(GraphProtocolMessageType.ExecutePaths, GraphProtocolExecuteMessage.Create(text).Encode());
        await client.ExpectAsync((ProtocolMessageType)GraphProtocolMessageType.PathsComplete);
        var session = (GraphDatabaseServerSession)harness.Server.Sessions.Single();

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => WritePathsAsync(session, new RejectedGraphResult([]), CancellationToken.None));

        // Assert
        AssertDiagnostic(failure, "COHDBG001", Location.Create(1, 1, 0, text.Length));
        failure.Message.ShouldBe("A path request did not produce a graph path result.");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Wire: rejected results preserve every diagnostic and select an unprefixed error message")]
    public async Task WriteResultAsync_RejectedResult_ShouldPreserveDiagnosticCarrierAsync()
    {
        // Arrange
        var warning = new Diagnostic { Code = "COHDBG010", Message = "Unknown label.", Severity = DiagnosticSeverity.Warning,
            Location = DiagnosticLocation.Absolute, Start = 7, End = 12, Line = 2 };
        var error = new Diagnostic { Code = "COHDBG001", Message = "Invalid graph request.", Severity = DiagnosticSeverity.Error,
            Location = DiagnosticLocation.Absolute, Start = 20, End = 25, Line = 3 };
        await using var harness = await GraphServerHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();
        var session = (GraphDatabaseServerSession)harness.Server.Sessions.Single();
        var result = new RejectedGraphResult([warning, error]);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => WriteResultAsync(session, result, new DatabaseKeyWriter(), CancellationToken.None));

        // Assert
        failure.Diagnostics.Count.ShouldBe(2);
        failure.Diagnostics[0].ShouldBe(warning);
        failure.Diagnostics[1].ShouldBe(error);
        failure.Diagnostics[1].Start.ShouldBe(20);
        failure.Diagnostics[1].End.ShouldBe(25);
        failure.Diagnostics[1].Line.ShouldBe(3);
        failure.Message.ShouldBe(error.Message);
    }

    private static void AssertDiagnostic(DatabaseException failure, string code, Location location)
    {
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(code);
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.Location.ShouldBe(DiagnosticLocation.Absolute);
        diagnostic.Start.ShouldBe(location.Start);
        diagnostic.End.ShouldBe(location.End);
        diagnostic.Line.ShouldBe(location.StartLine);
        diagnostic.Message.ShouldBe(failure.Message);
        failure.Message.ShouldNotContain(code + ":", Case.Sensitive);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ParseScalarRequest")]
    private static extern GraphQueryRequest ParseScalarRequest(GraphDatabaseServerSession session, string statement,
        System.Collections.Generic.IReadOnlyDictionary<string, object?>? parameters);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "WritePathsAsync")]
    private static extern Task WritePathsAsync(GraphDatabaseServerSession session, DatabaseResult result, CancellationToken cancellationToken);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "WriteResultAsync")]
    private static extern Task WriteResultAsync(GraphDatabaseServerSession session, DatabaseResult result, DatabaseKeyWriter rowWriter, CancellationToken cancellationToken);
}
