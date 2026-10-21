using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection("SQL definition diagnostics")]
public sealed class SqlDefinitionDiagnosticTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Definitions: statement admission carries COHSQLD001 and the offending span")]
    [InlineData("CREATE FUNCTION f(p absent_type) RETURNS INT RETURN p", "statement")]
    [InlineData("CREATE FUNCTION f(p INT, p INT) RETURNS INT RETURN p", "statement")]
    [InlineData("CREATE FUNCTION f(p INT) RETURNS INT RETURN f(p)", "body")]
    [InlineData("CREATE FUNCTION native_echo(p INT) RETURNS INT RETURN p", "statement")]
    [InlineData("CREATE FUNCTION f(p INT) RETURNS INT IMMUTABLE RETURN weak(p)", "body")]
    [InlineData("CREATE DOMAIN native_domain AS INT", "statement")]
    [InlineData("CREATE DOMAIN nested_domain AS base_domain", "statement")]
    [InlineData("DROP DOMAIN absent;", "statement")]
    [InlineData("DROP FUNCTION absent(INT);", "statement")]
    public async Task Execute_InvalidDefinition_ShouldCarryLocatedDiagnosticAsync(string sql, string span)
    {
        // Arrange
        var request = SqlQueryRequest.FromSql(" \n" + sql);
        var expression = request.Statement.SqlExpression;
        var location = span switch
        {
            "body" => ((SqlCreateFunctionExpression)expression).Body.Location!,
            "check" => ((SqlCreateTableExpression)expression).Constraints.Single().CheckExpression!.Location!,
            _ => expression.Location!,
        };
        var builder = SqlDatabaseEngine.CreateBuilder("definition-diagnostics");
        builder.Functions.Add(SqlScalarFunction.Create("native_echo", static (int value) => value));
        builder.Functions.Add(SqlScalarFunction.Create("weak", static (int value) => value));
        builder.Types.Add(SqlType.CreateDomain("native_domain", SqlType.Integer));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("definitions", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE DOMAIN base_domain AS INT", cancellationToken: CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None));

        // Assert
        AssertDiagnostic(failure, location);
        database.Catalog.Tables.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Definitions: malformed typed declarations keep the declaration location")]
    [InlineData("function-name")]
    [InlineData("domain-name")]
    [InlineData("volatility")]
    public void Declare_MalformedTypedDeclaration_ShouldCarryDeclarationSpan(string scenario)
    {
        // Arrange
        var template = (SqlCreateFunctionExpression)SqlQueryRequest.FromSql("CREATE FUNCTION f(p INT) RETURNS INT RETURN p").Statement.SqlExpression;
        var location = Location.Create(3, 3, 15, 70);
        Action declare = scenario == "domain-name"
            ? () => SqlDefinitionBinder.Declare(SqlDefinitionSyntax.Domain("", "INT", null, false, null, null, location),
                SqlFunctionEnvironment.Standard, Collation.Binary, null)
            : () => SqlDefinitionBinder.Declare(SqlDefinitionSyntax.Function(scenario == "function-name" ? "" : "f", template.Parameters,
                "INT", scenario == "volatility" ? "INVALID" : "VOLATILE", false, template.Body, location),
                SqlFunctionEnvironment.Standard, Collation.Binary, null);

        // Act
        var failure = Should.Throw<DatabaseException>(declare);

        // Assert
        AssertDiagnostic(failure, location);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Definitions: typed expression defaults carry their expression span")]
    public void Declare_ExpressionDomainDefault_ShouldCarryDefaultSpan()
    {
        // Arrange
        var value = Expression("1 + 2");
        var create = SqlDefinitionSyntax.Domain("invalid_default", "INT", value, false, null, null, Location.Create(1, 1, 0, 80));

        // Act
        var failure = Should.Throw<DatabaseException>(() => SqlDefinitionBinder.Declare(create, SqlFunctionEnvironment.Standard, Collation.Binary, null));

        // Assert
        AssertDiagnostic(failure, value.Location!);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Definitions: standalone type resolution locates the retained type spelling")]
    public void ResolveType_UnknownType_ShouldCarryTypeFragmentSpan()
    {
        // Arrange
        const string type = "absent_type";

        // Act
        var failure = Should.Throw<DatabaseException>(() => SqlDefinitionBinder.ResolveType(type, SqlFunctionEnvironment.Standard));

        // Assert
        AssertDiagnostic(failure, Location.Create(1, 1, 0, type.Length));
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Definitions: persisted metadata rejections locate the retained canonical fragment")]
    [InlineData("signature")]
    [InlineData("cycle")]
    [InlineData("native-domain")]
    public async Task Bind_InvalidPersistedMetadata_ShouldCarryFragmentSpanAsync(string scenario)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("definition-diagnostics", new());
        var database = await engine.CreateDatabaseAsync("definitions", CancellationToken.None);
        string signature = SqlDefinitionBinder.Signature("f", [SqlType.Integer]);
        var function = new SqlCatalogFunction("f", scenario == "signature" ? "bad" : signature, ["p"], ["INT"], "INT", "IMMUTABLE", false,
            "p + 1", [], DatabaseObjectOwner.Adhoc, null, scenario == "cycle" ? ["F:" + signature] : []);
        var environment = SqlFunctionEnvironment.Standard;
        if (scenario == "cycle")
        {
            await database.Catalog.CreateFunctionAsync(function, CancellationToken.None);
        }
        if (scenario == "native-domain")
        {
            await database.Catalog.CreateDomainAsync(new SqlCatalogDomain("native_domain", "INT", null, false, null, null, [], DatabaseObjectOwner.Adhoc, null), CancellationToken.None);
            environment = Environment(SqlType.CreateDomain("native_domain", SqlType.Integer));
        }

        // Act
        var failure = Should.Throw<DatabaseException>(() =>
        {
            if (scenario == "signature") { SqlDefinitionBinder.Bind(function, environment, Collation.Binary, CancellationToken.None); }
            else { SqlDefinitionBinder.Load(database.Catalog, environment, CancellationToken.None); }
        });

        // Assert
        AssertDiagnostic(failure, Location.Create(1, 1, 0, scenario == "native-domain" ? 3 : function.Body.Length));
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Definitions: unknown calls carry their call span in both dependency walks")]
    [InlineData(false)]
    [InlineData(true)]
    public void Dependencies_UnknownFunction_ShouldCarryCallSpan(bool collectCalls)
    {
        // Arrange
        var expression = Expression("absent_function(1)");
        var scope = new SqlExpressionEvaluator([], null);

        // Act
        var failure = Should.Throw<DatabaseException>(() =>
        {
            if (collectCalls) { SqlDefinitionBinder.FunctionCalls(expression, scope); }
            else { SqlDefinitionBinder.Dependencies(expression, scope, SqlFunctionVolatility.Immutable); }
        });

        // Assert
        AssertDiagnostic(failure, expression.Location!);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Definitions: persisted call vector rejections carry the expression span")]
    [InlineData("1", true)]
    [InlineData("ABS(1)", false)]
    [InlineData("ABS(1)", true)]
    public void PinCalls_MismatchedIdentities_ShouldCarryExpressionSpan(string text, bool extra)
    {
        // Arrange
        var expression = Expression(text);
        string[] identities = extra ? ["F:\"OTHER\"(4)"] : [];

        // Act
        var failure = Should.Throw<DatabaseException>(() => SqlDefinitionBinder.PinCalls(expression, new SqlExpressionEvaluator([], null), identities));

        // Assert
        AssertDiagnostic(failure, expression.Location!);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Definitions: aggregate dependency rejections carry their call span")]
    public void Dependencies_AggregateFunction_ShouldCarryCallSpan()
    {
        // Arrange
        var expression = Expression("SUM(1)");

        // Act
        var failure = Should.Throw<DatabaseException>(() => SqlDefinitionBinder.Dependencies(expression, new SqlExpressionEvaluator([], null), SqlFunctionVolatility.Immutable));

        // Assert
        AssertDiagnostic(failure, expression.Location!);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Definitions: unresolved SQL functions and domain casts carry their expression span")]
    [InlineData(false)]
    [InlineData(true)]
    public void Dependencies_UnresolvedDefinition_ShouldCarryExpressionSpan(bool cast)
    {
        // Arrange
        var unresolved = UnresolvedFunction();
        var domain = SqlType.CreateSqlDomain("unresolved_domain", SqlType.Integer, unresolved, null, false);
        var environment = Environment(domain, unresolved);
        var expression = Expression(cast ? "CAST(1 AS unresolved_domain)" : "unresolved_check(1)");
        var scope = new SqlExpressionEvaluator([], null, functions: environment);

        // Act
        var failure = Should.Throw<DatabaseException>(() => SqlDefinitionBinder.Dependencies(expression, scope, SqlFunctionVolatility.Immutable));

        // Assert
        AssertDiagnostic(failure, expression.Location!);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Definitions: unresolved signature domains carry declaration locations")]
    [InlineData(false)]
    [InlineData(true)]
    public void Declare_UnresolvedDomain_ShouldCarryDeclarationSpan(bool result)
    {
        // Arrange
        var environment = Environment(SqlType.CreateSqlDomain("unresolved_domain", SqlType.Integer, UnresolvedFunction(), null, false));
        var create = (SqlCreateFunctionExpression)SqlQueryRequest.FromSql(result
            ? "CREATE FUNCTION f(p INT) RETURNS unresolved_domain RETURN p"
            : "CREATE FUNCTION f(p unresolved_domain) RETURNS INT RETURN p").Statement.SqlExpression;

        // Act
        var failure = Should.Throw<DatabaseException>(() => SqlDefinitionBinder.Declare(create, environment, Collation.Binary, null));

        // Assert
        AssertDiagnostic(failure, create.Location!);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Definitions: deferred unavailable-domain binding first raises a located definition diagnostic")]
    public void Bind_UnresolvedDomain_ShouldCarryDiagnosticBeforeDeferring()
    {
        // Arrange
        var domain = SqlType.CreateSqlDomain("unresolved_domain", SqlType.Integer, UnresolvedFunction(), null, false);
        var environment = Environment(domain);
        var function = new SqlCatalogFunction("f", SqlDefinitionBinder.Signature("f", [domain]), ["p"], ["unresolved_domain"], "INT",
            "IMMUTABLE", false, "p", [], DatabaseObjectOwner.Adhoc, null);
        var location = SqlPersistedExpression.Load(function.Body, "test").Location!;
        DatabaseException? captured = null;
        int thread = System.Environment.CurrentManagedThreadId;
        void Capture(object? sender, FirstChanceExceptionEventArgs args)
        {
            if (System.Environment.CurrentManagedThreadId == thread && args.Exception is DatabaseException failure &&
                failure.Message.Contains("Domain 'unresolved_domain' has an unresolved SQL CHECK", StringComparison.Ordinal))
            {
                captured ??= failure;
            }
        }
        AppDomain.CurrentDomain.FirstChanceException += Capture;
        SqlExpressionFunction bound;

        // Act
        try { bound = SqlDefinitionBinder.Bind(function, environment, Collation.Binary, CancellationToken.None); }
        finally { AppDomain.CurrentDomain.FirstChanceException -= Capture; }

        // Assert
        AssertDiagnostic(captured.ShouldNotBeNull(), location);
        bound.Unresolved.ShouldNotBeNull();
    }

    private static SqlExpression Expression(string text)
        => ((SqlSelectExpression)SqlQueryRequest.FromSql("SELECT " + text + " FROM t").Statement.SqlExpression).Columns.Single().Expression;

    private static SqlExpressionFunction UnresolvedFunction()
        => new("unresolved_check", [SqlType.Integer], SqlType.Boolean, SqlFunctionVolatility.Immutable, false, new SqlBoundConstant(true), "missing native dependency");

    private static SqlFunctionEnvironment Environment(SqlType domain, SqlFunction? function = null)
    {
        var functions = new SqlFunctionCollection(null);
        if (function is not null) { functions.Add(function); }
        return new SqlFunctionEnvironment(functions.Freeze(), default, new SqlTypeCatalog(SqlTypeCatalog.Standard.Append(domain), SqlTypeCatalog.Standard.Casts));
    }

    private static void AssertDiagnostic(DatabaseException failure, Location location)
    {
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("COHSQLD001");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.Location.ShouldBe(DiagnosticLocation.Absolute);
        diagnostic.Start.ShouldBe(location.Start);
        diagnostic.End.ShouldBe(location.End);
        diagnostic.Line.ShouldBe(location.StartLine);
        diagnostic.Message.ShouldBe(failure.Message);
        failure.Message.ShouldNotContain("COHSQLD001:", Case.Sensitive);
    }
}
