using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlParserNviTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - Every SQL command and recovery path retains its complete source before analysis")]
    [InlineData("", SqlQueryCommandType.Unknown)]
    [InlineData("  /* only trivia */ ", SqlQueryCommandType.Unknown)]
    [InlineData("unknown;", SqlQueryCommandType.Unknown)]
    [InlineData("?", SqlQueryCommandType.Unknown)]
    [InlineData("SELECT ?;", SqlQueryCommandType.Select)]
    [InlineData("SELECT 1;", SqlQueryCommandType.Select)]
    [InlineData("(SELECT 1);", SqlQueryCommandType.Select)]
    [InlineData("WITH x AS (SELECT 1 AS n) SELECT n FROM x;", SqlQueryCommandType.Select)]
    [InlineData("SELECT 1 WINDOW w AS ();", SqlQueryCommandType.Select)]
    [InlineData("EXPLAIN SELECT 1;", SqlQueryCommandType.Explain)]
    [InlineData("INSERT INTO t (n) VALUES (1);", SqlQueryCommandType.Insert)]
    [InlineData("UPDATE t SET n = 1;", SqlQueryCommandType.Update)]
    [InlineData("DELETE FROM t;", SqlQueryCommandType.Delete)]
    [InlineData("CREATE TABLE t (n INT);", SqlQueryCommandType.Create)]
    [InlineData("ALTER TABLE t ADD COLUMN n INT;", SqlQueryCommandType.Alter)]
    [InlineData("DROP TABLE t;", SqlQueryCommandType.Drop)]
    [InlineData("BEGIN;", SqlQueryCommandType.Begin)]
    [InlineData("START TRANSACTION;", SqlQueryCommandType.Begin)]
    [InlineData("COMMIT;", SqlQueryCommandType.Commit)]
    [InlineData("END;", SqlQueryCommandType.Commit)]
    [InlineData("ROLLBACK;", SqlQueryCommandType.Rollback)]
    [InlineData("TRUNCATE t;", SqlQueryCommandType.Truncate)]
    [InlineData("COPY t TO STDOUT;", SqlQueryCommandType.CopyTo)]
    [InlineData("MERGE INTO t USING s ON t.n = s.n WHEN MATCHED THEN DELETE;", SqlQueryCommandType.Merge)]
    [InlineData("DISCARD ALL;", SqlQueryCommandType.Discard)]
    [InlineData("SET TRANSACTION READ ONLY;", SqlQueryCommandType.SetTransaction)]
    [InlineData("SAVEPOINT s;", SqlQueryCommandType.Savepoint)]
    [InlineData("RELEASE SAVEPOINT s;", SqlQueryCommandType.ReleaseSavepoint)]
    [InlineData("ROLLBACK TO SAVEPOINT s;", SqlQueryCommandType.RollbackToSavepoint)]
    public void Parse_CommandOrRecovery_ShouldStampSourceBeforeAnalysis(string source, SqlQueryCommandType command)
    {
        // Arrange
        var observer = new SourceObserver();
        var options = new QueryParserOptions();
        options.Analyzers.Add(observer);

        // Act
        var statement = (SqlQueryStatement)new SqlQueryParser(options).Parse(source);

        // Assert
        statement.SqlExpression.ShouldNotBeNull().CommandType.ShouldBe(command);
        observer.TextDuringAnalysis.ShouldBe(source);
        statement.SqlExpression.Text.ShouldBe(source);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql.Language] - Nesting recovery retains SQL source before analysis")]
    public void Parse_NestingLimitRecovery_ShouldStampSourceBeforeAnalysis()
    {
        // Arrange
        string source = "SELECT " + new string('(', 100) + "1" + new string(')', 100) + ";";
        var observer = new SourceObserver();
        var options = new SqlQueryParserOptions { ExpressionNestingLimit = SqlQueryParserOptions.MinimumExpressionNestingLimit };
        options.Analyzers.Add(observer);

        // Act
        var statement = (SqlQueryStatement)new SqlQueryParser(options).Parse(source);

        // Assert
        statement.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS00006");
        observer.TextDuringAnalysis.ShouldBe(source);
        statement.SqlExpression.ShouldNotBeNull().Text.ShouldBe(source);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql.Language] - SQL grammar stamps source before analysis")]
    public void Parse_SuccessfulAnalyzer_ShouldStampSourceBeforeAnalysis()
    {
        // Arrange
        const string source = "/* retained trivia */ SELECT 42;";
        var observer = new SourceObserver();
        var options = new SqlQueryParserOptions();
        options.Analyzers.Add(observer);
        QueryParser parser = new SqlQueryParser(options);

        // Act
        var statement = parser.Parse(source).ShouldBeOfType<SqlQueryStatement>();

        // Assert
        observer.TextDuringAnalysis.ShouldBe(source);
        observer.Expression.ShouldBeSameAs(statement.SqlExpression);
        statement.SqlExpression.ShouldNotBeNull().Text.ShouldBe(source);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql.Language] - Failed analysis retains SQL source stamped by grammar")]
    public void Parse_FaultedAnalyzer_ShouldRetainStampedSource()
    {
        // Arrange
        var failure = new InvalidOperationException();
        var observer = new SourceObserver(failure);
        var options = new QueryParserOptions();
        options.Analyzers.Add(observer);
        QueryParser parser = new SqlQueryParser(options);

        // Act / Assert
        Should.Throw<InvalidOperationException>(() => parser.Parse("SELECT 42;")).ShouldBeSameAs(failure);
        observer.Expression.ShouldNotBeNull().Text.ShouldBe("SELECT 42;");
    }

    private sealed class SourceObserver : QueryAnalyzer
    {
        private readonly Exception? _failure;

        internal SourceObserver(Exception? failure = null) { _failure = failure; }

        internal SqlQueryExpression? Expression { get; private set; }
        internal string? TextDuringAnalysis { get; private set; }

        protected override Task AnalyzeCoreAsync(QueryAnalyzerContext context, CancellationToken cancellationToken)
        {
            Expression = context.Statement.ShouldBeOfType<SqlQueryStatement>().SqlExpression;
            TextDuringAnalysis = Expression?.Text;
            return _failure is null ? Task.CompletedTask : Task.FromException(_failure);
        }
    }
}
