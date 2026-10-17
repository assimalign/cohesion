using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Language;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class OqlDiagnosticsFixPassTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - Diagnostics: typed evaluator rejections keep their innermost location")]
    [InlineData("binary", "COHDOC001")]
    [InlineData("unary", "COHDOC001")]
    [InlineData("context", "COHDOC001")]
    [InlineData("arity", "COHDOC001")]
    [InlineData("aggregate", "COHDOC001")]
    [InlineData("json", "COHDOC007")]
    [InlineData("value", "COHDOC008")]
    [InlineData("double", "COHDOC007")]
    public void Evaluate_TypedInvalidExpression_ShouldCarryLocatedDiagnostic(string scenario, string code)
    {
        // Arrange
        var location = Location.Create(1, 8, 7, 15);
        var literal = new OqlLiteralExpression(1m);
        using var json = JsonDocument.Parse("1e100");
        OqlExpression expression = scenario switch
        {
            "binary" => new OqlBinaryExpression(literal, "INVALID", literal, location),
            "unary" => new OqlUnaryExpression("INVALID", literal, location),
            "context" => new OqlCallExpression("SUM", [literal], location),
            "arity" => new OqlCallExpression("SUM", [], location),
            "aggregate" => new OqlCallExpression("INVALID", [literal], location),
            "json" => new OqlLiteralExpression(json.RootElement, location),
            "double" => new OqlLiteralExpression(double.MaxValue, location),
            _ => new OqlLiteralExpression(new object(), location),
        };
        var evaluator = new DocumentExpressionEvaluator(null, null);

        // Act
        var failure = Should.Throw<DatabaseException>(() => evaluator.Evaluate(expression, default,
            scenario == "context" ? null : [default]));

        // Assert
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(code);
        diagnostic.Start.ShouldBe(location.Start);
        diagnostic.End.ShouldBe(location.End);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Diagnostics: direct catalog collection writes carry an unlocated code")]
    public async Task CreateCollection_CatalogWrite_ShouldCarryUnlocatedDiagnosticAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("diagnostics", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.CreateCollectionAsync(
            "COHESION_SCHEMA.OBJECT_OWNERSHIP", CancellationToken.None));

        // Assert
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("COHDOC004");
        diagnostic.Start.ShouldBeNull();
        diagnostic.End.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Diagnostics: catalog index writes carry a code and span")]
    [InlineData("CREATE INDEX ix ON COHESION_SCHEMA.OBJECT_OWNERSHIP(NAME)")]
    [InlineData("DROP INDEX ix ON COHESION_SCHEMA.OBJECT_OWNERSHIP")]
    public async Task Execute_CatalogWrite_ShouldCarryLocatedDiagnosticAsync(string oql)
    {
        // Arrange
        var request = DocumentQueryRequest.FromOql(oql);
        await using var engine = DocumentDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("diagnostics", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None));

        // Assert
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("COHDOC004");
        diagnostic.Start.ShouldBe(request.Statement.OqlExpression.Location!.Start);
        diagnostic.End.ShouldBe(request.Statement.OqlExpression.Location.End);
        diagnostic.Message.ShouldBe(failure.Message);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Diagnostics: evaluator faults carry codes and expression spans")]
    [InlineData("1 / 0", "COHDOC006")]
    [InlineData("1 % 0", "COHDOC006")]
    [InlineData("$missing", "COHDOC005")]
    [InlineData("'text' + 1", "COHDOC008")]
    [InlineData("79228162514264337593543950335 * 2", "COHDOC007")]
    public async Task Execute_EvaluationFault_ShouldCarryExpressionSpanAsync(string text, string code)
    {
        // Arrange
        string oql = $"SELECT {text} FROM COHESION_SCHEMA.OBJECT_OWNERSHIP";
        var request = DocumentQueryRequest.FromOql(oql);
        var expression = ((OqlSelectExpression)request.Statement.OqlExpression).Projections.Single().Expression;
        await using var engine = DocumentDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("diagnostics", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None));

        // Assert
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(code);
        diagnostic.Start.ShouldBe(expression.Location!.Start);
        diagnostic.End.ShouldBe(expression.Location.End);
        diagnostic.Message.ShouldBe(failure.Message);
    }
}
