using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlParserNviTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Language] - Base parse stamps SQL source after analysis")]
    public void Parse_SuccessfulAnalyzer_ShouldStampSourceAfterAnalysis()
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
        observer.TextDuringAnalysis.ShouldBeNull();
        observer.Expression.ShouldBeSameAs(statement.SqlExpression);
        statement.SqlExpression.ShouldNotBeNull().Text.ShouldBe(source);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql.Language] - Failed analysis leaves SQL source unstamped")]
    public void Parse_FaultedAnalyzer_ShouldRetainUnstampedSource()
    {
        // Arrange
        var failure = new InvalidOperationException();
        var observer = new SourceObserver(failure);
        var options = new QueryParserOptions();
        options.Analyzers.Add(observer);
        QueryParser parser = new SqlQueryParser(options);

        // Act / Assert
        Should.Throw<InvalidOperationException>(() => parser.Parse("SELECT 42;")).ShouldBeSameAs(failure);
        observer.Expression.ShouldNotBeNull().Text.ShouldBeNull();
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
