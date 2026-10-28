using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlSequenceParserTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Sequences: every option round trips canonically")]
    [InlineData("CREATE SEQUENCE s;")]
    [InlineData("CREATE SEQUENCE IF NOT EXISTS dbo.s AS SMALLINT START 3 INCREMENT 2 MINVALUE -5 MAXVALUE 100 CACHE 9 CYCLE;")]
    [InlineData("CREATE SEQUENCE s AS INTEGER NO MINVALUE NO MAXVALUE NO CYCLE;")]
    [InlineData("ALTER SEQUENCE IF EXISTS s AS BIGINT START WITH -1 INCREMENT BY -2 NO MINVALUE NO MAXVALUE CACHE 1 CYCLE RESTART;")]
    [InlineData("ALTER SEQUENCE s RESTART WITH -9223372036854775808;")]
    [InlineData("DROP SEQUENCE IF EXISTS s, dbo.t CASCADE;")]
    [InlineData("DROP SEQUENCE s RESTRICT;")]
    [InlineData("SELECT NEXT VALUE FOR dbo.s, nextval('dbo.s'), currval('dbo.s'), setval('dbo.s', 3, false), lastval();")]
    [InlineData("SELECT nextval('s'::regclass);")]
    public void Parse_SequenceSurface_ShouldRoundTrip(string sql)
    {
        // Arrange / Act
        var first = new SqlQueryParser().Parse(sql).ShouldBeOfType<SqlQueryStatement>();
        first.Diagnostics.ShouldBeEmpty();
        string Render(SqlQueryExpression expression) => expression switch
        {
            SqlCreateSequenceExpression create => SqlExpressionRenderer.Render(create),
            SqlAlterSequenceExpression alter => SqlExpressionRenderer.Render(alter),
            SqlDropSequenceExpression drop => SqlExpressionRenderer.Render(drop),
            SqlQueryBodyExpression query => SqlExpressionRenderer.Render(query),
            _ => throw new System.InvalidOperationException(),
        };
        string rendered = Render(first.SqlExpression);
        var second = new SqlQueryParser().Parse(rendered + ";").ShouldBeOfType<SqlQueryStatement>();

        // Assert
        second.Diagnostics.ShouldBeEmpty();
        Render(second.SqlExpression).ShouldBe(rendered);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Sequences: malformed definitions refuse total parsing")]
    [InlineData("CREATE SEQUENCE")][InlineData("CREATE SEQUENCE s START")]
    [InlineData("CREATE SEQUENCE s START 1 START 2")][InlineData("CREATE SEQUENCE s MINVALUE 1 NO MINVALUE")]
    [InlineData("CREATE SEQUENCE s RESTART")][InlineData("CREATE SEQUENCE s CACHE 1.5")]
    [InlineData("CREATE SEQUENCE s START 9223372036854775808")][InlineData("ALTER SEQUENCE s RESTART WITH")]
    [InlineData("CREATE SEQUENCE s NO CACHE")][InlineData("DROP SEQUENCE s,")]
    [InlineData("SELECT NEXT VALUE s")][InlineData("CREATE SEQUENCE s OWNED BY t.id")]
    public void Parse_MalformedSequence_ShouldRefuse(string sql)
    {
        // Arrange / Act / Assert
        new SqlQueryParser().Parse(sql).Diagnostics.ShouldContain(d => d.Severity == DiagnosticSeverity.Error);
    }
}
