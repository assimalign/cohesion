using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public class SqlInsertParserTests
{
    private readonly SqlQueryParser _parser = new();

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Upsert: malformed inference lists and missing actions report syntax")]
    [InlineData("ON CONFLICT () DO NOTHING")]
    [InlineData("ON CONFLICT (id,, a) DO NOTHING")]
    [InlineData("ON CONFLICT (id,) DO NOTHING")]
    [InlineData("ON CONFLICT (id")]
    [InlineData("ON CONFLICT (id)")]
    [InlineData("ON CONFLICT (id) DO UPDATE SET")]
    public void Parse_IncompleteOnConflict_ShouldReportSyntax(string clause)
    {
        // Arrange / Act
        var statement = _parser.Parse("INSERT INTO t VALUES (1) " + clause + ";");

        // Assert
        statement.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "SQL0003");
        statement.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Code == "COHDBL001");
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Upsert: all conflict targets consume their complete statement and RETURNING")]
    [InlineData("INSERT INTO t (id) VALUES (1) ON CONFLICT DO NOTHING", SqlOnConflictTargetKind.None, SqlOnConflictAction.DoNothing)]
    [InlineData("INSERT INTO t (a, b) VALUES (1, 2) ON CONFLICT (a, b) DO NOTHING", SqlOnConflictTargetKind.Columns, SqlOnConflictAction.DoNothing)]
    [InlineData("INSERT INTO t (id) VALUES (1) ON CONFLICT ON CONSTRAINT uq_email DO UPDATE SET n = EXCLUDED.n", SqlOnConflictTargetKind.Constraint, SqlOnConflictAction.DoUpdate)]
    [InlineData("INSERT INTO t AS x (id) VALUES (1) ON CONFLICT (id) DO UPDATE SET n = x.n + EXCLUDED.n WHERE x.v < EXCLUDED.v", SqlOnConflictTargetKind.Columns, SqlOnConflictAction.DoUpdate)]
    [InlineData("INSERT INTO t (id) SELECT u.id FROM u JOIN w ON u.id = w.id ON CONFLICT DO NOTHING", SqlOnConflictTargetKind.None, SqlOnConflictAction.DoNothing)]
    [InlineData("WITH c AS (SELECT 1 AS id) INSERT INTO t SELECT id FROM c ON CONFLICT (id) DO UPDATE SET a = 1, b = 2", SqlOnConflictTargetKind.Columns, SqlOnConflictAction.DoUpdate)]
    public void Parse_OnConflict_ShouldRetainTargetAndAction(string sql, SqlOnConflictTargetKind target, SqlOnConflictAction action)
    {
        // Arrange / Act
        foreach (string tail in new[] { ";", " RETURNING id;" })
        {
            var statement = _parser.Parse(sql + tail).ShouldBeOfType<SqlQueryStatement>();

            // Assert
            statement.Diagnostics.ShouldBeEmpty();
            var insert = statement.SqlExpression.ShouldBeOfType<SqlInsertExpression>();
            var conflict = insert.OnConflict.ShouldNotBeNull();
            conflict.TargetKind.ShouldBe(target);
            conflict.Action.ShouldBe(action);
            conflict.Excluded.Alias.ShouldBe("EXCLUDED");
            if (target == SqlOnConflictTargetKind.Constraint) { conflict.ConstraintName.ShouldBe("uq_email"); }
            if (target == SqlOnConflictTargetKind.Columns) { conflict.Columns.ShouldNotBeEmpty(); }
            if (action == SqlOnConflictAction.DoUpdate) { conflict.Assignments.ShouldNotBeEmpty(); }
            if (sql.Contains("WHERE")) { conflict.Where.ShouldNotBeNull(); }
            (insert.Returning is not null).ShouldBe(tail.Contains("RETURNING"));
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Upsert: excluded spellings report one named capability refusal")]
    [InlineData("INSERT INTO t VALUES (1) ON DUPLICATE KEY UPDATE a = 3", "ON DUPLICATE KEY UPDATE")]
    [InlineData("INSERT OR IGNORE INTO t VALUES (1)", "INSERT OR IGNORE")]
    [InlineData("INSERT OR REPLACE INTO t VALUES (1)", "INSERT OR REPLACE")]
    [InlineData("INSERT INTO t VALUES (1) ON CONFLICT (lower(email)) DO NOTHING", "ON CONFLICT inference")]
    [InlineData("INSERT INTO t VALUES (1) ON CONFLICT (id) WHERE id > 0 DO NOTHING", "ON CONFLICT target WHERE")]
    [InlineData("INSERT INTO t VALUES (1) ON CONFLICT (c COLLATE x) DO NOTHING", "ON CONFLICT inference")]
    [InlineData("INSERT INTO t VALUES (1) ON CONFLICT (id DESC) DO NOTHING", "ON CONFLICT inference")]
    [InlineData("INSERT INTO t VALUES (1) ON CONFLICT (id NULLS FIRST) DO NOTHING", "ON CONFLICT inference")]
    [InlineData("INSERT INTO t VALUES (1) ON CONFLICT DO SELECT", "ON CONFLICT DO SELECT")]
    [InlineData("INSERT INTO t VALUES (1) ON CONFLICT (id) DO UPDATE SET a = (SELECT 1)", "SUBQUERY in ON CONFLICT DO UPDATE")]
    [InlineData("INSERT INTO t SELECT id FROM u ON CONFLICT (id) DO UPDATE SET a = (SELECT 1)", "SUBQUERY in ON CONFLICT DO UPDATE")]
    [InlineData("WITH c AS (SELECT id FROM u) INSERT INTO t SELECT id FROM c ON CONFLICT (id) DO UPDATE SET a = (SELECT 1)", "SUBQUERY in ON CONFLICT DO UPDATE")]
    [InlineData("INSERT INTO t VALUES (1) ON CONFLICT (id) DO UPDATE SET a = 1 WHERE EXISTS (SELECT 1)", "SUBQUERY in ON CONFLICT DO UPDATE")]
    [InlineData("INSERT INTO t SELECT id FROM u ON CONFLICT (id) DO UPDATE SET a = 1 WHERE EXISTS (SELECT 1)", "SUBQUERY in ON CONFLICT DO UPDATE")]
    [InlineData("INSERT INTO t VALUES (1) ON CONFLICT DO NOTHING RETURNING EXCLUDED.id", "EXCLUDED in RETURNING")]
    public void Parse_ExcludedOnConflict_ShouldNameConstruct(string sql, string construct)
    {
        // Arrange / Act
        var statement = _parser.Parse(sql + ";");

        // Assert
        statement.Diagnostics.Where(item => item.Code == "COHDBL001").ShouldHaveSingleItem().Message!.ShouldContain(construct, Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Upsert: contextual words remain ordinary identifiers")]
    [InlineData("INSERT INTO conflict (do, nothing, excluded) VALUES (1, 2, 3)")]
    [InlineData("SELECT conflict.do, nothing, excluded FROM conflict")]
    [InlineData("UPDATE excluded SET nothing = 2 WHERE do = 1")]
    [InlineData("INSERT INTO duplicate (ignore, replace) VALUES (1, 2)")]
    public void Parse_ContextualUpsertWords_ShouldPreserveIdentifiers(string sql)
    {
        // Arrange / Act
        var statement = _parser.Parse(sql + ";");

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_InsertWithValues_ParsesTableAndValues()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "INSERT INTO Users (Id, Name) VALUES (1, 'Alice');");
        var insert = statement.SqlExpression.ShouldBeOfType<SqlInsertExpression>();

        insert.CommandType.ShouldBe(SqlQueryCommandType.Insert);
        insert.Table.TableName.ShouldBe("Users");
        insert.Columns.ShouldNotBeNull();
        insert.Columns.Count.ShouldBe(2);
        insert.Columns![0].ShouldBe("Id");
        insert.Columns[1].ShouldBe("Name");

        insert.Values.ShouldNotBeNull();
        insert.Values.Count.ShouldBe(1);
        insert.Values![0].Count.ShouldBe(2);

        var val0 = insert.Values[0][0].ShouldBeOfType<SqlLiteralExpression>();
        val0.Value.ShouldBe("1");

        var val1 = insert.Values[0][1].ShouldBeOfType<SqlLiteralExpression>();
        val1.LiteralType.ShouldBe(SqlLiteralType.String);
    }

    [Fact]
    public void Parse_InsertMultipleRows_ParsesAllRows()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "INSERT INTO Users (Id, Name) VALUES (1, 'Alice'), (2, 'Bob');");
        var insert = statement.SqlExpression.ShouldBeOfType<SqlInsertExpression>();

        insert.Values!.Count.ShouldBe(2);
        insert.Values![0].Count.ShouldBe(2);
        insert.Values[1].Count.ShouldBe(2);
    }

    [Fact]
    public void Parse_InsertSelect_ParsesSelectSource()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "INSERT INTO Users (Id, Name) SELECT Id, Name FROM OldUsers;");
        var insert = statement.SqlExpression.ShouldBeOfType<SqlInsertExpression>();

        insert.SelectSource.ShouldNotBeNull();
        insert.Values.ShouldBeNull();
        insert.SelectSource.ShouldBeOfType<SqlSelectExpression>().Columns.Count.ShouldBe(2);
    }

    [Fact]
    public void Parse_InsertWithSchemaQualifiedTable_ParsesSchemaAndTable()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "INSERT INTO dbo.Users (Id) VALUES (1);");
        var insert = statement.SqlExpression.ShouldBeOfType<SqlInsertExpression>();

        insert.Table.SchemaName.ShouldBe("dbo");
        insert.Table.TableName.ShouldBe("Users");
    }

    [Fact]
    public void Parse_InsertWithoutColumnList_HasNullColumns()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "INSERT INTO Users VALUES (1, 'Alice');");
        var insert = statement.SqlExpression.ShouldBeOfType<SqlInsertExpression>();

        insert.Columns.ShouldBeNull();
        insert.Values!.Count.ShouldBe(1);
    }
}
