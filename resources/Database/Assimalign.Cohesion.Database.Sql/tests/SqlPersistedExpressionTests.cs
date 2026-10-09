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
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Persisted expressions: a DEFAULT loads only as one non-NULL literal")]
    [InlineData("'it''s'", "it's")]
    [InlineData("5", "5")]
    [InlineData("1.50", "1.50")]
    [InlineData("TRUE", "TRUE")]
    public void LoadDefaultValue_Literal_ShouldReturnValueText(string text, string value)
        => SqlPersistedExpression.LoadDefaultValue(text, "test").ShouldBe(value);

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Persisted expressions: a DEFAULT that is not a literal does not load")]
    [InlineData("abc")]
    [InlineData("NULL")]
    [InlineData("1 + 1")]
    [InlineData("-5")]
    public void LoadDefaultValue_NotALiteral_ShouldFail(string text)
        => Should.Throw<DatabaseException>(() => SqlPersistedExpression.LoadDefaultValue(text, "DEFAULT of column 'c' on table 'dbo.t'"))
            .Message.ShouldContain("is not a literal value", Case.Sensitive);

    /// <summary>An expression node defined outside the language package, which no SQL text spells.</summary>
    private sealed class UnspellableExpression : SqlExpression
    {
        public UnspellableExpression()
            : base(null)
        {
        }
    }
}
