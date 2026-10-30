using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GqlDiagnosticTests
{
    [Fact(DisplayName = "Cohesion Test [Graph] - Diagnostics: CALL and EXCEPT reach every request seam")]
    public async Task Execute_ErrorStatement_ShouldCarryCompleteDiagnosticsAsync()
    {
        // Arrange
        const string gql = "MATCH (a)->(b) CALL foo() RETURN a EXCEPT MATCH (c) RETURN c";
        var parsed = (GqlQueryStatement)new GqlQueryParser().Parse(gql);
        parsed.Diagnostics.Count().ShouldBe(2);
        await using var engine = GraphDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("diagnostics", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var factory = Should.Throw<DatabaseParseException>(() => GraphQueryRequest.FromGql(gql));
        var paths = Should.Throw<DatabaseParseException>(() => GraphPathsQueryRequest.FromGql(gql));
        var text = await Should.ThrowAsync<DatabaseParseException>(async () => await session.ExecuteAsync(gql, cancellationToken: CancellationToken.None));
        var typed = await Should.ThrowAsync<DatabaseParseException>(async () => await session.ExecuteAsync(new GraphQueryRequest(parsed), CancellationToken.None));

        // Assert
        foreach (var failure in new[] { factory, paths, text, typed })
        {
            failure.Diagnostics.Select(Value).ShouldBe(parsed.Diagnostics.Select(Value));
            failure.Diagnostics.Select(item => item.Start).ShouldBe(failure.Diagnostics.Select(item => item.Start).Order());
            failure.Message.ShouldBe(factory.Message);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Diagnostics: planner rejections carry a source span without code prefixes")]
    public async Task Execute_UnboundVariable_ShouldCarryLocatedDiagnosticAsync()
    {
        // Arrange
        const string gql = "MATCH (a) RETURN absent";
        var parsed = (GqlQueryStatement)new GqlQueryParser().Parse(gql);
        await using var engine = GraphDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("diagnostics", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(gql, cancellationToken: CancellationToken.None));

        // Assert
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CG05001");
        diagnostic.Start.ShouldBe(parsed.GqlExpression.Location!.Start);
        diagnostic.End.ShouldBe(parsed.GqlExpression.Location.End);
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        failure.Message.ShouldNotContain("COHDBG", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Diagnostics: unknown labels retain their structured warning and predicate span")]
    public async Task Execute_UnknownLabel_ShouldCarryLocatedWarningAsync()
    {
        // Arrange
        const string gql = "MATCH (a) WHERE a:Missing RETURN a";
        var parsed = (GqlQueryStatement)new GqlQueryParser().Parse(gql);
        await using var engine = GraphDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("diagnostics", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var result = await session.ExecuteAsync(gql, cancellationToken: CancellationToken.None);

        // Assert
        var warning = result.Diagnostics.ShouldNotBeNull().ShouldHaveSingleItem();
        warning.Code.ShouldBe("CG01010");
        warning.Severity.ShouldBe(DiagnosticSeverity.Warning);
        warning.Start.ShouldBe(parsed.GqlExpression.Predicate!.Location!.Start);
        warning.End.ShouldBe(parsed.GqlExpression.Predicate.Location.End);
        warning.Message.ShouldNotBeNull().ShouldNotContain("COHDBG", Case.Sensitive);
    }

    private static object Value(Diagnostic diagnostic) =>
        (diagnostic.Code, diagnostic.Severity, diagnostic.Message, diagnostic.Start, diagnostic.End, diagnostic.Line, diagnostic.Location);
}
