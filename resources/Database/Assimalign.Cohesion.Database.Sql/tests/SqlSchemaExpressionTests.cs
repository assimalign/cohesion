using System;
using System.Linq.Expressions;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public class SqlSchemaExpressionTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Schema: Sum retains analyzable selector and predicate expressions")]
    public void Sum_WithSelectorAndPredicate_ShouldRetainExpressionShape()
    {
        Expression<Func<OrderLine, object?>> selector = line => line.Quantity * line.UnitPrice;
        Expression<Func<OrderLine, bool>> predicate = line => line.OrderId == 42;

        ISqlAggregateExpression expression = Sql.Sum(selector, predicate);

        expression.SourceType.ShouldBe(typeof(OrderLine));
        expression.Selector.ShouldBeSameAs(selector);
        expression.Predicate.ShouldBeSameAs(predicate);
        expression.Selector.Parameters.ShouldHaveSingleItem().Type.ShouldBe(typeof(OrderLine));
        expression.Predicate.Parameters.ShouldHaveSingleItem().Type.ShouldBe(typeof(OrderLine));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Schema: Sum rejects null expressions")]
    public void Sum_WithNullExpression_ShouldRejectDeclaration()
    {
        Expression<Func<OrderLine, object?>> selector = line => line.Quantity;
        Expression<Func<OrderLine, bool>> predicate = line => line.OrderId == 42;

        Should.Throw<ArgumentNullException>(() => Sql.Sum<OrderLine>(null!, predicate));
        Should.Throw<ArgumentNullException>(() => Sql.Sum<OrderLine>(selector, null!));
    }

    private sealed record OrderLine(long Id, long OrderId, int Quantity, decimal UnitPrice);
}
