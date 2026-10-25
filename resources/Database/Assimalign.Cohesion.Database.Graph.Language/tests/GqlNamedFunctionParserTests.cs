using System;
using System.Linq;
using Assimalign.Cohesion.Database.Types;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public sealed class GqlNamedFunctionParserTests
{
    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Functions: Parse Deferred Calls Should Preserve Arguments Distinct And Spans")]
    public void Parse_DeferredCalls_ShouldPreserveArgumentsDistinctAndSpans()
    {
        const string source = "RETURN developer_sum(DISTINCT developer_echo(42)) AS answer";
        var statement = (GqlQueryStatement)new GqlQueryParser(allowCustomFunctions: true).Parse(source);
        statement.Diagnostics.ShouldBeEmpty();
        var call = statement.GqlExpression.Projections.ShouldHaveSingleItem().Expression.ShouldBeOfType<GqlNamedFunctionCallExpression>();
        call.Name.ToString().ShouldBe("developer_sum");
        call.Distinct.ShouldBeTrue();
        call.Arguments.ShouldHaveSingleItem().ShouldBeOfType<GqlNamedFunctionCallExpression>().Name.ToString().ShouldBe("developer_echo");
        call.Location!.Start.ShouldBe(source.IndexOf("developer_sum", StringComparison.Ordinal));
        call.Location.End.ShouldBe(source.IndexOf(" AS answer", StringComparison.Ordinal));
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Functions: Window Invocations Should Remain Unsupported")]
    [InlineData("RETURN developer_echo(OVER ())", false)]
    [InlineData("RETURN developer_echo(OVER ())", true)]
    [InlineData("RETURN count(*) OVER ()", false)]
    [InlineData("RETURN count(*) OVER ()", true)]
    [InlineData("RETURN developer_echo(1) over ()", true)]
    public void Parse_WindowInvocations_ShouldRemainUnsupported(string source, bool allowCustomFunctions)
    {
        var statement = new GqlQueryParser(allowCustomFunctions).Parse(source);
        statement.Diagnostics.ShouldContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error &&
            diagnostic.Code == "CK00001" && diagnostic.Message != null && diagnostic.Message.Contains("WINDOW OVER", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Functions: OVER Should Remain An Identifier Outside Invocations")]
    public void Parse_OverBindings_ShouldRemainIdentifiers()
    {
        new GqlQueryParser(allowCustomFunctions: true).Parse("LET over=1 RETURN developer_echo(over) AS over")
            .Diagnostics.ShouldBeEmpty();
        new GqlQueryParser(allowCustomFunctions: true).Parse("MATCH (over:over) RETURN developer_echo(over.over) AS over")
            .Diagnostics.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Functions: Parse Malformed Custom Calls Should Retain Grammar Refusals")]
    [InlineData("RETURN developer_echo(*)")]
    [InlineData("RETURN developer_echo(1,)")]
    [InlineData("RETURN developer_echo(OVER ())")]
    public void Parse_MalformedCustomCalls_ShouldRetainGrammarRefusals(string source)
    {
        var statement = new GqlQueryParser(allowCustomFunctions: true).Parse(source);
        statement.Diagnostics.ShouldContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }
}
