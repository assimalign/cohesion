using System;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlReturningParserTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Language] - RETURNING: all write forms retain projections and clause spans")]
    [InlineData("INSERT INTO items AS d (KEY, VALUE) VALUES ('a', {})", "d.n AS label, META(d).version AS version", false, 2)]
    [InlineData("UPSERT INTO items AS d (KEY, VALUE) VALUES ('a', {})", "RAW d", true, 1)]
    [InlineData("UPDATE items AS d SET n = 1", "VALUE [d.n, MISSING]", true, 1)]
    [InlineData("DELETE FROM items d WHERE n = 1", "*", false, 1)]
    [InlineData("INSERT INTO items (KEY returning) SELECT META().id AS returning FROM source", "returning", false, 1)]
    [InlineData("UPSERT INTO items (KEY k, VALUE v) SELECT META().id AS k, n AS v FROM source LIMIT 2", "items.n AS union", false, 1)]
    [InlineData("INSERT INTO items (KEY, VALUE) VALUES ('e', $doc)", "k", false, 1)]
    [InlineData("INSERT INTO archive (KEY k) SELECT META(d).id AS k FROM items d WHERE d.category = 'a'", "k", false, 1)]
    [InlineData("INSERT INTO archive (KEY k) SELECT META(d).id AS k FROM items d WHERE d.details = {'score':1}", "k", false, 1)]
    [InlineData("UPSERT INTO items (KEY, VALUE) VALUES ('e', $doc)", "k", false, 1)]
    [InlineData("UPSERT INTO archive (KEY k) SELECT META(d).id AS k FROM items d WHERE d.category = 'a'", "k", false, 1)]
    [InlineData("UPSERT INTO archive (KEY k) SELECT META(d).id AS k FROM items d WHERE d.details = {'score':1}", "k", false, 1)]
    [InlineData("UPDATE items SET a = 1", "a", false, 1)]
    [InlineData("DELETE FROM items WHERE amount > 3", "amount", false, 1)]
    [InlineData("INSERT INTO archive (KEY k) SELECT n AS k FROM items LIMIT 1", "META().id AS id, n", false, 2)]
    [InlineData("UPSERT INTO archive (KEY k) SELECT n AS k FROM items OFFSET 0 LIMIT 1", "META().id AS id, n", false, 2)]
    [InlineData("DELETE FROM items", "returning AS x", false, 1)]
    [InlineData("DELETE FROM items", "returning + 1", false, 1)]
    [InlineData("DELETE FROM items", "returning * 2", false, 1)]
    [InlineData("DELETE FROM items", "returning.n", false, 1)]
    [InlineData("DELETE FROM items", "returning, n", false, 2)]
    [InlineData("DELETE FROM items", "returning[0]", false, 1)]
    [InlineData("INSERT INTO archive (KEY k) SELECT n AS k FROM items", "returning + 1", false, 1)]
    public void Parse_WriteReturning_ShouldRetainStructure(string write, string projection, bool value, int count)
    {
        // Arrange
        string query = write + " RETURNING " + projection;

        // Act
        var statement = new OqlQueryParser().Parse(query).ShouldBeOfType<OqlQueryStatement>();

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var returning = GetReturning(statement.OqlExpression).ShouldNotBeNull();
        returning.IsValue.ShouldBe(value);
        returning.Projections.Count.ShouldBe(count);
        returning.Location.ShouldNotBeNull().Start.ShouldBe(write.Length + 1);
        returning.Location.End.ShouldBe(query.Length);
        statement.OqlExpression.Text.ShouldBe(query);
        statement.OqlExpression.Location.ShouldNotBeNull().End.ShouldBe(query.Length);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - RETURNING: malformed bare projections keep syntax diagnostics")]
    [InlineData("UPDATE items SET n = 1 RETURNING", "")]
    [InlineData("UPDATE items SET n = 1 RETURNING RAW", "")]
    [InlineData("UPDATE items SET n = 1 RETURNING VALUE *", "*")]
    [InlineData("UPDATE items SET n = 1 RETURNING RAW n AS x", "AS")]
    [InlineData("UPDATE items SET n = 1 RETURNING VALUE n, n", ",")]
    [InlineData("UPDATE items SET n = 1 RETURNING n,", "")]
    [InlineData("DELETE FROM items WHERE TRUE RETURNING n AS", "")]
    public void Parse_InvalidReturning_ShouldLocateSyntaxError(string query, string token)
    {
        // Arrange / Act
        var statement = new OqlQueryParser().Parse(query);

        // Assert
        var error = statement.Diagnostics.ShouldHaveSingleItem();
        error.Code.ShouldBe("CD00002");
        int start = token.Length == 0 ? query.Length : query.LastIndexOf(token, StringComparison.Ordinal);
        error.Start.ShouldBe(start);
        error.End.ShouldBe(start + token.Length);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - RETURNING: deferred constructs preserve one capability diagnostic")]
    [InlineData("UPDATE items SET n = 1 RETURNING (SELECT n FROM c)", "SELECT", "SUBQUERY")]
    [InlineData("INSERT INTO items (KEY, VALUE) VALUES ('a', {}) RETURNING (SELECT n FROM c)", "SELECT", "SUBQUERY")]
    [InlineData("UPSERT INTO items (KEY k) SELECT n AS k FROM c RETURNING (SELECT n FROM c)", "SELECT", "SUBQUERY")]
    [InlineData("DELETE FROM items RETURNING ELEMENT(n)", "ELEMENT", "ELEMENT")]
    [InlineData("SELECT n FROM items WHERE TRUE RETURNING n", "RETURNING", "RETURNING")]
    [InlineData("RETURNING n", "RETURNING", "RETURNING")]
    public void Parse_DeferredReturningConstruct_ShouldKeepCapabilityDiagnostic(string query, string token, string construct)
    {
        // Arrange / Act
        var statement = new OqlQueryParser().Parse(query);

        // Assert
        if (construct == "SUBQUERY") { statement.Diagnostics.ShouldBeEmpty(); return; }
        var error = statement.Diagnostics.ShouldHaveSingleItem();
        error.Code.ShouldBe("CK00001");
        error.Message.ShouldNotBeNull().ShouldContain(construct, Case.Sensitive);
        error.Start.ShouldBe(query.LastIndexOf(token, StringComparison.Ordinal));
        error.End.ShouldBe(error.Start + token.Length);
    }

    private static OqlReturningExpression? GetReturning(OqlExpression expression) => expression switch
    {
        OqlInsertExpression insert => insert.Returning,
        OqlUpsertExpression upsert => upsert.Returning,
        OqlUpdateExpression update => update.Returning,
        OqlDeleteExpression delete => delete.Returning,
        _ => null,
    };
}
