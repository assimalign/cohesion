using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// The engine's one persistence helper for SQL expressions: what it stores, what it accepts
/// back, and that it refuses rather than stores a definition that would not reload.
/// </summary>
public sealed class SqlPersistedExpressionTests
{
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Persisted queries: invalid catalog text has an execution diagnostic (#1653)")]
    [InlineData("SELECT (")]
    [InlineData("DELETE FROM t")]
    public void LoadQuery_InvalidDefinition_ShouldCarryDamageDiagnostic(string text)
    {
        // Act
        var failure = Should.Throw<DatabaseException>(() => SqlPersistedExpression.LoadQuery(text, "view 'dbo.v'"));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS06058");
        failure.Diagnostics[0].Start.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Persisted bindings: an unknown catalog collation has a damage diagnostic (#1653)")]
    public void Bind_UnknownCollation_ShouldCarryDamageDiagnostic()
    {
        // Arrange: deliberately pass an invalid parsed tree beyond ordinary language admission.
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse("SELECT 'x' COLLATE absent_collation");
        var expression = ((SqlSelectExpression)statement.SqlExpression).Columns[0].Expression;
        var evaluator = new SqlExpressionEvaluator([], null);

        // Act
        var failure = Should.Throw<DatabaseException>(() => SqlPersistedExpression.Bind(expression, evaluator));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS06058");
        failure.InnerException.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Persisted expressions: canonical text reloads to an equivalent tree")]
    public void Canonicalize_ParsedPredicate_ShouldReloadEquivalent()
    {
        // Arrange
        var declared = SqlPersistedExpression.Load("((QTY - 1) - 2)>0 /* c */ and NOT \"name\" like 'a%'", "test");

        // Act
        string canonical = SqlPersistedExpression.Canonicalize(declared, "test");
        var reloaded = SqlPersistedExpression.Load(canonical, "test");

        // Assert
        canonical.ShouldBe("QTY - 1 - 2 > 0 AND NOT name LIKE 'a%'");
        SqlPersistedExpression.AreEquivalent(declared, reloaded).ShouldBeTrue();
        SqlPersistedExpression.AreEquivalent(declared, SqlPersistedExpression.Load("QTY - (1 - 2) > 0 AND NOT name LIKE 'a%'", "test"))
            .ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Persisted expressions: a node with no SQL spelling is refused, not stored")]
    public void Canonicalize_UnspellableNode_ShouldRefuse()
    {
        // Arrange
        var unspellable = new UnspellableExpression();

        // Act
        var failure = Should.Throw<DatabaseException>(() => SqlPersistedExpression.Canonicalize(unspellable, "CHECK constraint 'ck' on table 'dbo.t'"));

        // Assert
        failure.Message.ShouldStartWith("CHECK constraint 'ck' on table 'dbo.t' cannot be stored:", Case.Sensitive);
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03059");
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Persisted expressions: text that is not exactly one expression does not load")]
    [InlineData("qty >")]
    [InlineData("qty > 0 ORDER BY qty")]
    [InlineData("qty > 0 LIMIT 1")]
    [InlineData("qty > 0; DELETE FROM t")]
    [InlineData("qty > 0) OR (1 = 1")]
    [InlineData("qty > 0 /* unterminated")]
    [InlineData("")]
    public void Load_NotOneExpression_ShouldFailNamingTheDefinition(string text)
    {
        // Act
        var failure = Should.Throw<DatabaseException>(() => SqlPersistedExpression.Load(text, "CHECK constraint 'ck' on table 'dbo.t'"));

        // Assert
        failure.Message.ShouldStartWith("CHECK constraint 'ck' on table 'dbo.t' cannot be loaded:", Case.Sensitive);
        failure.Message.ShouldContain(SqlPersistedExpression.DamagedCatalogHint, Case.Sensitive);
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS06058");
        failure.Diagnostics[0].Start.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Persisted expressions: a DEFAULT loads only as one non-NULL literal")]
    [InlineData("'it''s'", "it's")]
    [InlineData("5", "5")]
    [InlineData("1.50", "1.50")]
    [InlineData("-5", "-5")]
    [InlineData("-1.50", "-1.50")]
    [InlineData("TRUE", "TRUE")]
    public void LoadDefaultValue_Literal_ShouldReturnValueText(string text, string value)
        => SqlPersistedExpression.LoadDefaultValue(text, "test").ShouldBe(value);

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Persisted expressions: a DEFAULT that is not a literal does not load")]
    [InlineData("abc")]
    [InlineData("NULL")]
    [InlineData("1 + 1")]
    [InlineData("-(-5)")]
    [InlineData("-(1 + 1)")]
    public void LoadDefaultValue_NotALiteral_ShouldFail(string text)
        => Should.Throw<DatabaseException>(() => SqlPersistedExpression.LoadDefaultValue(text, "DEFAULT of column 'c' on table 'dbo.t'"))
            .Message.ShouldContain("is not a literal value", Case.Sensitive);

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Persisted expressions: aggregate quantifier and ordering survive round trips")]
    [InlineData("COUNT(DISTINCT x)", "COUNT(x)")]
    [InlineData("STRING_AGG(x, ',' ORDER BY id)", "STRING_AGG(x, ',' ORDER BY id DESC)")]
    [InlineData("STRING_AGG(x, ',' ORDER BY id)", "STRING_AGG(x, ',' ORDER BY x)")]
    [InlineData("LISTAGG(DISTINCT x) WITHIN GROUP (ORDER BY x)", "LISTAGG(x) WITHIN GROUP (ORDER BY x)")]
    public void Canonicalize_AggregateShape_ShouldPreserveAndDistinguish(string text, string other)
    {
        // Arrange
        var declared = SqlPersistedExpression.Load(text, "test");
        // Act
        string canonical = SqlPersistedExpression.Canonicalize(declared, "test");
        // Assert
        SqlPersistedExpression.AreEquivalent(declared, SqlPersistedExpression.Load(canonical, "test")).ShouldBeTrue();
        SqlPersistedExpression.AreEquivalent(declared, SqlPersistedExpression.Load(other, "test")).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Persisted expressions: explicit ALL is equivalent to default and still renders")]
    public void Canonicalize_ExplicitAll_ShouldMatchDefaultAndRetainSpelling()
    {
        // Arrange
        var declared = SqlPersistedExpression.Load("COUNT(ALL x)", "test");
        // Act / Assert
        SqlPersistedExpression.Canonicalize(declared, "test").ShouldBe("COUNT(ALL x)");
        SqlPersistedExpression.AreEquivalent(declared, SqlPersistedExpression.Load("COUNT(x)", "test")).ShouldBeTrue();
    }

    /// <summary>An expression node defined outside the language package, which no SQL text spells.</summary>
    private sealed class UnspellableExpression : SqlExpression
    {
        public UnspellableExpression()
            : base(null)
        {
        }
    }
}
