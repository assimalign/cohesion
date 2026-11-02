using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlCopyToTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - COPY: supported options round trip with their defaults")]
    [InlineData("COPY t TO STDOUT;")]
    [InlineData("COPY s.t (b,a) TO STDOUT (FORMAT csv, HEADER, FORCE_QUOTE (a));")]
    [InlineData("COPY t TO STDOUT WITH (FORMAT 'csv', DELIMITER '|', NULL 'nil', QUOTE '~', ESCAPE '!', FORCE_QUOTE *, ENCODING 'UTF-8', HEADER false);")]
    [InlineData("COPY t TO STDOUT WITH (HEADER, FORMAT text);")]
    [InlineData("COPY t TO STDOUT WITH (FORMAT \"csv\", DELIMITER 1, NULL missing, ENCODING UTF8, QUOTE q, ESCAPE e);")]
    [InlineData("COPY t TO STDOUT WITH (FORMAT CSV, HEADER +01, NULL -2);")]
    [InlineData("COPY t TO STDOUT WITH (FORMAT csv, HEADER ' +01 ', DELIMITER +1, NULL MiSsInG);")]
    [InlineData("COPY t TO STDOUT WITH (ENCODING 'UNICODE');")]
    [InlineData("COPY t TO STDOUT WITH (ENCODING 'uTf_8');")]
    [InlineData("COPY \"odd table\" (\"a quote\") TO STDOUT (FORMAT csv, QUOTE '''', ESCAPE '''', HEADER 'on');")]
    public void Parse_Options_ShouldRoundTrip(string sql)
    {
        // Arrange / Act
        var parsed = (SqlQueryStatement)new SqlQueryParser().Parse(sql);
        // Assert
        parsed.Diagnostics.ShouldNotContain(d => d.Severity == DiagnosticSeverity.Error);
        var copy = parsed.SqlExpression.ShouldBeOfType<SqlCopyToExpression>();
        string rendered = SqlExpressionRenderer.Render(copy);
        var reparsed = (SqlQueryStatement)new SqlQueryParser().Parse(rendered);
        reparsed.Diagnostics.ShouldNotContain(d => d.Severity == DiagnosticSeverity.Error);
        SqlExpressionRenderer.Render(reparsed.SqlExpression.ShouldBeOfType<SqlCopyToExpression>()).ShouldBe(rendered);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - COPY: defaults match PostgreSQL text and CSV")]
    public void Parse_Defaults_ShouldResolveTextAndCsv()
    {
        // Arrange / Act
        var text = ((SqlQueryStatement)new SqlQueryParser().Parse("COPY t TO STDOUT")).SqlExpression.ShouldBeOfType<SqlCopyToExpression>().Options;
        var csv = ((SqlQueryStatement)new SqlQueryParser().Parse("COPY t TO STDOUT (FORMAT csv)")).SqlExpression.ShouldBeOfType<SqlCopyToExpression>().Options;
        // Assert
        text.Delimiter.ShouldBe('\t'); text.NullString.ShouldBe("\\N"); text.Header.ShouldBeFalse();
        csv.Delimiter.ShouldBe(','); csv.NullString.ShouldBe(""); csv.Quote.ShouldBe('"'); csv.Escape.ShouldBe('"');
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - COPY: malformed or incompatible options refuse with codes")]
    [InlineData("WITH ()", "CS00003")]
    [InlineData("WITH (FORMAT)", "CS00003")]
    [InlineData("WITH (FORMAT xml)", "CS01070")]
    [InlineData("WITH (FORMAT 'CSV')", "CS01070")]
    [InlineData("WITH (\"FORMAT\" csv)", "CS01070")]
    [InlineData("WITH (FORMAT csv, FORMAT text)", "CS01070")]
    [InlineData("WITH (DELIMITER '')", "CS01070")]
    [InlineData("WITH (DELIMITER 'xx')", "CS01070")]
    [InlineData("WITH (DELIMITER 'é')", "CS01070")]
    [InlineData("WITH (DELIMITER 'a')", "CS01070")]
    [InlineData("WITH (DELIMITER '\\')", "CS01070")]
    [InlineData("WITH (DELIMITER '1')", "CS01070")]
    [InlineData("WITH (NULL 'a\tb')", "CS01070")]
    [InlineData("WITH (NULL 'a\nb')", "CS01070")]
    [InlineData("WITH (QUOTE '~')", "CS01070")]
    [InlineData("WITH (ESCAPE '~')", "CS01070")]
    [InlineData("WITH (FORCE_QUOTE *)", "CS01070")]
    [InlineData("WITH (FORMAT csv, DELIMITER '~', QUOTE '~')", "CS01070")]
    [InlineData("WITH (FORMAT csv, NULL 'a\"b')", "CS01070")]
    [InlineData("WITH (FORMAT csv, QUOTE '')", "CS01070")]
    [InlineData("WITH (FORMAT csv, ESCAPE 'xx')", "CS01070")]
    [InlineData("WITH (ENCODING 'LATIN1')", "CS01070")]
    [InlineData("WITH (HEADER MATCH)", "CS01070")]
    [InlineData("WITH (HEADER 2)", "CS01070")]
    [InlineData("WITH (FORMAT csv, FORCE_QUOTE ())", "CS00003")]
    [InlineData("WITH (FORMAT csv,)", "CS00003")]
    [InlineData("CSV HEADER", "CS00003")]
    [InlineData("WITH (FREEZE)", "CS01070")]
    public void Parse_InvalidOptions_ShouldRefuse(string options, string code)
    {
        // Arrange / Act
        var parsed = new SqlQueryParser().Parse("COPY t TO STDOUT " + options);
        // Assert
        parsed.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ShouldContain(d => d.Code == code);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - COPY: unsupported directions and destinations are explicit")]
    [InlineData("COPY t TO 'server.txt'")]
    [InlineData("COPY t TO PROGRAM 'cat'")]
    [InlineData("COPY t TO STDOUT (FORMAT binary)")]
    [InlineData("COPY (SELECT * FROM t) TO STDOUT")]
    [InlineData("COPY t FROM STDIN")]
    public void Parse_UnsupportedSurface_ShouldRefuse(string sql)
    {
        // Arrange / Act
        var parsed = new SqlQueryParser().Parse(sql);
        // Assert
        parsed.Diagnostics.ShouldContain(d => d.Code == "CK00001" && d.Severity == DiagnosticSeverity.Error);
    }
}
