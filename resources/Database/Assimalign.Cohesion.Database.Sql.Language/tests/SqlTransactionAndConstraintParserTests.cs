using System.Linq;

using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlTransactionAndConstraintParserTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - DEFAULT: a negative numeric value parses through the unary expression rule")]
    [InlineData("CREATE TABLE t (amount DECIMAL(5, 2) DEFAULT -1.25);", "-1.25")]
    [InlineData("CREATE TABLE t (amount BIGINT DEFAULT -9223372036854775808);", "-9223372036854775808")]
    [InlineData("ALTER TABLE t ADD COLUMN amount DOUBLE DEFAULT -1.5e30;", "-1.5e30")]
    public void Default_WithNegativeNumericLiteral_ShouldRetainSign(string sql, string expected)
    {
        // Arrange
        var parser = new SqlQueryParser();

        // Act
        var statement = parser.Parse(sql).ShouldBeOfType<SqlQueryStatement>();

        // Assert
        statement.Diagnostics.ShouldBeEmpty(string.Join("; ", statement.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        SqlExpression? expression = statement.SqlExpression switch
        {
            SqlCreateTableExpression create => create.Columns.Single().DefaultValue,
            SqlAlterTableExpression alter => alter.Action.ShouldBeOfType<SqlAlterAddColumnAction>().Column.DefaultValue,
            _ => null,
        };
        expression.ShouldNotBeNull();
        expression.ShouldBeOfType<SqlUnaryExpression>().Operator.ShouldBe(SqlUnaryOperator.Negate);
        SqlExpressionRenderer.Render(expression).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Transactions: spellings accept case, trivia and optional terminators")]
    [InlineData("START TRANSACTION", SqlQueryCommandType.Begin)]
    [InlineData("BEGIN", SqlQueryCommandType.Begin)]
    [InlineData("BEGIN WORK", SqlQueryCommandType.Begin)]
    [InlineData("BEGIN TRANSACTION", SqlQueryCommandType.Begin)]
    [InlineData("COMMIT", SqlQueryCommandType.Commit)]
    [InlineData("COMMIT WORK", SqlQueryCommandType.Commit)]
    [InlineData("COMMIT TRANSACTION", SqlQueryCommandType.Commit)]
    [InlineData("END", SqlQueryCommandType.Commit)]
    [InlineData("ROLLBACK", SqlQueryCommandType.Rollback)]
    [InlineData("ROLLBACK WORK", SqlQueryCommandType.Rollback)]
    [InlineData("ROLLBACK TRANSACTION", SqlQueryCommandType.Rollback)]
    public void Parse_TransactionSpelling_ShouldRetainCommand(string sql, SqlQueryCommandType command)
    {
        // Arrange
        string mixedCase = string.Concat(sql.Select((character, index) => index % 2 == 0
            ? char.ToLowerInvariant(character) : char.ToUpperInvariant(character)));
        string[] forms = [sql, sql.ToLowerInvariant(), mixedCase,
            mixedCase.Replace(" ", " /* between */ "), mixedCase.Replace(" ", " -- between\r\n ")];

        // Act / Assert
        foreach (string form in forms)
        {
            foreach (string terminator in new[] { "", ";" })
            {
                var statement = Parse(form + terminator);
                statement.SqlExpression.ShouldBeOfType<SqlTransactionExpression>().CommandType.ShouldBe(command);
                if (terminator.Length == 0)
                {
                    // SQL0100 uniformly reports an omitted terminator for every SQL statement.
                    statement.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error || diagnostic.Severity == DiagnosticSeverity.Warning);
                    var diagnostic = statement.Diagnostics.ShouldHaveSingleItem();
                    diagnostic.Code.ShouldBe("SQL0100");
                    diagnostic.Severity.ShouldBe(DiagnosticSeverity.Information);
                }
                else
                {
                    statement.Diagnostics.ShouldBeEmpty();
                }
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Transactions: malformed and excluded forms retain their diagnostic code")]
    [InlineData("START", "SQL0003")]
    [InlineData("START WORK", "SQL0003")]
    [InlineData("BEGIN SAVEPOINT marker", "SQL0003")]
    [InlineData("ROLLBACK TO marker", "SQL0003")]
    [InlineData("BEGIN WORK TRANSACTION", "SQL0003")]
    [InlineData("COMMIT WORK WORK", "SQL0003")]
    [InlineData("END WORK", "SQL0003")]
    [InlineData("END TRANSACTION", "SQL0003")]
    [InlineData("BEGIN TRAN", "SQL0003")]
    [InlineData("BEGIN TRANSACTION named", "SQL0003")]
    [InlineData("END IF", "SQL0003")]
    [InlineData("COMMIT PREPARED 'x'", "SQL0003")]
    [InlineData("START TRANSACTION ISOLATION LEVEL READ COMMITTED", "SQL0003")]
    [InlineData("START TRANSACTION READ ONLY", "SQL0003")]
    [InlineData("BEGIN WORK ISOLATION LEVEL SERIALIZABLE", "SQL0003")]
    [InlineData("ROLLBACK WORK TO SAVEPOINT a", "SQL0003")]
    [InlineData("COMMIT AND NO CHAIN", "SQL0003")]
    [InlineData("COMMIT AND CHAIN", "SQL0003")]
    [InlineData("ROLLBACK AND NO CHAIN", "SQL0003")]
    [InlineData("END AND NO CHAIN", "SQL0003")]
    [InlineData("ABORT", "SQL0002")]
    [InlineData("ABORT WORK", "SQL0002")]
    [InlineData("ABORT TRANSACTION", "SQL0002")]
    [InlineData("SAVEPOINT a", "SQL0002")]
    [InlineData("RELEASE SAVEPOINT a", "SQL0002")]
    [InlineData("SET TRANSACTION ISOLATION LEVEL READ COMMITTED", "SQL0002")]
    [InlineData("PREPARE TRANSACTION 'x'", "SQL0002")]
    [InlineData("\"START\" TRANSACTION", "SQL0002")]
    [InlineData("START \"TRANSACTION\"", "SQL0003")]
    [InlineData("BEGIN \"WORK\"", "SQL0003")]
    public void Parse_UnsupportedTransactionForm_ShouldReportExpectedCode(string sql, string code)
    {
        // Act
        var statement = Parse(sql + ";");

        // Assert
        statement.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Transactions: contextual words remain identifiers and CASE END is unchanged")]
    [InlineData("CREATE TABLE shifts (start INT, work TEXT, abort BOOLEAN);")]
    [InlineData("INSERT INTO shifts (start, work, abort) VALUES (1, 'day', FALSE);")]
    [InlineData("SELECT start, work, abort FROM shifts;")]
    [InlineData("SELECT CASE WHEN start = 1 THEN work ELSE 'other' END FROM shifts;")]
    public void Parse_TransactionWordsInOtherContexts_ShouldReportNoDiagnostics(string sql)
    {
        // Act
        var statement = Parse(sql);

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void TransactionKeyword_RequiresAControlCommand() =>
        Parse("TRANSACTION;").Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "SQL0002");

    [Fact]
    public void CreateTable_NormalizesNamedAndUnnamedColumnAndTableConstraints()
    {
        const string sql = "CREATE TABLE t (id INT PRIMARY KEY, parent_id INT REFERENCES parent(id) ON DELETE CASCADE, email TEXT UNIQUE, qty INT CHECK (qty > 0), CONSTRAINT fk_parent FOREIGN KEY (parent_id) REFERENCES parent(id) ON DELETE RESTRICT);";
        var statement = Parse(sql);
        statement.Diagnostics.ShouldBeEmpty();
        var create = statement.SqlExpression.ShouldBeOfType<SqlCreateTableExpression>();
        create.Columns.Count.ShouldBe(4);
        create.Constraints.Count.ShouldBe(5);
        create.Constraints[0].Kind.ShouldBe(SqlConstraintKind.PrimaryKey);
        var reference = create.Constraints[1];
        reference.Columns.ShouldBe(["parent_id"]);
        reference.ReferencedTable!.TableName.ShouldBe("parent");
        reference.ReferencedColumns.ShouldBe(["id"]);
        reference.OnDelete.ShouldBe(SqlReferentialAction.Cascade);
        create.Constraints[2].Kind.ShouldBe(SqlConstraintKind.Unique);
        create.Constraints[3].CheckExpression.ShouldBeOfType<SqlBinaryExpression>();
        create.Constraints[3].CheckExpressionText.ShouldBe("qty > 0");
        create.Constraints[4].Name.ShouldBe("fk_parent");
        create.Constraints[4].OnDelete.ShouldBe(SqlReferentialAction.Restrict);
    }

    [Fact]
    public void CreateTable_CompositeConstraints_PreserveColumnOrderAndPredicateSource()
    {
        var statement = Parse("CREATE TABLE child (tenant INT, id INT, qty INT, CONSTRAINT pk PRIMARY KEY (tenant, id), CONSTRAINT uq UNIQUE (id, tenant), CONSTRAINT fk FOREIGN KEY (tenant, id) REFERENCES dbo.parent(tenant, id), CONSTRAINT ck CHECK ((qty + 1) > 0 AND qty < 100));");
        statement.Diagnostics.ShouldBeEmpty();
        var constraints = statement.SqlExpression.ShouldBeOfType<SqlCreateTableExpression>().Constraints;
        constraints[0].Columns.ShouldBe(["tenant", "id"]);
        constraints[1].Columns.ShouldBe(["id", "tenant"]);
        constraints[2].ReferencedTable!.SchemaName.ShouldBe("dbo");
        constraints[2].OnDelete.ShouldBe(SqlReferentialAction.NoAction);
        constraints[3].CheckExpressionText.ShouldBe("(qty + 1) > 0 AND qty < 100");
    }

    [Theory]
    [InlineData("ALTER TABLE t ADD CONSTRAINT fk FOREIGN KEY (parent_id) REFERENCES parent(id) ON DELETE CASCADE;", SqlConstraintKind.ForeignKey)]
    [InlineData("ALTER TABLE t ADD UNIQUE (email);", SqlConstraintKind.Unique)]
    [InlineData("ALTER TABLE t ADD CHECK (qty > 0);", SqlConstraintKind.Check)]
    public void AlterTable_AddConstraint_Parses(string sql, SqlConstraintKind kind)
    {
        var statement = Parse(sql);
        statement.Diagnostics.ShouldBeEmpty();
        statement.SqlExpression.ShouldBeOfType<SqlAlterTableExpression>().Action
            .ShouldBeOfType<SqlAlterAddConstraintAction>().Constraint.Kind.ShouldBe(kind);
    }

    [Fact]
    public void AlterTable_AddColumn_PreservesNamedColumnConstraint()
    {
        var statement = Parse("ALTER TABLE t ADD COLUMN parent_id INT CONSTRAINT fk REFERENCES parent(id);");
        statement.Diagnostics.ShouldBeEmpty();
        var column = statement.SqlExpression.ShouldBeOfType<SqlAlterTableExpression>().Action
            .ShouldBeOfType<SqlAlterAddColumnAction>().Column;
        column.Constraints.Single().Name.ShouldBe("fk");
        column.Constraints.Single().Columns.ShouldBe(["parent_id"]);
    }

    [Fact]
    public void AlterTable_DropConstraint_PreservesName()
    {
        var statement = Parse("ALTER TABLE t DROP CONSTRAINT fk;");
        statement.Diagnostics.ShouldBeEmpty();
        statement.SqlExpression.ShouldBeOfType<SqlAlterTableExpression>().Action
            .ShouldBeOfType<SqlAlterDropConstraintAction>().ConstraintName.ShouldBe("fk");
    }

    [Theory]
    [InlineData("CREATE TABLE t (parent_id INT REFERENCES parent(id) ON UPDATE CASCADE);")]
    [InlineData("CREATE TABLE t (parent_id INT REFERENCES parent(id) ON DELETE CASCADE ON UPDATE RESTRICT);")]
    [InlineData("ALTER TABLE t ADD CONSTRAINT fk FOREIGN KEY (parent_id) REFERENCES parent(id) ON UPDATE RESTRICT;")]
    public void OnUpdate_RemainsUnsupported(string sql)
    {
        SqlLanguageProfile.Instance.Supports("ON UPDATE").ShouldBeFalse();
        Parse(sql).Diagnostics.Single(diagnostic => diagnostic.Code == "CK00001")
            .Message!.ShouldContain("ON UPDATE");
    }

    [Theory]
    [InlineData("CREATE TABLE t (parent_id INT REFERENCES parent());")]
    [InlineData("CREATE TABLE t (id INT, FOREIGN KEY (id) REFERENCES parent(a, b));")]
    [InlineData("CREATE TABLE t (id INT, CONSTRAINT ck CHECK (id > 0);")]
    [InlineData("ALTER TABLE t ADD UNIQUE ();")]
    [InlineData("CREATE TABLE t (id INT CHECK ());")]
    [InlineData("DROP TABLE t CASCADE;")]
    public void MalformedConstraint_ReturnsAnErrorInsteadOfThrowing(string sql)
    {
        var statement = Should.NotThrow(() => Parse(sql));
        statement.Diagnostics.ShouldContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    private static SqlQueryStatement Parse(string sql) => (SqlQueryStatement)new SqlQueryParser().Parse(sql);
}
