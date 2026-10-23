using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlTypedLiteralTests
{
    public static IEnumerable<object[]> Literals() =>
    [
        ["DATE '2024-01-01'", SqlLiteralType.Date, "2024-01-01"],
        ["TIME '1:02'", SqlLiteralType.Time, "01:02:00"],
        ["TIME '1 PM'", SqlLiteralType.Time, "13:00:00"],
        ["TIME '13:45:00.'", SqlLiteralType.Time, "13:45:00"],
        ["TIME '10:00Z'", SqlLiteralType.Time, "10:00:00"],
        ["TIME '10:00:00+01:00'", SqlLiteralType.Time, "10:00:00"],
        ["INTERVAL '1 2 hours'", SqlLiteralType.Interval, "PT93600S"],
        ["INTERVAL '@ 1 day 2 hours ago'", SqlLiteralType.Interval, "-PT93600S"],
        ["TIME '12:30'", SqlLiteralType.Time, "12:30:00"],
        ["TIME WITHOUT TIME ZONE '12:30:01.1234567'", SqlLiteralType.Time, "12:30:01.1234567"],
        ["TIMESTAMP '2024-01-01 10:00:00'", SqlLiteralType.Timestamp, "2024-01-01 10:00:00"],
        ["TIMESTAMP WITHOUT TIME ZONE '2024-01-01T10:00'", SqlLiteralType.Timestamp, "2024-01-01 10:00:00"],
        ["TIMESTAMP WITH TIME ZONE '2024-01-01 12:00:00+02:00'", SqlLiteralType.TimestampWithTimeZone, "2024-01-01 12:00:00.0000000+02:00"],
        ["TIMESTAMPTZ '2024-01-01 10:00:00'", SqlLiteralType.TimestampWithTimeZone, "2024-01-01 10:00:00.0000000+00:00"],
        ["INTERVAL '2' HOUR", SqlLiteralType.Interval, "PT7200S"],
        ["INTERVAL '3' MINUTE", SqlLiteralType.Interval, "PT180S"],
        ["INTERVAL '12'", SqlLiteralType.Interval, "PT12S"],
        ["INTERVAL '-12.0000001'", SqlLiteralType.Interval, "-PT12.0000001S"],
        ["INTERVAL '+12.5'", SqlLiteralType.Interval, "PT12.5S"],
        ["INTERVAL '0'", SqlLiteralType.Interval, "PT0S"],
        ["INTERVAL '0.0000001' SECOND", SqlLiteralType.Interval, "PT0.0000001S"],
        ["INTERVAL '1 02' DAY TO HOUR", SqlLiteralType.Interval, "PT93600S"],
        ["INTERVAL '1 02:30' DAY TO MINUTE", SqlLiteralType.Interval, "PT95400S"],
        ["INTERVAL '26:30:04.5' HOUR TO SECOND", SqlLiteralType.Interval, "PT95404.5S"],
        ["INTERVAL '90:04.5' MINUTE TO SECOND", SqlLiteralType.Interval, "PT5404.5S"],
        ["INTERVAL '-2 days +3 hours'", SqlLiteralType.Interval, "-PT162000S"],
        ["INTERVAL 'PT922337203685.4775807S'", SqlLiteralType.Interval, "PT922337203685.4775807S"],
        ["INTERVAL '3' DAY", SqlLiteralType.Interval, "PT259200S"],
        ["INTERVAL '1:30' HOUR TO MINUTE", SqlLiteralType.Interval, "PT5400S"],
        ["INTERVAL '2 days 3 hours'", SqlLiteralType.Interval, "PT183600S"],
        ["INTERVAL '1 day 02:03:04'", SqlLiteralType.Interval, "PT93784S"],
        ["INTERVAL '1 02:03:04'", SqlLiteralType.Interval, "PT93784S"],
        ["INTERVAL '02:03:04'", SqlLiteralType.Interval, "PT7384S"],
        ["INTERVAL '-1 day -02:03:04'", SqlLiteralType.Interval, "-PT93784S"],
        ["INTERVAL '-1 02:03:04'", SqlLiteralType.Interval, "-PT79016S"],
        ["INTERVAL '1 +02:03:04.1234567'", SqlLiteralType.Interval, "PT93784.1234567S"],
        ["INTERVAL '2:03'", SqlLiteralType.Interval, "PT7380S"],
        ["INTERVAL '2:03.5'", SqlLiteralType.Interval, "PT123.5S"],
        ["INTERVAL '02:03:.5'", SqlLiteralType.Interval, "PT7380.5S"],
        ["INTERVAL '02:03:04.'", SqlLiteralType.Interval, "PT7384S"],
        ["INTERVAL '02::04'", SqlLiteralType.Interval, "PT7204S"],
        ["INTERVAL '02:03:'", SqlLiteralType.Interval, "PT7380S"],
        ["INTERVAL '02:'", SqlLiteralType.Interval, "PT7200S"],
        ["INTERVAL '25:03:04'", SqlLiteralType.Interval, "PT90184S"],
        ["INTERVAL '00:00:60.5'", SqlLiteralType.Interval, "PT60.5S"],
        ["INTERVAL '10675199 02:48:05.4775807'", SqlLiteralType.Interval, "PT922337203685.4775807S"],
        ["INTERVAL '-10675199 -02:48:05.4775808'", SqlLiteralType.Interval, "-PT922337203685.4775808S"],
        ["INTERVAL '256204778:48:05.4775807'", SqlLiteralType.Interval, "PT922337203685.4775807S"],
        ["INTERVAL '-256204778:48:05.4775808'", SqlLiteralType.Interval, "-PT922337203685.4775808S"],
        ["INTERVAL 'P1DT2H'", SqlLiteralType.Interval, "PT93600S"],
        ["INTERVAL 'P-1DT2H'", SqlLiteralType.Interval, "-PT79200S"],
        ["INTERVAL 'P1W'", SqlLiteralType.Interval, "PT604800S"],
        ["INTERVAL '2 weeks'", SqlLiteralType.Interval, "PT1209600S"],
        ["INTERVAL '1 millisecond 2 microseconds'", SqlLiteralType.Interval, "PT0.001002S"],
        ["INTERVAL '1ms 0.1us'", SqlLiteralType.Interval, "PT0.0010001S"],
        ["INTERVAL 'PT1M'", SqlLiteralType.Interval, "PT60S"],
        ["INTERVAL '-1 02:03:04.0000001' DAY TO SECOND", SqlLiteralType.Interval, "-PT93784.0000001S"],
        ["INTERVAL '-PT922337203685.4775808S'", SqlLiteralType.Interval, "-PT922337203685.4775808S"],
        ["X''", SqlLiteralType.Binary, ""],
        ["X'00ff'", SqlLiteralType.Binary, "00FF"],
        ["x'ABCD'", SqlLiteralType.Binary, "ABCD"],
        ["N'héllo'", SqlLiteralType.String, "héllo"],
        ["n'it''s'", SqlLiteralType.String, "it's"],
    ];

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Typed literals: canonical kinds and values round-trip in every scalar context")]
    [MemberData(nameof(Literals))]
    public void Parse_TypedLiteral_ShouldNormalizeAndConsumeEveryContext(string text, SqlLiteralType kind, string value)
    {
        // Arrange
        var parser = new SqlQueryParser();
        string[] statements = [$"SELECT {text};", $"SELECT c FROM t WHERE c = {text};",
            $"INSERT INTO t VALUES ({text});", $"UPDATE t SET c = {text};", $"SELECT c IN ({text}) FROM t;",
            $"SELECT CASE WHEN TRUE THEN {text} ELSE NULL END;", $"CREATE TABLE t (c TEXT DEFAULT {text});"];

        // Act / Assert
        foreach (string sql in statements) { parser.Parse(sql).Diagnostics.ShouldBeEmpty(sql); }
        var statement = parser.Parse(statements[0]).ShouldBeOfType<SqlQueryStatement>();
        var literal = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>().Columns.Single().Expression.ShouldBeOfType<SqlLiteralExpression>();
        literal.LiteralType.ShouldBe(kind);
        literal.Value.ShouldBe(value);
        string canonical = SqlExpressionRenderer.Render(literal);
        var reloaded = parser.Parse($"SELECT {canonical};").ShouldBeOfType<SqlQueryStatement>();
        reloaded.Diagnostics.ShouldBeEmpty(canonical);
        var again = reloaded.SqlExpression.ShouldBeOfType<SqlSelectExpression>().Columns.Single().Expression.ShouldBeOfType<SqlLiteralExpression>();
        again.Value.ShouldBe(value);
        again.LiteralType.ShouldBe(kind);
        SqlExpressionRenderer.Render(again).ShouldBe(canonical);
        literal.Location!.Start.ShouldBe(7);
        literal.Location.End.ShouldBe(7 + text.Length);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Typed literals: malformed content reports SQL0008 at the whole literal")]
    [InlineData("DATE '2024-02-30'")]
    [InlineData("DATE '2024-1-1'")]
    [InlineData("TIME '24:00:00'")]
    [InlineData("TIME '10:00:00.12345678'")]
    [InlineData("TIMESTAMP '2024-01-01 10:00:00Z'")]
    [InlineData("TIMESTAMP '2024-01-01 10:00:00+01:00'")]
    [InlineData("TIMESTAMPTZ '2024-01-01 10:00 Europe/Paris'")]
    [InlineData("TIMESTAMPTZ '2024-01-01 10:00:00+14:01'")]
    [InlineData("TIMESTAMPTZ '0001-01-01 00:00:00+01:00'")]
    [InlineData("DATE 'infinity'")]
    [InlineData("TIMESTAMP 'now'")]
    [InlineData("X'0'")]
    [InlineData("X'0G'")]
    [InlineData("INTERVAL '12\u00a0'")]
    [InlineData("INTERVAL '1\u20032 hours'")]
    [InlineData("INTERVAL 'garbage'")]
    [InlineData("INTERVAL '12.34.56'")]
    [InlineData("INTERVAL '0.00000001'")]
    [InlineData("INTERVAL '922337203685.4775808'")]
    [InlineData("INTERVAL 'P'")]
    [InlineData("INTERVAL '1 day 2 days'")]
    [InlineData("INTERVAL '1:02' DAY TO HOUR")]
    [InlineData("INTERVAL '1 24' DAY TO HOUR")]
    [InlineData("INTERVAL 'PT0.00000001S'")]
    [InlineData("INTERVAL 'P1W2D'")]
    [InlineData("INTERVAL '0.01us'")]
    [InlineData("TIME '١٠:00'")]
    [InlineData("DATE '2024-01-01 '")]
    [InlineData("INTERVAL 'P1DT'")]
    [InlineData("INTERVAL 'PT1S2H'")]
    [InlineData("INTERVAL '1:60' HOUR TO MINUTE")]
    [InlineData("INTERVAL '1.12345678' SECOND")]
    [InlineData("INTERVAL '999999999999999999999' DAY")]
    [InlineData("INTERVAL 'PT922337203685.4775808S'")]
    [InlineData("INTERVAL '10675199 02:48:05.4775808'")]
    [InlineData("INTERVAL '-10675199 -02:48:05.4775809'")]
    [InlineData("INTERVAL '256204778:48:05.4775808'")]
    [InlineData("INTERVAL '02:60:00'")]
    [InlineData("INTERVAL '02:00:61'")]
    [InlineData("INTERVAL '02:-03:04'")]
    [InlineData("INTERVAL '02:03:04.12345678'")]
    [InlineData("INTERVAL '1 day 02:03:04:05'")]
    [InlineData("INTERVAL '1 day 2 02:03:04'")]
    [InlineData("INTERVAL '02:03:04 1 hour'")]
    [InlineData("INTERVAL '02:03:04 02:03:04'")]
    [InlineData("INTERVAL '1-12'")]
    [InlineData("INTERVAL 'P0001-xx-03T04:05:06'")]
    public void Parse_InvalidLiteral_ShouldReportContentDiagnostic(string text)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse($"SELECT {text};");

        // Assert
        var diagnostic = statement.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("SQL0008");
        diagnostic.Start.ShouldBe(7);
        diagnostic.End.ShouldBe(7 + text.Length);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Typed literals: recognized exclusions report CK00001")]
    [InlineData("TIME WITH TIME ZONE '10:00:00+01:00'")]
    [InlineData("TIME(3) WITH TIME ZONE '10:00:00+01:00'")]
    [InlineData("INTERVAL(3) '1 day'")]
    [InlineData("INTERVAL '1.5' SECOND(3)")]
    [InlineData("TIMESTAMP(3) '2024-01-01 10:00:00'")]
    [InlineData("UUID 'abc'")]
    [InlineData("JSON '{}'")]
    [InlineData("BYTEA '00'")]
    [InlineData("INT '1'")]
    [InlineData("DOUBLE PRECISION '1'")]
    [InlineData("NUMERIC(4,2) '1'")]
    [InlineData("B'0101'")]
    [InlineData("E'\\n'")]
    [InlineData("U&'hello'")]
    [InlineData("INTERVAL '1' DAY(3)")]
    [InlineData("INTERVAL '1 02' DAY(3) TO HOUR")]
    [InlineData("INTERVAL '1' YEAR")]
    [InlineData("INTERVAL '1-2' YEAR TO MONTH")]
    [InlineData("INTERVAL '1 year'")]
    [InlineData("INTERVAL '2 mons'")]
    [InlineData("INTERVAL 'P1Y'")]
    [InlineData("INTERVAL 'P1M'")]
    [InlineData("INTERVAL '1-2'")]
    [InlineData("INTERVAL '-1-2'")]
    [InlineData("INTERVAL '+1-2'")]
    [InlineData("INTERVAL '1-2 3 04:05:06'")]
    [InlineData("INTERVAL 'P1Y2M'")]
    [InlineData("INTERVAL 'P0001-02-03T04:05:06'")]
    [InlineData("INTERVAL 'P00010203T040506'")]
    [InlineData("INTERVAL 'P-1Y2M'")]
    [InlineData("INTERVAL 'P0001--02-03T04:05:06'")]
    [InlineData("INTERVAL '1 decade'")]
    [InlineData("INTERVAL '2 centuries'")]
    [InlineData("INTERVAL '1 quarter'")]
    public void Parse_UnsupportedLiteral_ShouldNameTheForm(string text)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse($"SELECT {text};");

        // Assert
        var diagnostic = statement.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CK00001");
        diagnostic.Message.ShouldNotBeNull().ShouldContain("literal", Case.Insensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Literal text: invariant readers preserve ticks across cultures")]
    public void ParseLiteralText_NonInvariantCulture_ShouldRetainExactValues()
    {
        // Arrange
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

            // Act / Assert
            SqlLiteralText.ParseInterval("PT0.0000001S").Ticks.ShouldBe(1L);
            SqlLiteralText.ParseInterval("-PT922337203685.4775808S").ShouldBe(TimeSpan.MinValue);
            SqlLiteralText.ParseTimestamp("2024-01-01T10:00:00.0000001").Ticks.ShouldBe(new DateTime(2024, 1, 1, 10, 0, 0).Ticks + 1);
            SqlLiteralText.ParseTimestampWithTimeZone("2024-01-01 10:00:00").Offset.ShouldBe(TimeSpan.Zero);
            Should.Throw<FormatException>(() => SqlLiteralText.ParseInterval("PT1,5S"));
            Should.Throw<FormatException>(() => SqlLiteralText.ParseInterval("1", "DAY TO DAY"));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Typed literals: contextual names remain columns and prefixes require adjacency")]
    [InlineData("date")]
    [InlineData("time")]
    [InlineData("interval")]
    [InlineData("x")]
    [InlineData("n")]
    public void Parse_ContextualName_ShouldRemainColumn(string name)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse($"SELECT {name} FROM t;").ShouldBeOfType<SqlQueryStatement>();

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>().Columns.Single().Expression.ShouldBeOfType<SqlColumnReferenceExpression>().ColumnName.ShouldBe(name);
        new SqlQueryParser().Parse("SELECT X '00';").Diagnostics.ShouldContain(d => d.Code == "SQL0003");
        new SqlQueryParser().Parse("CREATE TABLE t (c TEXT DEFAULT -'x');").Diagnostics.ShouldBeEmpty();
    }
}
