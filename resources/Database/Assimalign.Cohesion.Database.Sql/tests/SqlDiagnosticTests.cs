using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlDiagnosticTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Diagnostics: text and typed requests preserve every parser diagnostic")]
    public async Task Execute_ErrorStatement_ShouldCarryCompleteDiagnosticsAsync()
    {
        // Arrange
        const string sql = "SELECT CURRENT_DATE(), LOCALTIME() FROM t;";
        var parsed = (SqlQueryStatement)new SqlQueryParser().Parse(sql);
        parsed.Diagnostics.Count().ShouldBe(2);
        await using var engine = SqlDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("diagnostics", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var factory = Should.Throw<DatabaseParseException>(() => SqlQueryRequest.FromSql(sql));
        var text = await Should.ThrowAsync<DatabaseParseException>(async () => await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));
        var typed = await Should.ThrowAsync<DatabaseParseException>(async () => await session.ExecuteAsync(new SqlQueryRequest(parsed), CancellationToken.None));

        // Assert
        foreach (var failure in new[] { factory, text, typed })
        {
            failure.Message.ShouldBe($"SQL parse error {parsed.Diagnostics.First().Code}: {parsed.Diagnostics.First().Message}");
            failure.Diagnostics.Select(Value).ShouldBe(parsed.Diagnostics.Select(Value));
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Diagnostics: an unknown column has a code and its exact AST span")]
    public async Task Execute_UnknownColumn_ShouldCarryLocatedDiagnosticAsync()
    {
        // Arrange
        const string sql = "SELECT absent FROM t;";
        await using var engine = SqlDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("diagnostics", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT)", cancellationToken: CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));

        // Assert
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CS01015");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.Start.ShouldBe(7);
        diagnostic.End.ShouldBe(13);
        failure.Message.ShouldBe("Unknown column 'absent'.");
        failure.Message.ShouldNotContain("COHSQLE", Case.Sensitive);
        failure.Message.ShouldNotContain("CK00001:", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Functions: unsupported and unknown calls have distinct single diagnostics")]
    public async Task Execute_UnknownFunction_ShouldRejectOnlyAtPlanningAsync()
    {
        // Arrange
        var unsupported = new SqlQueryParser().Parse("SELECT FIRST_VALUE() FROM t;");
        unsupported.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK00001");
        new SqlQueryParser().Parse("SELECT FOO(a) FROM t;").Diagnostics.ShouldBeEmpty();
        await using var engine = SqlDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("diagnostics", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (a INT, b INT)", cancellationToken: CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT FOO(a) FROM t;", cancellationToken: CancellationToken.None));

        // Assert
        failure.ShouldNotBeOfType<DatabaseParseException>();
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS01016");
        failure.Message.ShouldBe("Unknown function 'FOO'.");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Schema: recognized function identifiers quote, parse and reapply as a no-op")]
    public async Task ApplySchema_FunctionNamedColumns_ShouldRemainNoOpAsync()
    {
        // Arrange
        var columns = new[] { "rank", "current_date", "localtime", "localtimestamp", "position", "sign", "reverse", "ln", "exp", "concat_ws", "date_trunc", "gen_random_uuid" }
            .Select(name => new CompiledSchemaColumn(name, DatabaseType.Int32, true)).ToArray();
        var table = new CompiledSchemaTable("function_names", "Tests.FunctionNames", columns, null, [], []);
        var schema = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "diagnostics", false, [], [table], []);
        string rendered = SqlSchemaStatementRenderer.CreateTable(table);
        foreach (var column in columns) { rendered.ShouldContain($"\"{column.Name}\"", Case.Sensitive); }
        SqlQueryRequest.FromSql(rendered).Statement.Diagnostics.ShouldBeEmpty();
        await using var engine = SqlDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("diagnostics", CancellationToken.None);

        // Act
        await database.ApplySchemaAsync(schema, cancellationToken: CancellationToken.None);
        var second = await database.ApplySchemaAsync(schema, cancellationToken: CancellationToken.None);

        // Assert
        second.WasAlreadyApplied.ShouldBeTrue();
        second.OperationCount.ShouldBe(0);
    }

    private static object Value(Diagnostic diagnostic) =>
        (diagnostic.Code, diagnostic.Severity, diagnostic.Message, diagnostic.Start, diagnostic.End, diagnostic.Line, diagnostic.Location);
}
