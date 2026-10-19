using System.Linq;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public sealed class GqlAggregateParserTests
{
    [Theory(DisplayName = "Cohesion Test [GQL] - Aggregates: calls have dedicated aggregate nodes")]
    [InlineData("count(*)", GqlAggregateFunction.Count, false, true)]
    [InlineData("count(a)", GqlAggregateFunction.Count, false, false)]
    [InlineData("count(DISTINCT a.name)", GqlAggregateFunction.Count, true, false)]
    [InlineData("sum(ALL a.age)", GqlAggregateFunction.Sum, false, false)]
    [InlineData("avg(a.age)", GqlAggregateFunction.Avg, false, false)]
    [InlineData("min(a.age)", GqlAggregateFunction.Min, false, false)]
    [InlineData("max(a.age)", GqlAggregateFunction.Max, false, false)]
    [InlineData("collect_list(a.name)", GqlAggregateFunction.CollectList, false, false)]
    [InlineData("CoLlEcT(DISTINCT a.name)", GqlAggregateFunction.CollectList, true, false)]
    public void Parse_Aggregate_ShouldPreserveFunctionOperandAndQuantifier(string source, GqlAggregateFunction function, bool distinct, bool star)
    {
        // Arrange / Act
        var statement = (GqlQueryStatement)new GqlQueryParser().Parse("MATCH (a) RETURN " + source);
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var aggregate = statement.GqlExpression.Projections.ShouldHaveSingleItem().Expression.ShouldBeOfType<GqlAggregateExpression>();
        aggregate.Function.ShouldBe(function);
        aggregate.Distinct.ShouldBe(distinct);
        (aggregate.Operand is null).ShouldBe(star);
        (aggregate.Operand is GqlVariableExpression or GqlPropertyExpression || star).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [GQL] - Aggregates: ISO GROUP BY is separate from RETURN and ORDER BY")]
    public void Parse_GroupBy_ShouldPreserveSeparateKeyList()
    {
        // Arrange / Act
        var statement = (GqlQueryStatement)new GqlQueryParser().Parse(
            "MATCH (a) RETURN a.active, count(*) AS n GROUP BY a.active ORDER BY n DESC LIMIT 1");
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        statement.GqlExpression.GroupBy.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBeOfType<GqlPropertyExpression>().Property.ShouldBe("active");
        statement.GqlExpression.Projections.Last().Expression.ShouldBeOfType<GqlAggregateExpression>();
        statement.GqlExpression.OrderBy.ShouldHaveSingleItem().Expression.ShouldBeOfType<GqlVariableExpression>().Variable.ShouldBe("n");
    }

    [Fact(DisplayName = "Cohesion Test [GQL] - Aggregates: ISO empty grouping set is an explicit empty key list")]
    public void Parse_EmptyGroupingSet_ShouldPreserveEmptyKeyList()
    {
        // Arrange / Act
        var statement = (GqlQueryStatement)new GqlQueryParser().Parse("MATCH (a) RETURN count(*) GROUP BY () ORDER BY count(*)");
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        statement.GqlExpression.GroupBy.ShouldNotBeNull().ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [GQL] - Aggregates: function-named comprehension variables retain the capability diagnostic")]
    [InlineData("count")]
    [InlineData("sum")]
    [InlineData("avg")]
    [InlineData("min")]
    [InlineData("max")]
    [InlineData("collect_list")]
    [InlineData("collect")]
    public void Parse_FunctionNamedComprehension_ShouldReportCapabilityDiagnostic(string name)
    {
        // Arrange / Act
        var statement = new GqlQueryParser().Parse($"RETURN [{name} IN [1,2] | {name}]");
        // Assert
        var error = statement.Diagnostics.ShouldHaveSingleItem();
        error.Code.ShouldBe("COHDBL001");
        error.Message.ShouldNotBeNull().ShouldContain("LIST COMPREHENSION", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [GQL] - Aggregates: function names remain legal variables and property owners")]
    [InlineData("MATCH (count:Person) WHERE count.age = 42 RETURN count.name")]
    [InlineData("MATCH count = (sum)-[avg:max]->(collect) WHERE sum:Person RETURN count, avg, collect")]
    [InlineData("MATCH (collect_list IS count) RETURN collect_list.count AS sum")]
    [InlineData("MATCH (count) RETURN {n:count(count), total:sum(count.age)}")]
    public void Parse_FunctionSpelledName_ShouldRemainAnIdentifier(string source)
    {
        // Arrange / Act / Assert
        new GqlQueryParser().Parse(source).Diagnostics.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [GQL] - Aggregates: malformed argument lists and DISTINCT star are syntax errors")]
    [InlineData("count(DISTINCT *)")]
    [InlineData("count(ALL *)")]
    [InlineData("sum(*)")]
    [InlineData("count()")]
    [InlineData("sum(1,2)")]
    [InlineData("count(1")]
    [InlineData("count(DISTINCT)")]
    public void Parse_InvalidAggregate_ShouldReportSyntaxDiagnostic(string expression)
    {
        // Arrange / Act / Assert
        new GqlQueryParser().Parse("RETURN " + expression).Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "GQL0002");
    }

    [Fact(DisplayName = "Cohesion Test [GQL] - Aggregates: WITH count composes with RETURN")]
    public void Parse_WithAggregate_ShouldAcceptComposition()
    {
        // Arrange / Act
        var errors = new GqlQueryParser().Parse("MATCH (a:Person) WITH count(a) AS c RETURN c").Diagnostics;
        // Assert
        errors.ShouldBeEmpty();
    }
}
