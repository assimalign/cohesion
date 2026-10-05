using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Execution.Tests;

/// <summary>
/// Tests for the request contract every model request derives from: the typed statement view of
/// <see cref="QueryRequest{TStatement}"/> and the parameter default of <see cref="QueryRequest"/>.
/// </summary>
public class QueryRequestTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Execution] - QueryRequest: the typed statement is the statement the request carries")]
    public void Statement_TypedRequest_ShouldReturnTheSameStatementThroughBothViews()
    {
        // Arrange
        var statement = new TestStatement();

        // Act
        var request = new TestRequest(statement);
        QueryRequest untyped = request;

        // Assert
        request.Statement.ShouldBeSameAs(statement);
        untyped.Statement.ShouldBeSameAs(statement);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Execution] - QueryRequest: a request that carries no parameters reports null")]
    public void Parameters_RequestWithoutParameters_ShouldBeNull()
    {
        // Arrange
        QueryRequest request = new TestRequest(new TestStatement());

        // Act
        var parameters = request.Parameters;

        // Assert
        parameters.ShouldBeNull();
    }

    private sealed class TestStatement : QueryStatement
    {
        public TestStatement() => Expression = new TestExpression();

        public override QueryExpression Expression { get; }

        private sealed class TestExpression : QueryExpression
        {
        }
    }

    private sealed class TestRequest : QueryRequest<TestStatement>
    {
        public TestRequest(TestStatement statement)
            : base(statement)
        {
        }
    }
}
