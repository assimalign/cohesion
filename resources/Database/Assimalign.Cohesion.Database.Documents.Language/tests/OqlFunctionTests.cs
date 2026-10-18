using System;
using System.Collections.Generic;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlFunctionTests
{
    private const string scalarNames = "LOWER UPPER LENGTH SUBSTR POSITION CONTAINS STARTS_WITH ENDS_WITH REPLACE TRIM CONCAT ABS ROUND TRUNC FLOOR CEIL POWER SQRT NOW_STR NOW_MILLIS STR_TO_MILLIS MILLIS_TO_STR TYPE IS_ARRAY IS_OBJECT IS_STRING IS_NUMBER IS_BOOLEAN ARRAY_LENGTH ARRAY_CONTAINS COALESCE IFMISSING IFNULL IFMISSINGORNULL";

    [Fact(DisplayName = "Cohesion Test [OQL] - Functions: the exact profile and table agree")]
    public void Profile_FunctionTable_ShouldContainExactVocabulary()
    {
        // Arrange
        var expected = scalarNames.Split(' ').Concat(["META", "COUNT", "SUM", "AVG", "MIN", "MAX"]).Order().ToArray();
        // Act / Assert
        OqlFunctions.All.Select(function => function.Name).Order().ToArray().ShouldBe(expected);
        OqlLanguageProfile.Instance.Functions.ToArray().Order().ToArray().ShouldBe(expected);
    }

    public static IEnumerable<object[]> Calls => OqlFunctions.All.Where(function => function.Name != "META")
        .Select(function => new object[] { function.Name, function.MinimumArguments });

    [Theory(DisplayName = "Cohesion Test [OQL] - Functions: every call parses in all expression positions")]
    [MemberData(nameof(Calls))]
    public void Parse_DeclaredCall_ShouldAcceptEveryPosition(string name, int arity)
    {
        // Arrange
        string call = name.ToLowerInvariant() + "(" + string.Join(",", Enumerable.Repeat("d.name + 1", arity)) + ")";
        string[] queries = [ $"SELECT {call} FROM items d", $"SELECT d.name FROM items d WHERE {call}",
            $"SELECT {call} FROM items d GROUP BY {call} HAVING {call} ORDER BY {call}", $"SELECT SUM({call}) FROM items d" ];
        // Act / Assert: aggregate placement belongs to the planner.
        foreach (string query in queries) { new OqlQueryParser().Parse(query).Diagnostics.ShouldBeEmpty(query); }
    }

    [Theory(DisplayName = "Cohesion Test [OQL] - Functions: unknown names produce one located diagnostic after argument parsing")]
    [InlineData("LOWER2()")]
    [InlineData("LOWER2(d.name)")]
    [InlineData("LOWER2(d.name + 1, 2)")]
    public void Parse_UnknownCall_ShouldReportOnlyOql0008(string call)
    {
        // Arrange / Act
        var diagnostic = new OqlQueryParser().Parse($"SELECT {call} FROM items d").Diagnostics.ShouldHaveSingleItem();
        // Assert
        diagnostic.Code.ShouldBe("OQL0008");
        diagnostic.Message.ShouldNotBeNull().ShouldContain("LOWER2", Case.Sensitive);
        diagnostic.Start.ShouldBe(7);
        diagnostic.End.ShouldBe(13);
    }

    [Theory(DisplayName = "Cohesion Test [OQL] - Functions: invalid arity and stars produce one located OQL0006")]
    [InlineData("LOWER()", 5)]
    [InlineData("LOWER(a,b)", 5)]
    [InlineData("CONCAT(a)", 6)]
    [InlineData("NOW_STR(a)", 7)]
    [InlineData("NOW_MILLIS(*)", 10)]
    [InlineData("SUBSTR(a)", 6)]
    [InlineData("POSITION(a,b,c)", 8)]
    [InlineData("REPLACE(a,b)", 7)]
    [InlineData("REPLACE(a,b,c,d,e)", 7)]
    [InlineData("META(a,b).id", 4)]
    public void Parse_InvalidArguments_ShouldReportOql0006(string call, int length)
    {
        // Arrange / Act
        var diagnostic = new OqlQueryParser().Parse($"SELECT {call} FROM items").Diagnostics.ShouldHaveSingleItem();
        // Assert
        diagnostic.Code.ShouldBe("OQL0006");
        diagnostic.Start.ShouldBe(7);
        diagnostic.End.ShouldBe(7 + length);
        string function = call[..call.IndexOf('(')];
        diagnostic.Message.ShouldBe(call.Contains('*')
            ? $"Invalid argument for OQL function '{function}'; only COUNT accepts '*'."
            : $"Invalid argument count for OQL function '{function}'.");
    }

    [Theory(DisplayName = "Cohesion Test [OQL] - Functions: REPLACE accepts its optional numeric count")]
    [InlineData("SELECT REPLACE(name,'a','b',2) FROM items")]
    [InlineData("SELECT REPLACE(name,'a','b',-2) FROM items")]
    public void Parse_ReplaceCount_ShouldAcceptFourthArgument(string query)
    {
        // Arrange / Act
        var statement = new OqlQueryParser().Parse(query);
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [OQL] - Functions: names stay usable as paths and aliases")]
    [InlineData("SELECT length, d.type FROM items d")]
    [InlineData("SELECT type.name FROM items type")]
    [InlineData("SELECT abs FROM items")]
    public void Parse_FunctionField_ShouldRemainPath(string query)
    {
        // Arrange / Act
        var statement = (OqlQueryStatement)new OqlQueryParser().Parse(query);
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        foreach (var projection in ((OqlSelectExpression)statement.OqlExpression).Projections)
        { projection.Expression.ShouldBeOfType<OqlPathExpression>(); }
    }

    [Fact(DisplayName = "Cohesion Test [OQL] - Functions: quantified bindings admit function tokens")]
    public void Parse_QuantifierFunctionBinding_ShouldParse()
    {
        // Arrange / Act
        var statement = new OqlQueryParser().Parse("SELECT ANY type IN c.items SATISFIES type.x END FROM c");
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
    }
}
