using System.Collections.Generic;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlReturningParserTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - RETURNING: DML retains the complete projection")]
    [InlineData("INSERT INTO t VALUES (4,'B',33) RETURNING *;", 1)]
    [InlineData("INSERT INTO t (id) SELECT id FROM u RETURNING id, name AS n;", 2)]
    [InlineData("INSERT INTO t DEFAULT VALUES RETURNING id;", 1)]
    [InlineData("INSERT INTO t WITH c AS (SELECT id FROM u) SELECT id FROM c RETURNING id;", 1)]
    [InlineData("UPDATE t AS x SET age = age + 1 WHERE id = 1 RETURNING x.id, age * 2;", 2)]
    [InlineData("DELETE FROM t WHERE id = 2 RETURNING *;", 1)]
    [InlineData("DELETE FROM t WHERE id IN (SELECT id FROM u) RETURNING *;", 1)]
    [InlineData("WITH c AS (SELECT id FROM u) DELETE FROM t WHERE id IN (SELECT id FROM c) RETURNING *;", 1)]
    public void Parse_ReturningDml_ShouldRetainProjection(string sql, int count)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse(sql).ShouldBeOfType<SqlQueryStatement>();

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        IReadOnlyList<SqlSelectColumn>? returning = statement.SqlExpression switch
        {
            SqlInsertExpression insert => insert.Returning,
            SqlUpdateExpression update => update.Returning,
            SqlDeleteExpression delete => delete.Returning,
            _ => null,
        };
        returning.ShouldNotBeNull().Count.ShouldBe(count);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - RETURNING: excluded forms keep precise capability names")]
    [InlineData("INSERT INTO t OUTPUT inserted.* VALUES (1)", "OUTPUT")]
    [InlineData("DELETE FROM t OUTPUT deleted.* WHERE id = 1", "OUTPUT")]
    [InlineData("DELETE FROM t output WHERE id = 1", "OUTPUT")]
    [InlineData("UPDATE t SET age = 1 OUTPUT inserted.age", "OUTPUT")]
    [InlineData("DELETE FROM t RETURNING id INTO v", "RETURNING ... INTO")]
    [InlineData("DELETE FROM t RETURNING OLD.age", "RETURNING OLD.")]
    [InlineData("UPDATE t SET age = 1 RETURNING NEW.age", "RETURNING NEW.")]
    [InlineData("DELETE FROM t RETURNING WITH (OLD AS o) *", "RETURNING WITH")]
    [InlineData("UPDATE t SET age = 1 RETURNING (SELECT MAX(id) FROM t)", "SUBQUERY in RETURNING")]
    [InlineData("INSERT INTO t VALUES (1) RETURNING (SELECT 1)", "SUBQUERY in RETURNING")]
    [InlineData("INSERT INTO t (id) SELECT id FROM u RETURNING (SELECT 1)", "SUBQUERY in RETURNING")]
    [InlineData("INSERT INTO t WITH c AS (SELECT id FROM u) SELECT id FROM c RETURNING (SELECT 1)", "SUBQUERY in RETURNING")]
    [InlineData("WITH c AS (SELECT id FROM u) INSERT INTO t SELECT id FROM c RETURNING (SELECT 1)", "SUBQUERY in RETURNING")]
    [InlineData("DELETE FROM t WHERE id = 1 RETURNING (SELECT 1)", "SUBQUERY in RETURNING")]
    [InlineData("UPDATE t SET age = 1 RETURNING ROW_NUMBER() OVER ()", "OVER")]
    [InlineData("WITH d AS (DELETE FROM t RETURNING *) SELECT * FROM d", "DELETE")]
    public void Parse_ExcludedReturning_ShouldNameConstruct(string sql, string construct)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse(sql);

        // Assert
        var diagnostic = statement.Diagnostics.Where(item => item.Code == "CK00001").ShouldHaveSingleItem();
        diagnostic.Message!.ShouldContain(construct, Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - RETURNING: incomplete lists and SELECT tails fail syntax")]
    [InlineData("DELETE FROM t RETURNING")]
    [InlineData("DELETE FROM t RETURNING;")]
    [InlineData("DELETE FROM t RETURNING id,;")]
    [InlineData("INSERT INTO t VALUES (1) RETURNING")]
    [InlineData("UPDATE t SET id = 1 RETURNING id,")]
    [InlineData("SELECT id FROM t RETURNING id;")]
    public void Parse_InvalidReturning_ShouldReportSyntax(string sql)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse(sql);

        // Assert
        statement.Diagnostics.ShouldContain(item => item.Code == "CS00003" && item.Severity == DiagnosticSeverity.Error);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - RETURNING: contextual names preserve ordinary identifiers")]
    [InlineData("UPDATE t AS old SET age = 1 RETURNING old.age;")]
    [InlineData("UPDATE t output SET age = 1;")]
    [InlineData("UPDATE t output SET age = 1 RETURNING output.age;")]
    [InlineData("UPDATE t AS new SET age = 1 RETURNING new.age;")]
    [InlineData("DELETE FROM old RETURNING old.age;")]
    [InlineData("DELETE FROM t AS output RETURNING output.id;")]
    [InlineData("UPDATE t SET age = 1 RETURNING output, old, new;")]
    [InlineData("UPDATE old.t SET age = 1 RETURNING old.t.age;")]
    [InlineData("UPDATE new.t SET age = 1 RETURNING new.t.age;")]
    public void Parse_ContextualIdentifiers_ShouldRemainUsable(string sql)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse(sql);

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
    }
}
