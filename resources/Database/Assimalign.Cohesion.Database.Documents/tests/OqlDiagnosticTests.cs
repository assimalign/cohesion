using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class OqlDiagnosticTests
{
    [Fact(DisplayName = "Cohesion Test [Documents] - Diagnostics: JOIN and nested SELECT reach every request seam")]
    public async Task Execute_ErrorStatement_ShouldCarryCompleteDiagnosticsAsync()
    {
        // Arrange
        const string oql = "SELECT (SELECT a FROM nested) FROM items JOIN other ON items.a = other.a";
        var parsed = (OqlQueryStatement)new OqlQueryParser().Parse(oql);
        parsed.Diagnostics.Count().ShouldBe(2);
        await using var engine = DocumentDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("diagnostics", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var factory = Should.Throw<DatabaseParseException>(() => DocumentQueryRequest.FromOql(oql));
        var text = await Should.ThrowAsync<DatabaseParseException>(async () => await session.ExecuteAsync(oql, cancellationToken: CancellationToken.None));
        var typed = await Should.ThrowAsync<DatabaseParseException>(async () => await session.ExecuteAsync(new DocumentQueryRequest(parsed), CancellationToken.None));

        // Assert
        foreach (var failure in new[] { factory, text, typed })
        {
            failure.Diagnostics.Select(Value).ShouldBe(parsed.Diagnostics.Select(Value));
            failure.Diagnostics.Select(item => item.Start).ShouldBe(failure.Diagnostics.Select(item => item.Start).Order());
            failure.Message.ShouldBe(factory.Message);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Diagnostics: duplicate projection names carry a code and source span")]
    public async Task Execute_DuplicateProjection_ShouldCarryLocatedDiagnosticAsync()
    {
        // Arrange
        const string oql = "SELECT a AS same, b AS same FROM items";
        var parsed = (OqlQueryStatement)new OqlQueryParser().Parse(oql);
        var location = ((OqlSelectExpression)parsed.OqlExpression).Projections[1].Expression.Location;
        await using var engine = DocumentDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("diagnostics", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(oql, cancellationToken: CancellationToken.None));

        // Assert
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("COHDOC003");
        diagnostic.Start.ShouldBe(location!.Start);
        diagnostic.End.ShouldBe(location.End);
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        failure.Message.ShouldNotContain("COHDOC", Case.Sensitive);
        failure.Message.ShouldNotContain("COHDBL001:", Case.Sensitive);
    }

    private static object Value(Diagnostic diagnostic) =>
        (diagnostic.Code, diagnostic.Severity, diagnostic.Message, diagnostic.Start, diagnostic.End, diagnostic.Line, diagnostic.Location);
}
