using System;
using System.Text;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

/// <summary>
/// Generates random scalar-expression text over the dialect's expression grammar with a fixed
/// seed: every operator rung, sign and NOT nesting, the predicates, CASE, COLLATE, CAST,
/// builtin and plain calls, subqueries, and the literal and identifier spellings (quoted,
/// keyword, unicode, doubled quotes). Operands are parenthesized at random, so the text
/// exercises both the parser's precedence and explicit grouping; text that does not parse is
/// skipped by the caller. Spelling varies too — keyword case, spacing, comments — because the
/// canonical form must not depend on it.
/// </summary>
internal sealed class SqlExpressionTextGenerator
{
    private static readonly string[] _columns =
    [
        "a", "B", "qty", "t.a", "dbo.t.qty", "\"order\"", "\"my col\"", "\"Count\"", "数量", "größe", "_x",
        "\"ét\"", "\"qty\"", "escape", "\"unknown\"",
    ];

    private static readonly string[] _literals =
    [
        "0", "42", "007", "999999999999999999999999", "1.5", ".5", "1e3", "2.5E-3", "'x'", "'it''s'", "''",
        "'--not a comment'", "'/* nor this */'", "'ünïcödé ✓'", "TRUE", "false", "NULL",
    ];

    private readonly Random _random;

    internal SqlExpressionTextGenerator(int seed)
    {
        _random = new Random(seed);
    }

    internal string Next(int depth) => Value(depth);

    private string Value(int depth)
    {
        if (depth <= 0)
        {
            return Leaf();
        }

        int next = depth - 1;
        return _random.Next(24) switch
        {
            0 or 1 => Leaf(),
            2 => Operand(next) + Spaced(Pick("+", "-", "||")) + Operand(next),
            3 => Operand(next) + Spaced(Pick("*", "/", "%")) + Operand(next),
            4 => Operand(next) + Spaced(Pick("=", "<>", "!=", "<", ">", "<=", ">=")) + Operand(next),
            5 => Operand(next) + Spaced(Pick("AND", "and", "OR", "Or")) + Operand(next),
            6 => Pick("NOT ", "not ", "NOT /* c */ ") + Operand(next),
            7 => Pick("-", "- ", "+", "+ ") + Signed(next),
            8 => Operand(next) + Pick(" IS NULL", " is not null", " IS NOT NULL"),
            9 => Operand(next) + Pick(" BETWEEN ", " NOT BETWEEN ", " between ") + Operand(next) + " AND " + Operand(next),
            10 => Operand(next) + Pick(" IN (", " NOT IN (", " in(") + List(next) + ")",
            11 => Operand(next) + Pick(" LIKE ", " NOT LIKE ") + Pick("'a%'", "'_b'", "(" + Value(next) + ")", "b"),
            12 => "CASE WHEN " + Value(next) + " THEN " + Value(next) + Else(next) + " END",
            13 => "case " + Value(next) + " when " + Value(next) + " then " + Value(next) + Else(next) + " end",
            14 => Primary(next) + Pick(" COLLATE binary", " collate CASE_INSENSITIVE", " COLLATE case_accent_insensitive"),
            15 => "CAST(" + Value(next) + Pick(" AS INT)", " as varchar( 10 ))", " AS DECIMAL(10,2))", " AS Boolean)"),
            16 => Pick("UPPER(", "lower(", "LENGTH(", "ABS(", "f(", "\"select\"(") + Value(next) + ")",
            17 => "COALESCE(" + Value(next) + ", " + Value(next) + ")",
            18 => "(SELECT " + Value(next) + " FROM t WHERE " + Value(next) + ")",
            19 => Pick("EXISTS (", "NOT EXISTS (", "exists(") + "SELECT a FROM u WHERE " + Value(next) + ")",
            20 => Operand(next) + Pick(" IN (SELECT ", " NOT IN (SELECT ") + Value(next) + " FROM u)",
            21 => "(" + Value(next) + ")",
            _ => Operand(next) + Spaced(Pick("+", "*", "=", "AND", "OR")) + Operand(next) + Spaced(Pick("-", "/", "<", "OR")) + Operand(next),
        };
    }

    // A sign's operand: a leaf or a parenthesized expression, so the generator never writes
    // a second minus sign straight after the first (that would start a line comment).
    private string Signed(int depth) => _random.Next(3) == 0 ? Leaf() : "(" + Value(depth) + ")";

    private string Operand(int depth) => _random.Next(2) == 0 ? "(" + Value(depth) + ")" : Value(depth);

    private string Primary(int depth) => _random.Next(2) == 0 ? Leaf() : "(" + Value(depth) + ")";

    private string Else(int depth) => _random.Next(2) == 0 ? string.Empty : " ELSE " + Value(depth);

    private string List(int depth)
    {
        var builder = new StringBuilder(Value(depth));
        int count = _random.Next(3);
        for (int index = 0; index < count; index++)
        {
            builder.Append(Pick(", ", ",")).Append(Value(depth));
        }
        return builder.ToString();
    }

    private string Leaf() => _random.Next(5) switch
    {
        0 or 1 => Pick(_columns),
        2 or 3 => Pick(_literals),
        _ => Pick("@p", "$1", "@Value_2"),
    };

    private string Spaced(string text) => Pick(" ", "  ", " /* x */ ") + text + Pick(" ", "\n ");

    private string Pick(params string[] choices) => choices[_random.Next(choices.Length)];
}
