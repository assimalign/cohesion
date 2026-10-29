using System;
using System.Linq;
using System.Threading;
using Shouldly;
using Xunit;
using Assimalign.Cohesion.Database.Graph.Language.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public sealed class GqlReduceParserTests
{
    [Theory(DisplayName = "Cohesion Test [GQL] - List predicates: binding forms retain operands and full spans")]
    [InlineData("all", GqlListPredicateKind.All)]
    [InlineData("aNy", GqlListPredicateKind.Any)]
    [InlineData("none", GqlListPredicateKind.None)]
    [InlineData("single", GqlListPredicateKind.Single)]
    public void Parse_ListPredicate_ShouldPreserveBindingAndSpan(string name, GqlListPredicateKind kind)
    {
        // Arrange
        string value = name + """("x y" IN $xs WHERE "x y" > 0)""";
        // Act
        var statement = (GqlQueryStatement)new GqlQueryParser().Parse("RETURN " + value);
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var predicate = statement.GqlExpression.Projections[0].Expression.ShouldBeOfType<GqlListPredicateExpression>();
        predicate.Kind.ShouldBe(kind);
        predicate.Variable.ShouldBe("x y");
        predicate.List.ShouldBeOfType<GqlParameterExpression>().Name.ShouldBe("xs");
        predicate.Predicate.ShouldBeOfType<GqlBinaryExpression>().Operator.ShouldBe(">");
        predicate.Location!.Start.ShouldBe(7);
        predicate.Location.End.ShouldBe(7 + value.Length);
        new GqlQueryParser().Parse("MATCH (a) WHERE " + value + " RETURN a").Diagnostics.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [GQL] - Reduce: quoted accumulator and iterator retain outer and body expressions")]
    public void Parse_Reduce_ShouldPreserveBindingAndSpan()
    {
        // Arrange
        const string value = """reduce("a b" = $seed, sum IN $xs | "a b" + sum)""";
        // Act
        var statement = (GqlQueryStatement)new GqlQueryParser().Parse("RETURN " + value);
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var reduce = statement.GqlExpression.Projections[0].Expression.ShouldBeOfType<GqlReduceExpression>();
        reduce.Accumulator.ShouldBe("a b"); reduce.Variable.ShouldBe("sum");
        reduce.Initial.ShouldBeOfType<GqlParameterExpression>().Name.ShouldBe("seed");
        reduce.List.ShouldBeOfType<GqlParameterExpression>().Name.ShouldBe("xs");
        reduce.Body.ShouldBeOfType<GqlBinaryExpression>().Operator.ShouldBe("+");
        reduce.Location!.Start.ShouldBe(7); reduce.Location.End.ShouldBe(7 + value.Length);
    }

    [Theory(DisplayName = "Cohesion Test [GQL] - ALL: list predicates remain distinct from set quantifiers")]
    [InlineData("RETURN all(x IN [] WHERE TRUE)")]
    [InlineData("RETURN ALL all(x IN [] WHERE TRUE)")]
    [InlineData("RETURN ALL (1+2)")]
    [InlineData("LET x=1 RETURN ALL (x IN [1])")]
    [InlineData("LET x=1,m={WHERE:TRUE} RETURN ALL (x IN [1] AND m.WHERE)")]
    [InlineData("MATCH (n) LET x=1 RETURN ALL (x IN [1] AND n:WHERE)")]
    [InlineData("MATCH (n) LET x=1 RETURN ALL (x IN [1] AND n:A|WHERE)")]
    [InlineData("MATCH (n) LET x=1 RETURN ALL (x IN [1] AND n IS LABELED WHERE)")]
    [InlineData("RETURN count(ALL (1+2))")]
    [InlineData("FOR x IN [1] RETURN count(ALL (x IN [1]))")]
    [InlineData("RETURN application_call(ALL (1+2))")]
    [InlineData("RETURN count(all(x IN [] WHERE TRUE))")]
    [InlineData("RETURN collect(ALL all(x IN [] WHERE TRUE))")]
    [InlineData("RETURN toString(all(x IN [] WHERE TRUE))")]
    [InlineData("WITH all(x IN [] WHERE TRUE) AS p RETURN p")]
    [InlineData("RETURN application_call(all(x IN [] WHERE TRUE))")]
    [InlineData("RETURN [x IN [1] WHERE all(y IN [x] WHERE y:A|B) | x]")]
    public void Parse_AllInValuePositions_ShouldPreservePredicate(string source)
    {
        // Arrange / Act
        var statement = new GqlQueryParser(allowCustomFunctions: true).Parse(source);
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [GQL] - Reduce and list predicates: malformed binding syntax is CG00002")]
    [InlineData("reduce(1)")]
    [InlineData("reduce(a=0,x IN [1])")]
    [InlineData("reduce(a=0,x IN [1] | )")]
    [InlineData("reduce(a=0,x [1] | a)")]
    [InlineData("reduce(a=0,x IN [1] | a")]
    [InlineData("coalesce(all(x IN [1]),TRUE)")]
    [InlineData("any([1])")]
    [InlineData("none(x IN [1] WHERE )")]
    [InlineData("single(x IN [1] WHERE TRUE, FALSE)")]
    public void Parse_InvalidForm_ShouldReportSyntax(string expression)
    {
        // Arrange / Act
        var statement = new GqlQueryParser().Parse("RETURN " + expression);
        // Assert
        statement.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG00002");
    }

    [Fact(DisplayName = "Cohesion Test [GQL] - Reduce renderer: nested bindings round-trip structurally")]
    public void Render_NestedBindings_ShouldPreserveSemanticTree()
    {
        // Arrange
        const string source = "MATCH (a WHERE all(x IN [1,2] WHERE any(y IN [x] WHERE y > 0)) AND reduce(s=0,x IN [1,2] | s+reduce(s=x,y IN [x] | s+y)) > 0)->(b) RETURN b";
        var first = (GqlQueryStatement)new GqlQueryParser().Parse(source);
        first.Diagnostics.ShouldBeEmpty();
        // Act
        string rendered = first.GqlExpression.MatchClauses[0].Paths[0].ToString();
        var second = (GqlQueryStatement)new GqlQueryParser().Parse("MATCH " + rendered + " RETURN b");
        // Assert
        second.Diagnostics.ShouldBeEmpty();
        GqlSemanticTree.Path(second.GqlExpression.MatchClauses[0].Paths[0]).ShouldBe(GqlSemanticTree.Path(first.GqlExpression.MatchClauses[0].Paths[0]));
    }

    [Theory(DisplayName = "Cohesion Test [GQL] - Reduce nesting: explicit small-stack parsing reports complexity")]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_DeepCollection_ShouldGuardStack(bool reduce)
    {
        // Arrange
        string expression = string.Concat(Enumerable.Repeat(reduce ? "reduce(a=0,x IN [1] | " : "all(x IN [1] WHERE ", 5000)) + "TRUE" + new string(')', 5000);
        string? code = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { code = new GqlQueryParser().Parse("RETURN " + expression).Diagnostics.ShouldHaveSingleItem().Code; }
            catch (Exception error) { failure = error; }
        }, 256 * 1024);
        // Act
        thread.Start(); thread.Join(TimeSpan.FromSeconds(30)).ShouldBeTrue();
        // Assert
        failure.ShouldBeNull(); code.ShouldBe("CG00009");
    }
}
