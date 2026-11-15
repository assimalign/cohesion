using System;
using System.Linq;
using System.Threading;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Sql.Schema.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlMigrationLoweringTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Migration DEFAULT reader: shared admission preserves engine values and diagnostics")]
    [InlineData(null)]
    [InlineData("NULL")]
    [InlineData("(+1)")]
    [InlineData("-1.25")]
    [InlineData("'it''s ready'")]
    [InlineData("true")]
    [InlineData("1 + 2")]
    [InlineData("CURRENT_TIMESTAMP")]
    [InlineData("'unterminated")]
    [InlineData("1); DROP TABLE Orders; --")]
    [InlineData("1 ORDER BY Id")]
    [InlineData("1; --")]
    [InlineData("-(1 + 2)")]
    [InlineData("-'text'")]
    [InlineData("nextval('s')")]
    [InlineData("1 + nextval('s')")]
    [InlineData("CAST('s' AS REGCLASS)")]
    [InlineData("NEXT VALUE FOR s")]
    [InlineData("(SELECT nextval('s') FROM Orders)")]
    public void CanonicalDefault_SharedReader_ShouldMatchRetainedEngineReader(string? literal)
        => CompareReaders(literal);

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Migration DEFAULT reader: grouped text retains declaration and guard limits")]
    [InlineData(-1, "1")]
    [InlineData(0, "1")]
    [InlineData(1, "1")]
    [InlineData(-1, "nextval('s')")]
    [InlineData(0, "nextval('s')")]
    [InlineData(1, "nextval('s')")]
    public void CanonicalDefault_NestingBoundary_ShouldMatchRetainedEngineReader(int offset, string expression)
    {
        // Arrange: a fixed stack makes the parser ceiling independent of the runner's stack.
        int depth = SqlQueryParserOptions.DefaultExpressionNestingLimit + offset;
        string literal = new string('(', depth) + expression + new string(')', depth);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { CompareReaders(literal); }
            catch (Exception exception) { failure = exception; }
        }, 16 * 1024 * 1024) { IsBackground = true };

        // Act
        thread.Start();

        // Assert: only a hang guard, not a wall-clock performance requirement.
        thread.Join(TimeSpan.FromSeconds(30)).ShouldBeTrue();
        failure.ShouldBeNull();
    }

    private static void CompareReaders(string? literal)
    {
        // Arrange: the retained provisioner reader is the unchanged base implementation.
        string? expected = null;
        Exception? refusal = null;
        try { expected = SqlSchemaProvisioner.CanonicalDefault(literal); }
        catch (DatabaseException exception) { refusal = exception; }

        // Act / Assert
        if (refusal is null)
        {
            SqlMigrationDefaultLiteral.CanonicalDefault(literal).ShouldBe(expected);
        }
        else
        {
            var actual = Should.Throw<DatabaseException>(() => SqlMigrationDefaultLiteral.CanonicalDefault(literal));
            actual.GetType().ShouldBe(refusal.GetType());
            actual.Message.ShouldBe(refusal.Message);
            actual.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Message, diagnostic.Severity,
                diagnostic.Location, diagnostic.Start, diagnostic.End, diagnostic.Line)).ShouldBe(
                ((DatabaseException)refusal).Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Message, diagnostic.Severity,
                    diagnostic.Location, diagnostic.Start, diagnostic.End, diagnostic.Line)));
        }
    }
}
