using System;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlSqlppBasisTests
{
    [Fact(DisplayName = "Cohesion Test [OQL] - SQL++ profile reserves presence predicates and frees retired ODMG names")]
    public void Profile_SqlppBasis_ShouldDeclareRecordedMembership()
    {
        // Arrange
        var profile = OqlLanguageProfile.Instance;
        string[] reserved = ["MISSING", "VALUED", "KNOWN", "UNKNOWN", "NIL", "UNDEFINED", "SET", "ARRAY", "COLLECTION", "FIRST", "LAST", "UNIQUE"];
        string[] freed = ["DEFINE", "ELEMENT", "FLATTEN", "STRUCT", "LIST", "BAG", "LISTTOSET", "TYPEOF"];
        string[] retired = ["ELEMENT", "FLATTEN", "FIRST", "LAST", "LISTTOSET", "UNIQUE"];

        // Act / Assert
        foreach (string word in reserved) { profile.Keywords.ToArray().ShouldContain(word); }
        foreach (string word in freed) { profile.Keywords.ToArray().ShouldNotContain(word); }
        foreach (string word in retired) { profile.Functions.ToArray().ShouldNotContain(word); }
    }

    [Theory(DisplayName = "Cohesion Test [OQL] - SQL++ presence predicates retain distinct unary operators")]
    [InlineData("IS MISSING")]
    [InlineData("IS NOT MISSING")]
    [InlineData("IS VALUED")]
    [InlineData("IS NOT VALUED")]
    public void Parse_PresencePredicate_ShouldRetainOperator(string operation)
    {
        // Arrange / Act
        var statement = (OqlQueryStatement)new OqlQueryParser().Parse($"SELECT x {operation} FROM c");

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var unary = ((OqlSelectExpression)statement.OqlExpression).Projections[0].Expression.ShouldBeOfType<OqlUnaryExpression>();
        unary.Operator.ShouldBe(operation);
        unary.Operand.ShouldBeOfType<OqlPathExpression>();
    }

    [Fact(DisplayName = "Cohesion Test [OQL] - MISSING has its own AST node and quoted names remain paths")]
    public void Parse_MissingLiteralAndFieldNames_ShouldRemainDistinct()
    {
        // Arrange / Act
        var statement = (OqlQueryStatement)new OqlQueryParser().Parse("SELECT MISSING, NIL AS n, p.missing, p['missing'], \"missing\" FROM c");

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var projections = ((OqlSelectExpression)statement.OqlExpression).Projections;
        projections[0].Expression.ShouldBeOfType<OqlMissingExpression>();
        projections[1].Expression.ShouldBeOfType<OqlLiteralExpression>().Value.ShouldBeNull();
        foreach (var projection in projections.Skip(2)) { projection.Expression.ShouldBeOfType<OqlPathExpression>(); }
    }

    [Fact(DisplayName = "Cohesion Test [OQL] - Freed ODMG words parse as eight ordinary field paths")]
    public void Parse_RetiredOdmgFieldNames_ShouldRemainPaths()
    {
        // Arrange / Act
        var statement = (OqlQueryStatement)new OqlQueryParser().Parse("SELECT define, element, flatten, listtoset, list, bag, struct, typeof FROM c");

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var projections = ((OqlSelectExpression)statement.OqlExpression).Projections;
        projections.Count.ShouldBe(8);
        foreach (var projection in projections) { projection.Expression.ShouldBeOfType<OqlPathExpression>(); }
    }

    [Theory(DisplayName = "Cohesion Test [OQL] - Retired constructs and deferred presence tests retain one located capability diagnostic")]
    [InlineData("DEFINE q AS SELECT * FROM c", "DEFINE", 0)]
    [InlineData("SELECT ELEMENT(a) FROM c", "ELEMENT", 7)]
    [InlineData("SELECT FLATTEN(a) FROM c", "FLATTEN", 7)]
    [InlineData("SELECT LISTTOSET(a) FROM c", "LISTTOSET", 7)]
    [InlineData("SELECT STRUCT(a) FROM c", "STRUCT", 7)]
    [InlineData("SELECT LIST(a) FROM c", "LIST", 7)]
    [InlineData("SELECT BAG(a) FROM c", "BAG", 7)]
    [InlineData("SELECT TYPEOF(a) FROM c", "TYPEOF", 7)]
    [InlineData("SELECT FIRST(a) FROM c", "FIRST", 7)]
    [InlineData("SELECT LAST(a) FROM c", "LAST", 7)]
    [InlineData("SELECT UNIQUE(a) FROM c", "UNIQUE", 7)]
    [InlineData("SELECT a FROM c WHERE a = UNDEFINED", "UNDEFINED", 26)]
    [InlineData("SELECT a FROM c WHERE x IS UNKNOWN", "UNKNOWN", 27)]
    [InlineData("SELECT a FROM c WHERE x IS KNOWN", "KNOWN", 27)]
    public void Parse_RecognizedUnsupportedConstruct_ShouldReportOneDiagnostic(string query, string word, int start)
    {
        // Arrange / Act
        var diagnostic = new OqlQueryParser().Parse(query).Diagnostics.ShouldHaveSingleItem();

        // Assert
        diagnostic.Code.ShouldBe("CK00001");
        diagnostic.Message.ShouldNotBeNull().ShouldContain(word, Case.Sensitive);
        diagnostic.Start.ShouldBe(start);
        diagnostic.End.ShouldBe(start + word.Length);
    }
}
