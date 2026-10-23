using System;
using System.Collections.Generic;
using System.Linq;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public sealed class GqlFunctionParserTests
{
    public static IEnumerable<object[]> Calls()
    {
        yield return ["element_id(a)"];
        yield return ["elementId(a)"];
        yield return ["labels(a)"];
        yield return ["type(a)"];
        yield return ["properties(a)"];
        yield return ["keys(a)"];
        yield return ["startNode(a)"];
        yield return ["endNode(a)"];
        yield return ["path_length(a)"];
        yield return ["length(a)"];
        yield return ["nodes(a)"];
        yield return ["edges(a)"];
        yield return ["relationships(a)"];
        yield return ["lower(a)"];
        yield return ["toLower(a)"];
        yield return ["upper(a)"];
        yield return ["toUpper(a)"];
        yield return ["trim(a)"];
        yield return ["substring(a, a)"];
        yield return ["replace(a, a, a)"];
        yield return ["char_length(a)"];
        yield return ["character_length(a)"];
        yield return ["size(a)"];
        yield return ["abs(a)"];
        yield return ["ceil(a)"];
        yield return ["ceiling(a)"];
        yield return ["floor(a)"];
        yield return ["round(a)"];
        yield return ["sqrt(a)"];
        yield return ["ln(a)"];
        yield return ["log(a)"];
        yield return ["log(a,a)"];
        yield return ["mod(a, a)"];
        yield return ["toString(a)"];
        yield return ["toInteger(a)"];
        yield return ["toFloat(a)"];
        yield return ["toBoolean(a)"];
        yield return ["current_date"];
        yield return ["current_timestamp"];
        yield return ["CAST(a AS INTEGER)"];
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Functions: every scalar spelling parses in WHERE and RETURN without aggregate dispatch")]
    [MemberData(nameof(Calls))]
    public void Parse_ScalarCalls_ShouldResolveBothValuePositions(string call)
    {
        // Arrange
        string value = call.ToUpperInvariant();
        // Act
        var statement = (GqlQueryStatement)new GqlQueryParser().Parse($"MATCH (a) WHERE {value} IS NOT NULL RETURN {value}");
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var expression = statement.GqlExpression.Projections.ShouldHaveSingleItem().Expression;
        (expression is GqlFunctionCallExpression or GqlCastExpression).ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Functions: nested calls and function words as graph names remain legal")]
    [InlineData("RETURN upper(trim(toString(abs(-2))))")]
    [InlineData("MATCH (size:Person {count:1}) WHERE size.count=1 RETURN size.count")]
    [InlineData("MATCH p=(a)-[r]->(b) RETURN path_length(p), nodes(p), relationships(p)")]
    [InlineData("MATCH date=(date:date)-[duration:duration]->(time) RETURN date, duration, time")]
    [InlineData("MATCH (abs)-[size]->(trim) RETURN abs, size, trim")]
    [InlineData("MATCH (stddev_samp)-[percentile_cont]->(date) RETURN stddev_samp, percentile_cont, date")]
    [InlineData("MATCH (cast:Movie) RETURN cast.title")]
    [InlineData("MATCH cast=()-[cast]->() RETURN cast")]
    [InlineData("LET cast=1 RETURN cast AS cast")]
    [InlineData("FOR cast IN [1] RETURN cast")]
    [InlineData("UNWIND [1] AS cast RETURN cast")]
    public void Parse_FunctionWordsAndNesting_ShouldPreserveBindings(string source)
    {
        // Arrange / Act
        var statement = new GqlQueryParser().Parse(source);
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
    }

    public static IEnumerable<object[]> TemporalBindings()
    {
        foreach (string name in new[] { "CURRENT_DATE", "CURRENT_TIMESTAMP", "CURRENT_TIME", "LOCAL_TIME", "LOCAL_TIMESTAMP" })
        {
            foreach (string template in new[] { "MATCH ({0}:Movie) RETURN {0}", "MATCH ()-[{0}:T]->() RETURN {0}", "MATCH {0}=() RETURN {0}", "LET {0}=1 RETURN {0}", "RETURN 1 AS {0}", "FOR {0} IN [1] RETURN {0}", "UNWIND [1] AS {0} RETURN {0}" })
            { yield return [name, template]; }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Functions: reserved temporal bindings require quoting")]
    [MemberData(nameof(TemporalBindings))]
    public void Parse_TemporalBinding_ShouldRejectUnquotedAndAcceptQuoted(string name, string template)
    {
        // Arrange / Act
        var rejected = new GqlQueryParser().Parse(string.Format(template, name));
        var quoted = new GqlQueryParser().Parse(string.Format(template, "\"" + name + "\""));
        // Assert
        rejected.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG00002");
        quoted.Diagnostics.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Functions: scalar and aggregate arity use the same parse diagnostic")]
    [InlineData("RETURN abs()")]
    [InlineData("RETURN abs(1,2)")]
    [InlineData("RETURN substring('x')")]
    [InlineData("RETURN round(1,2,3)")]
    [InlineData("RETURN current_date()")]
    [InlineData("RETURN current_timestamp()")]
    [InlineData("RETURN upper(DISTINCT 'a')")]
    [InlineData("RETURN upper(ALL 'a')")]
    [InlineData("RETURN count()")]
    [InlineData("RETURN sum(1,2)")]
    [InlineData("RETURN CAST(1 AS mystery)")]
    public void Parse_InvalidSignature_ShouldUseGql0002(string source)
    {
        // Arrange / Act
        var statement = new GqlQueryParser().Parse(source);
        // Assert
        statement.Diagnostics.ShouldContain(error => error.Code == "CG00002");
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Functions: unknown calls have a distinct located diagnostic in every linear value position")]
    [InlineData("FOR x IN [1] INSERT (:P {k:foo(x)})")]
    [InlineData("LET next=foo(1) RETURN next")]
    [InlineData("LET next=1 LET x=next+foo(1) RETURN x")]
    [InlineData("LET x=foo(1) RETURN x")]
    [InlineData("FOR x IN foo(1) RETURN x")]
    [InlineData("UNWIND foo(1) AS x RETURN x")]
    [InlineData("FILTER foo(1)=1 RETURN 1")]
    [InlineData("WITH foo(1) AS x RETURN x")]
    [InlineData("MATCH (a) WHERE foo(a) IS NULL RETURN a")]
    public void Parse_UnknownFunction_ShouldNameFunctionWithGql0010(string source)
    {
        // Arrange / Act
        var error = new GqlQueryParser().Parse(source).Diagnostics.ShouldHaveSingleItem();
        // Assert
        error.Code.ShouldBe("CG00010"); error.Message!.ShouldContain("foo", Case.Sensitive);
        error.Start.ShouldNotBeNull(); error.End.ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - CAST: supported targets and SQL spellings resolve explicitly")]
    [InlineData("INT")]
    [InlineData("INTEGER")]
    [InlineData("INT64")]
    [InlineData("BIGINT")]
    [InlineData("REAL")]
    [InlineData("DOUBLE")]
    [InlineData("DOUBLE PRECISION")]
    [InlineData("FLOAT")]
    [InlineData("FLOAT64")]
    [InlineData("DECIMAL")]
    [InlineData("NUMERIC")]
    [InlineData("STRING")]
    [InlineData("CHAR")]
    [InlineData("VARCHAR")]
    [InlineData("BOOL")]
    [InlineData("BOOLEAN")]
    [InlineData("DATE")]
    [InlineData("ZONED DATETIME")]
    [InlineData("TIMESTAMP WITH TIME ZONE")]
    public void Parse_CastTargets_ShouldAcceptDeclaredTypes(string target)
    {
        // Arrange / Act
        var statement = new GqlQueryParser().Parse($"RETURN CAST(NULL AS {target})");
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - CAST: recognized later-tier types keep capability diagnostics")]
    [InlineData("LIST")]
    [InlineData("DURATION")]
    [InlineData("BYTES")]
    [InlineData("ZONED TIME")]
    [InlineData("INT32")]
    [InlineData("SMALLINT")]
    [InlineData("FLOAT32")]
    [InlineData("TIMESTAMP WITHOUT TIME ZONE")]
    [InlineData("LOCAL DATETIME")]
    public void Parse_UnsupportedCastTarget_ShouldNameCast(string target)
    {
        // Arrange / Act
        var error = new GqlQueryParser().Parse($"RETURN CAST(NULL AS {target})").Diagnostics.ShouldHaveSingleItem();
        // Assert
        error.Code.ShouldBe("CK00001"); error.Message!.ShouldContain("CAST " + target, Case.Sensitive);
    }
}
