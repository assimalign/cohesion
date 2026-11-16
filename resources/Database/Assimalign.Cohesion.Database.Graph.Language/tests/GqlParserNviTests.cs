using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public sealed class GqlParserNviTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Analyzers observe complete statement text before analysis (#1264)")]
    [InlineData("/* retained trivia */ RETURN 42 AS n")]
    [InlineData("CALL { RETURN 42 AS n } RETURN n")]
    [InlineData("?")]
    public void Parse_StatementText_ShouldBeAvailableDuringAnalysis(string source)
    {
        // Arrange
        var observer = new SourceObserver();
        var options = new QueryParserOptions();
        options.Analyzers.Add(observer);
        var parser = new GqlQueryParser(options);

        // Act
        var statement = parser.Parse(source);

        // Assert
        observer.TextDuringAnalysis.ShouldBe(source);
        observer.Expression.ShouldBeSameAs(statement.Expression);
    }

    private sealed class SourceObserver : QueryAnalyzer
    {
        internal QueryExpression? Expression { get; private set; }
        internal string? TextDuringAnalysis { get; private set; }

        protected override Task AnalyzeCoreAsync(QueryAnalyzerContext context, CancellationToken cancellationToken)
        {
            Expression = context.Statement.Expression;
            TextDuringAnalysis = Expression.Text;
            return Task.CompletedTask;
        }
    }
}
