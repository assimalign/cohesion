using System;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlDmlParserTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Language] - UPDATE: assignment, removal and predicate spans are retained")]
    [InlineData("UPDATE items SET amount = amount + 1 WHERE name = 'alpha'", null, 1, 0, true)]
    [InlineData("UPDATE items AS d SET d.details.score = 5, d.tags[0] = 'red' UNSET d.category WHERE d.amount > 3", "d", 2, 1, true)]
    [InlineData("UPDATE items UNSET details", null, 0, 1, false)]
    [InlineData("UPDATE items AS d SET d['odd.name'] = 1", "d", 1, 0, false)]
    [InlineData("UPDATE items AS d SET d.set = 1", "d", 1, 0, false)]
    [InlineData("UPDATE items returning SET returning.a = 1", "returning", 1, 0, false)]
    [InlineData("UPDATE items returning UNSET returning.a", "returning", 0, 1, false)]
    [InlineData("UPDATE items d SET d.a = {'b':[1, MISSING]} UNSET d.c, d.e", "d", 1, 2, false)]
    public void Parse_UpdateForms_ShouldRetainStructureAndSpans(string query, string? alias, int assignments, int unset, bool predicate)
    {
        // Arrange / Act
        var statement = new OqlQueryParser().Parse(query).ShouldBeOfType<OqlQueryStatement>();
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var update = statement.OqlExpression.ShouldBeOfType<OqlUpdateExpression>();
        update.Collection.ShouldBe("items");
        update.Alias.ShouldBe(alias);
        update.Assignments.Count.ShouldBe(assignments);
        update.Unset.Count.ShouldBe(unset);
        (update.Predicate is not null).ShouldBe(predicate);
        update.Location.ShouldNotBeNull().Start.ShouldBe(0);
        update.Location.End.ShouldBe(query.Length);
        foreach (var assignment in update.Assignments)
        {
            var span = assignment.Location.ShouldNotBeNull();
            span.Start.ShouldBe(assignment.Path.Location.ShouldNotBeNull().Start);
            span.End.ShouldBe(assignment.Value.Location.ShouldNotBeNull().End);
            query[span.Start..span.End].ShouldContain(" = ", Case.Sensitive);
        }
        foreach (var path in update.Unset)
        {
            path.Location.ShouldNotBeNull();
            query[path.Location.Start..path.Location.End].ShouldStartWith(path.Segments[0].Name!);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - UPDATE: malformed and deferred syntax has exact spans")]
    [InlineData("UPDATE items", "OQL0002", "", "SET")]
    [InlineData("UPDATE use", "OQL0002", "", "SET")]
    [InlineData("UPDATE items SET = 1", "OQL0002", "=", "identifier")]
    [InlineData("UPDATE items SET a 1", "OQL0002", "1", "=")]
    [InlineData("UPDATE items SET a = 1 garbage", "OQL0002", "garbage", "Unexpected")]
    [InlineData("UPDATE items SET a = 1 RETURNING a", "COHDBL001", "RETURNING", "RETURNING")]
    [InlineData("UPDATE items SET a = 1 FOR t IN tags END", "COHDBL001", "FOR", "FOR")]
    [InlineData("UPDATE items USE KEYS 'a' SET a = 1", "COHDBL001", "USE", "USE")]
    [InlineData("UPDATE items SET a = 1 LIMIT 1", "COHDBL001", "LIMIT", "LIMIT")]
    [InlineData("UPDATE items UNSET a LIMIT 1", "COHDBL001", "LIMIT", "LIMIT")]
    [InlineData("SELECT SET(1) FROM items", "COHDBL001", "SET", "SET")]
    [InlineData("UPDATE items SET a = SET(1)", "COHDBL001", "SET(1)", "SET")]
    [InlineData("UPDATE items SET a = (SELECT x FROM c)", "COHDBL001", "SELECT", "SUBQUERY")]
    [InlineData("UPDATE items SET a = 1 WHERE (SELECT x FROM c)", "COHDBL001", "SELECT", "SUBQUERY")]
    [InlineData("EXPLAIN UPDATE items SET a = (SELECT x FROM c)", "COHDBL001", "SELECT", "SUBQUERY")]
    public void Parse_InvalidUpdate_ShouldLocateFailure(string query, string code, string token, string message)
    {
        // Arrange / Act
        var statement = new OqlQueryParser().Parse(query);
        // Assert
        var diagnostic = statement.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(code);
        diagnostic.Message.ShouldNotBeNull().ShouldContain(message, Case.Sensitive);
        int start = token.Length == 0 ? query.Length : query.IndexOf(token, StringComparison.Ordinal);
        diagnostic.Start.ShouldBe(start);
        diagnostic.End.ShouldBe(start + (token == "SET(1)" ? 3 : token.Length));
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - DELETE: source, alias, predicate and spans are retained")]
    [InlineData("DELETE FROM items", null, false)]
    [InlineData("DELETE FROM items WHERE amount > 3", null, true)]
    [InlineData("DELETE FROM items AS d WHERE d.details.score IS NULL", "d", true)]
    [InlineData("DELETE FROM items d WHERE META(d).id = $id", "d", true)]
    public void Parse_DeleteForms_ShouldRetainStructureAndSpans(string query, string? alias, bool filtered)
    {
        // Arrange / Act
        var statement = (OqlQueryStatement)new OqlQueryParser().Parse(query);
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var delete = statement.OqlExpression.ShouldBeOfType<OqlDeleteExpression>();
        delete.Collection.ShouldBe("items");
        delete.Alias.ShouldBe(alias);
        (delete.Predicate is not null).ShouldBe(filtered);
        delete.Location!.Start.ShouldBe(0);
        delete.Location.End.ShouldBe(query.Length);
        delete.Location.StartLine.ShouldBe(1);
        delete.Text.ShouldBe(query);
        if (delete.Predicate is { } predicate)
        {
            predicate.Location!.Start.ShouldBe(query.IndexOf("WHERE", StringComparison.Ordinal) + 6);
            predicate.Location.End.ShouldBe(query.Length);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - DELETE: invalid and deferred forms have exact diagnostics")]
    [InlineData("DELETE items", "OQL0002", "items")]
    [InlineData("DELETE FROM other.items", "OQL0002", ".")]
    [InlineData("DELETE FROM items d garbage", "OQL0002", "garbage")]
    [InlineData("DELETE FROM items WHERE amount > 3 garbage", "OQL0002", "garbage")]
    [InlineData("DELETE FROM items; garbage", "OQL0002", "garbage")]
    [InlineData("DELETE FROM items WHERE amount > 3 RETURNING amount", "COHDBL001", "RETURNING")]
    [InlineData("DELETE FROM items WHERE amount > 3 LIMIT 1", "COHDBL001", "LIMIT")]
    [InlineData("DELETE FROM items LIMIT 1", "COHDBL001", "LIMIT")]
    [InlineData("DELETE FROM items USE KEYS 'a'", "COHDBL001", "USE")]
    public void Parse_InvalidDelete_ShouldLocateFailure(string query, string code, string token)
    {
        // Arrange / Act
        var statement = new OqlQueryParser().Parse(query);
        // Assert
        var diagnostic = statement.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(code);
        diagnostic.Start.ShouldBe(query.IndexOf(token, StringComparison.Ordinal));
        diagnostic.End.ShouldBe(diagnostic.Start + token.Length);
        diagnostic.Line.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - DML: nested SELECT reports SUBQUERY at its keyword")]
    [InlineData("DELETE FROM items WHERE amount IN (SELECT VALUE amount FROM other)")]
    [InlineData("DELETE FROM items WHERE amount = (SELECT RAW amount FROM other)")]
    [InlineData("EXPLAIN DELETE FROM items WHERE amount IN (SELECT amount FROM other)")]
    [InlineData("UPSERT INTO items (KEY, VALUE) VALUES ('a', (SELECT VALUE amount FROM other))")]
    [InlineData("EXPLAIN UPSERT INTO items (KEY, VALUE) VALUES ('a', (SELECT amount FROM other))")]
    [InlineData("INSERT INTO items (KEY, VALUE) VALUES ('a', (SELECT VALUE amount FROM other))")]
    [InlineData("INSERT INTO items (KEY, VALUE) VALUES ((SELECT RAW id FROM other), 1)")]
    [InlineData("EXPLAIN INSERT INTO items (KEY, VALUE) VALUES ('a', (SELECT amount FROM other))")]
    public void Parse_DmlSubquery_ShouldLocateOneCapabilityDiagnostic(string query)
    {
        // Arrange / Act
        var statement = new OqlQueryParser().Parse(query);

        // Assert
        var diagnostic = statement.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("COHDBL001");
        diagnostic.Message.ShouldNotBeNull().ShouldContain("SUBQUERY", Case.Sensitive);
        diagnostic.Start.ShouldBe(query.IndexOf("SELECT", StringComparison.Ordinal));
        diagnostic.End.ShouldBe(diagnostic.Start + "SELECT".Length);
        diagnostic.Line.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - INSERT: KEY/VALUE forms retain complete spans")]
    [InlineData("INSERT INTO items (KEY, VALUE) VALUES ('e', $doc)", 1, null, null, null)]
    [InlineData("INSERT INTO items (KEY, VALUE) VALUES ('e', $d1), ('f', $d2)", 2, null, null, null)]
    [InlineData("INSERT INTO items AS i (KEY, VALUE) VALUES ('e', $doc)", 1, "i", null, null)]
    [InlineData("INSERT INTO archive (KEY k, VALUE v) SELECT META(d).id AS k, d AS v FROM items AS d WHERE d.category = 'a'", 0, null, "k", "v")]
    [InlineData("INSERT INTO archive (KEY k) SELECT META(d).id AS k FROM items AS d", 0, null, "k", null)]
    public void Parse_InsertForms_ShouldRetainStructureAndSpans(string query, int count, string? alias, string? key, string? value)
    {
        // Arrange / Act
        var statement = (OqlQueryStatement)new OqlQueryParser().Parse(query);
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var insert = statement.OqlExpression.ShouldBeOfType<OqlInsertExpression>();
        insert.Collection.ShouldBe(key is null ? "items" : "archive");
        insert.Alias.ShouldBe(alias);
        insert.Values.Count.ShouldBe(count);
        insert.KeyName.ShouldBe(key);
        insert.ValueName.ShouldBe(value);
        insert.Location!.Start.ShouldBe(0);
        insert.Location.End.ShouldBe(query.Length);
        insert.Text.ShouldBe(query);
        if (count > 0)
        {
            insert.Values[0].Key.Location!.Start.ShouldBe(query.IndexOf("'e'", StringComparison.Ordinal));
            insert.Values[0].Key.Location!.End.ShouldBe(query.IndexOf("'e'", StringComparison.Ordinal) + 3);
            insert.Values[0].Value.ShouldBeOfType<OqlParameterExpression>();
        }
        else
        {
            insert.Select!.Location!.Start.ShouldBe(query.IndexOf("SELECT", StringComparison.Ordinal));
            insert.Select.Projections[0].Expression.ShouldBeOfType<OqlMetaExpression>().Member.ShouldBe("id");
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - INSERT: malformed and deferred forms have exact diagnostics")]
    [InlineData("INSERT INTO items VALUE $doc", "OQL0002", "VALUE", "KEY")]
    [InlineData("INSERT INTO items VALUES ($doc)", "OQL0002", "VALUES", "KEY")]
    [InlineData("INSERT INTO items (KEY, VALUE) VALUES ('e')", "OQL0002", ")", "VALUE")]
    [InlineData("INSERT INTO other.items (KEY, VALUE) VALUES ('e', $doc)", "OQL0002", ".", "KEY")]
    [InlineData("INSERT INTO items (KEY, VALUE) VALUES ('e', $doc) garbage", "OQL0002", "garbage", "Unexpected")]
    [InlineData("INSERT INTO items (KEY, VALUE, OPTIONS) VALUES ('e', $doc, $o)", "COHDBL001", "OPTIONS", "OPTIONS")]
    [InlineData("INSERT INTO items (KEY, VALUE) VALUES ('e', $doc) RETURNING k", "COHDBL001", "RETURNING", "RETURNING")]
    [InlineData("INSERT INTO archive (KEY k) SELECT META(d).id AS k FROM items d WHERE d.category = 'a' RETURNING k", "COHDBL001", "RETURNING", "RETURNING")]
    [InlineData("INSERT INTO archive (KEY k) SELECT META(d).id AS k FROM items d WHERE d.details = {'score':1} RETURNING k", "COHDBL001", "RETURNING", "RETURNING")]
    [InlineData("INSERT INTO archive (KEY k, OPTIONS o) SELECT META(d).id AS k FROM items d", "COHDBL001", "OPTIONS", "OPTIONS")]
    [InlineData("INSERT INTO archive (KEY k, OPTIONS {'expiration': 10}) SELECT META(d).id AS k FROM items d", "COHDBL001", "OPTIONS", "OPTIONS")]
    [InlineData("INSERT INTO archive (KEY k, VALUE v, OPTIONS o) SELECT META(d).id AS k, d AS v FROM items d", "COHDBL001", "OPTIONS", "OPTIONS")]
    public void Parse_InvalidInsert_ShouldLocateFailure(string query, string code, string token, string message)
    {
        // Arrange / Act
        var statement = new OqlQueryParser().Parse(query);
        // Assert
        var diagnostic = statement.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(code);
        diagnostic.Message.ShouldNotBeNull().ShouldContain(message, Case.Sensitive);
        int start = token == ")" ? query.LastIndexOf(')') : query.IndexOf(token, StringComparison.Ordinal);
        diagnostic.Start.ShouldBe(start);
        diagnostic.End.ShouldBe(start + token.Length);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - UPSERT: KEY/VALUE forms retain complete spans")]
    [InlineData("UPSERT INTO items (KEY, VALUE) VALUES ('e', $doc)", 1, null, null, null)]
    [InlineData("UPSERT INTO items (KEY, VALUE) VALUES ('e', $d1), ('f', $d2)", 2, null, null, null)]
    [InlineData("UPSERT INTO items AS u (KEY, VALUE) VALUES ('e', $doc)", 1, "u", null, null)]
    [InlineData("UPSERT INTO archive (KEY k, VALUE v) SELECT META(d).id AS k, d AS v FROM items AS d WHERE d.category = 'a'", 0, null, "k", "v")]
    [InlineData("UPSERT INTO archive (KEY k) SELECT META(d).id AS k FROM items AS d", 0, null, "k", null)]
    public void Parse_UpsertForms_ShouldRetainStructureAndSpans(string query, int count, string? alias, string? key, string? value)
    {
        // Arrange / Act
        var statement = (OqlQueryStatement)new OqlQueryParser().Parse(query);
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var insert = statement.OqlExpression.ShouldBeOfType<OqlUpsertExpression>();
        insert.Collection.ShouldBe(key is null ? "items" : "archive");
        insert.Alias.ShouldBe(alias);
        insert.Values.Count.ShouldBe(count);
        insert.KeyName.ShouldBe(key);
        insert.ValueName.ShouldBe(value);
        insert.Location!.Start.ShouldBe(0);
        insert.Location.End.ShouldBe(query.Length);
        insert.Text.ShouldBe(query);
        if (count > 0)
        {
            insert.Values[0].Key.Location!.Start.ShouldBe(query.IndexOf("'e'", StringComparison.Ordinal));
            insert.Values[0].Key.Location!.End.ShouldBe(query.IndexOf("'e'", StringComparison.Ordinal) + 3);
            insert.Values[0].Value.ShouldBeOfType<OqlParameterExpression>();
        }
        else
        {
            insert.Select!.Location!.Start.ShouldBe(query.IndexOf("SELECT", StringComparison.Ordinal));
            insert.Select.Projections[0].Expression.ShouldBeOfType<OqlMetaExpression>().Member.ShouldBe("id");
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - UPSERT: malformed and deferred forms have exact diagnostics")]
    [InlineData("UPSERT INTO items (KEY, VALUE) VALUES ('e', $doc, 1)", "OQL0002", ", 1", ")")]
    [InlineData("UPSERT INTO items VALUE $doc", "OQL0002", "VALUE", "KEY")]
    [InlineData("UPSERT INTO items VALUES ($doc)", "OQL0002", "VALUES", "KEY")]
    [InlineData("UPSERT INTO items (KEY, VALUE) VALUES ('e')", "OQL0002", ")", "VALUE")]
    [InlineData("UPSERT INTO other.items (KEY, VALUE) VALUES ('e', $doc)", "OQL0002", ".", "KEY")]
    [InlineData("UPSERT INTO items (KEY, VALUE) VALUES ('e', $doc) garbage", "OQL0002", "garbage", "Unexpected")]
    [InlineData("UPSERT INTO items (KEY, VALUE, OPTIONS) VALUES ('e', $doc, $o)", "COHDBL001", "OPTIONS", "OPTIONS")]
    [InlineData("UPSERT INTO items (KEY, VALUE) VALUES ('e', $doc) RETURNING k", "COHDBL001", "RETURNING", "RETURNING")]
    [InlineData("UPSERT INTO archive (KEY k) SELECT META(d).id AS k FROM items d WHERE d.category = 'a' RETURNING k", "COHDBL001", "RETURNING", "RETURNING")]
    [InlineData("UPSERT INTO archive (KEY k) SELECT META(d).id AS k FROM items d WHERE d.details = {'score':1} RETURNING k", "COHDBL001", "RETURNING", "RETURNING")]
    [InlineData("UPSERT INTO archive (KEY k, OPTIONS o) SELECT META(d).id AS k FROM items d", "COHDBL001", "OPTIONS", "OPTIONS")]
    [InlineData("UPSERT INTO archive (KEY k, OPTIONS {'expiration': 10}) SELECT META(d).id AS k FROM items d", "COHDBL001", "OPTIONS", "OPTIONS")]
    [InlineData("UPSERT INTO archive (KEY k, VALUE v, OPTIONS o) SELECT META(d).id AS k, d AS v FROM items d", "COHDBL001", "OPTIONS", "OPTIONS")]
    public void Parse_InvalidUpsert_ShouldLocateFailure(string query, string code, string token, string message)
    {
        // Arrange / Act
        var statement = new OqlQueryParser().Parse(query);
        // Assert
        var diagnostic = statement.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(code);
        diagnostic.Message.ShouldNotBeNull().ShouldContain(message, Case.Sensitive);
        int start = token == ")" ? query.LastIndexOf(')') : query.IndexOf(token, StringComparison.Ordinal);
        diagnostic.Start.ShouldBe(start);
        diagnostic.End.ShouldBe(start + (token == ", 1" ? 1 : token.Length));
        diagnostic.Line.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - META: function vocabulary preserves paths and aliases")]
    [InlineData("DELETE FROM returning")]
    [InlineData("UPDATE use SET a = 1")]
    [InlineData("UPDATE use UNSET a")]
    [InlineData("UPDATE items use SET use.a = 1")]
    [InlineData("UPDATE items use UNSET use.a")]
    [InlineData("DELETE FROM items returning WHERE returning.n = 1")]
    [InlineData("DELETE FROM items AS returning WHERE returning.n = 1")]
    [InlineData("EXPLAIN DELETE FROM items d WHERE META(d).id = 'a'")]
    [InlineData("SELECT key FROM items WHERE key = 'x'")]
    [InlineData("SELECT values, into FROM items")]
    [InlineData("SELECT meta FROM items")]
    [InlineData("SELECT d.meta FROM items d")]
    [InlineData("SELECT meta.name FROM items meta")]
    [InlineData("SELECT META().id FROM items")]
    [InlineData("INSERT INTO items (KEY, VALUE) VALUES ('k', {'omitted': MISSING, 'kept': NULL})")]
    [InlineData("INSERT INTO returning (KEY, VALUE) VALUES ('a', 1)")]
    [InlineData("INSERT INTO items (KEY returning) SELECT META().id AS returning FROM source")]
    [InlineData("INSERT INTO items (KEY k, VALUE returning) SELECT META().id AS k, n AS returning FROM source")]
    [InlineData("CREATE COLLECTION returning")]
    [InlineData("SELECT name FROM returning")]
    [InlineData("SELECT returning FROM items")]
    [InlineData("SELECT n AS returning FROM items")]
    [InlineData("SELECT returning.n FROM items AS returning")]
    [InlineData("SELECT returning.n FROM items returning")]
    [InlineData("SELECT returning.n FROM items returning WHERE returning.n = 1 ORDER BY returning.n")]
    [InlineData("INSERT INTO items AS returning (KEY, VALUE) VALUES ('a', {returning: 1})")]
    [InlineData("CREATE INDEX returning ON returning (returning)")]
    [InlineData("DROP INDEX returning ON returning")]
    [InlineData("EXPLAIN INSERT INTO returning (KEY returning, VALUE v) SELECT META().id AS returning, returning.v AS v FROM source returning")]
    public void Parse_ContextualNames_ShouldRemainUsable(string query)
    {
        // Arrange / Act / Assert
        new OqlQueryParser().Parse(query).Diagnostics.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - RETURNING: actual trailing clauses remain unsupported after ordinary names")]
    [InlineData("DELETE FROM returning RETURNING *")]
    [InlineData("DELETE FROM items returning RETURNING *")]
    [InlineData("DELETE FROM items WHERE n = 1 RETURNING returning")]
    [InlineData("INSERT INTO returning (KEY, VALUE) VALUES ('a', 1) RETURNING *")]
    [InlineData("INSERT INTO items (KEY returning) SELECT META().id AS returning FROM source RETURNING *")]
    [InlineData("INSERT INTO items (KEY k, VALUE returning) SELECT META().id AS k, n AS returning FROM source RETURNING returning")]
    [InlineData("INSERT INTO items (KEY k) SELECT META().id AS k FROM source returning RETURNING *")]
    public void Parse_TrailingReturning_ShouldLocateTheClause(string query)
    {
        // Arrange / Act
        var statement = new OqlQueryParser().Parse(query);

        // Assert
        var diagnostic = statement.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("COHDBL001");
        diagnostic.Start.ShouldBe(query.LastIndexOf("RETURNING", StringComparison.Ordinal));
        diagnostic.End.ShouldBe(diagnostic.Start + "RETURNING".Length);
        diagnostic.Message.ShouldNotBeNull().ShouldContain("RETURNING", Case.Sensitive);
    }
}
