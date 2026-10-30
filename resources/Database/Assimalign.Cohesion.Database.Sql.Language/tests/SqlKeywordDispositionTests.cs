using System;
using System.Collections.Generic;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Language.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

/// <summary>
/// Keyword disposition (#1101): every word the SQL profile lexes as a keyword, and every
/// entry of the recognized-unsupported table, either parses inside a supported clause or is
/// rejected with exactly one <c>CK00001</c> naming its construct. A word added to either list
/// without a case here fails the coverage test.
/// </summary>
public sealed class SqlKeywordDispositionTests
{
    private static readonly Dictionary<string, KeywordCase> _corpus = new(StringComparer.OrdinalIgnoreCase)
    {
        // DML
        ["SELECT"] = Parses("SELECT a FROM t"),
        ["EXPLAIN"] = Parses("EXPLAIN SELECT a FROM t"),
        ["FROM"] = Parses("SELECT a FROM t"),
        ["WHERE"] = Parses("SELECT a FROM t WHERE a = 1"),
        ["INSERT"] = Parses("INSERT INTO t VALUES (1)"),
        ["INTO"] = Parses("INSERT INTO t VALUES (1)"),
        ["VALUES"] = Parses("INSERT INTO t VALUES (1)"),
        ["UPDATE"] = Parses("UPDATE t SET a = 1"),
        ["SET"] = Parses("UPDATE t SET a = 1"),
        ["DELETE"] = Parses("DELETE FROM t WHERE a = 1"),
        ["MERGE"] = Parses("MERGE INTO t USING s ON t.id=s.id WHEN MATCHED THEN DO NOTHING"),
        ["RETURNING"] = Parses("DELETE FROM t RETURNING a"),
        ["FUNCTION"] = Parses("CREATE FUNCTION f(p INT) RETURNS INT VOLATILE CALLED ON NULL INPUT RETURN p"),
        ["DOMAIN"] = Parses("CREATE DOMAIN d AS INT CHECK (VALUE > 0)"),
        ["RETURNS"] = Parses("CREATE FUNCTION f(p INT) RETURNS INT VOLATILE CALLED ON NULL INPUT RETURN p"),
        ["RETURN"] = Parses("CREATE FUNCTION f(p INT) RETURNS INT VOLATILE CALLED ON NULL INPUT RETURN p"),
        ["IMMUTABLE"] = Parses("CREATE FUNCTION f(p INT) RETURNS INT IMMUTABLE CALLED ON NULL INPUT RETURN p"),
        ["STABLE"] = Parses("CREATE FUNCTION f(p INT) RETURNS INT STABLE CALLED ON NULL INPUT RETURN p"),
        ["VOLATILE"] = Parses("CREATE FUNCTION f(p INT) RETURNS INT VOLATILE CALLED ON NULL INPUT RETURN p"),
        ["STRICT"] = Parses("CREATE FUNCTION f(p INT) RETURNS INT VOLATILE STRICT RETURN p"),
        ["CALLED"] = Parses("CREATE FUNCTION f(p INT) RETURNS INT VOLATILE CALLED ON NULL INPUT RETURN p"),
        ["INPUT"] = Parses("CREATE FUNCTION f(p INT) RETURNS INT VOLATILE CALLED ON NULL INPUT RETURN p"),
        // DDL
        ["CREATE"] = Parses("CREATE TABLE t (a INT)"),
        ["TABLE"] = Parses("CREATE TABLE t (a INT)"),
        ["DROP"] = Parses("DROP TABLE t"),
        ["TRUNCATE"] = Parses("TRUNCATE TABLE t"),
        ["IF"] = Parses("DROP TABLE IF EXISTS t"),
        ["ALTER"] = Parses("ALTER TABLE t ADD COLUMN c INT"),
        ["ADD"] = Parses("ALTER TABLE t ADD COLUMN c INT"),
        ["COLUMN"] = Parses("ALTER TABLE t ADD COLUMN c INT"),
        ["INDEX"] = Parses("CREATE INDEX ix ON t (a)"),
        ["SEQUENCE"] = Parses("CREATE SEQUENCE ids"),
        ["SERIAL"] = Parses("CREATE TABLE t (id SERIAL)"),
        ["BIGSERIAL"] = Parses("CREATE TABLE t (id BIGSERIAL)"),
        ["SMALLSERIAL"] = Parses("CREATE TABLE t (id SMALLSERIAL)"),
        ["OVERRIDING"] = Parses("INSERT INTO t OVERRIDING SYSTEM VALUE VALUES (1)"),
        ["MINVALUE"] = Parses("CREATE TABLE t (id INT GENERATED ALWAYS AS IDENTITY (MINVALUE 1))"),
        ["MAXVALUE"] = Parses("CREATE TABLE t (id INT GENERATED ALWAYS AS IDENTITY (MAXVALUE 9))"),
        ["CYCLE"] = Parses("CREATE TABLE t (id INT GENERATED ALWAYS AS IDENTITY (CYCLE))"),
        ["CACHE"] = Parses("CREATE TABLE t (id INT GENERATED ALWAYS AS IDENTITY (CACHE 2))"),
        ["RESTART"] = Parses("ALTER TABLE t ALTER COLUMN id RESTART WITH 1"),
        ["VIEW"] = Parses("CREATE VIEW v AS SELECT a FROM t"),
        ["PRIMARY"] = Parses("CREATE TABLE t (a INT PRIMARY KEY)"),
        ["KEY"] = Parses("CREATE TABLE t (a INT PRIMARY KEY)"),
        ["FOREIGN"] = Parses("CREATE TABLE c (p INT, FOREIGN KEY (p) REFERENCES t (id))"),
        ["REFERENCES"] = Parses("CREATE TABLE c (p INT REFERENCES t (id))"),
        ["CONSTRAINT"] = Parses("CREATE TABLE c (a INT, CONSTRAINT uq UNIQUE (a))"),
        ["DEFAULT"] = Parses("CREATE TABLE t (a INT DEFAULT 1)"),
        ["CHECK"] = Parses("CREATE TABLE t (a INT CHECK (a > 0))"),
        ["UNIQUE"] = Parses("CREATE TABLE t (a INT UNIQUE)"),
        ["COLLATE"] = Parses("SELECT a FROM t WHERE a = 'x' COLLATE binary"),
        ["CASCADE"] = Parses("CREATE TABLE c (p INT REFERENCES t (id) ON DELETE CASCADE)"),
        ["RESTRICT"] = Parses("CREATE TABLE c (p INT REFERENCES t (id) ON DELETE RESTRICT)"),
        ["COLLATION"] = Rejects("CREATE COLLATION c (provider = icu)", "user-defined collations (CREATE COLLATION)"),
        ["FULLTEXT"] = Rejects("CREATE FULLTEXT INDEX ix ON t (a)", "collation-aware full-text indexes"),
        // Joins
        ["JOIN"] = Parses("SELECT * FROM t JOIN u ON t.id = u.id"),
        ["INNER"] = Parses("SELECT * FROM t INNER JOIN u ON t.id = u.id"),
        ["ON"] = Parses("SELECT * FROM t JOIN u ON t.id = u.id"),
        ["LEFT"] = Parses("SELECT * FROM t LEFT JOIN u ON t.id = u.id"),
        ["RIGHT"] = Parses("SELECT * FROM t RIGHT JOIN u ON t.id = u.id"),
        ["FULL"] = Parses("SELECT * FROM t FULL JOIN u ON t.id = u.id"),
        ["OUTER"] = Parses("SELECT * FROM t LEFT OUTER JOIN u ON t.id = u.id"),
        ["CROSS"] = Parses("SELECT * FROM t CROSS JOIN u"),
        ["NATURAL"] = Rejects("SELECT * FROM t NATURAL JOIN u", SqlClauses.Natural),
        ["USING"] = Parses("SELECT * FROM t JOIN u USING (id)"),
        ["LATERAL"] = Rejects("SELECT * FROM t JOIN LATERAL (SELECT b FROM u) x ON TRUE", "LATERAL subquery"),
        // Clauses and modifiers
        ["AS"] = Parses("SELECT a AS b FROM t"),
        ["GROUP"] = Parses("SELECT a FROM t GROUP BY a"),
        ["BY"] = Parses("SELECT a FROM t GROUP BY a"),
        ["HAVING"] = Parses("SELECT a FROM t GROUP BY a HAVING COUNT(*) > 1"),
        ["FILTER"] = Parses("SELECT COUNT(*) FILTER (WHERE a > 1) FROM t"),
        ["WITHIN"] = Rejects("SELECT PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY a) FROM t", "WITHIN GROUP"),
        ["ORDER"] = Parses("SELECT a FROM t ORDER BY a"),
        ["ASC"] = Parses("SELECT a FROM t ORDER BY a ASC"),
        ["DESC"] = Parses("SELECT a FROM t ORDER BY a DESC"),
        ["LIMIT"] = Parses("SELECT a FROM t LIMIT 1"),
        ["OFFSET"] = Parses("SELECT a FROM t LIMIT 1 OFFSET 1"),
        ["FETCH"] = Parses("SELECT a FROM t FETCH NEXT 1 ROWS ONLY"),
        ["FOR"] = Rejects("SELECT a FROM t FOR UPDATE", "FOR UPDATE/SHARE"),
        ["PERCENT"] = Rejects("SELECT TOP (1) PERCENT a FROM t", "PERCENT"),
        ["TIES"] = Rejects("SELECT a FROM t FETCH FIRST 1 ROW WITH TIES", "WITH TIES"),
        ["NEXT"] = Parses("SELECT a FROM t FETCH NEXT 1 ROWS ONLY"),
        ["ROWS"] = Parses("SELECT a FROM t FETCH NEXT 1 ROWS ONLY"),
        ["ONLY"] = Parses("SELECT a FROM t FETCH NEXT 1 ROWS ONLY"),
        ["DISTINCT"] = Parses("SELECT DISTINCT a FROM t"),
        ["ALL"] = Rejects("SELECT ALL a FROM t", SqlClauses.All),
        ["TOP"] = Parses("SELECT TOP 1 a FROM t"),
        // Set operations
        ["UNION"] = Parses("SELECT a FROM t UNION SELECT a FROM u"),
        ["INTERSECT"] = Parses("SELECT a FROM t INTERSECT SELECT a FROM u"),
        ["EXCEPT"] = Parses("SELECT a FROM t EXCEPT SELECT a FROM u"),
        // Logical operators and predicates
        ["AND"] = Parses("SELECT a FROM t WHERE a = 1 AND b = 2"),
        ["OR"] = Parses("SELECT a FROM t WHERE a = 1 OR b = 2"),
        ["NOT"] = Parses("SELECT a FROM t WHERE NOT a = 1"),
        ["IN"] = Parses("SELECT a FROM t WHERE a IN (1, 2)"),
        ["EXISTS"] = Parses("SELECT a FROM t WHERE EXISTS (SELECT b FROM u)"),
        ["BETWEEN"] = Parses("SELECT a FROM t WHERE a BETWEEN 1 AND 2"),
        ["ILIKE"] = Rejects("SELECT a FROM t WHERE a ILIKE 'x%'", "ILIKE"),
        ["SIMILAR"] = Rejects("SELECT a FROM t WHERE a SIMILAR TO 'x%'", "SIMILAR TO"),
        ["LIKE"] = Parses("SELECT a FROM t WHERE a LIKE 'x%'"),
        ["IS"] = Parses("SELECT a FROM t WHERE a IS NULL"),
        ["ANY"] = Parses("SELECT a FROM t WHERE a = ANY (SELECT b FROM u)"),
        ["SOME"] = Parses("SELECT a FROM t WHERE a = SOME (SELECT b FROM u)"),
        ["~"] = Rejects("SELECT ~a FROM t", "prefix ~ operator"),
        // Literals
        ["NULL"] = Parses("SELECT a FROM t WHERE a IS NULL"),
        ["TRUE"] = Parses("SELECT a FROM t WHERE b = TRUE"),
        ["FALSE"] = Parses("SELECT a FROM t WHERE b = FALSE"),
        // CASE
        ["CASE"] = Parses("SELECT CASE WHEN a = 1 THEN 'x' ELSE 'y' END FROM t"),
        ["WHEN"] = Parses("SELECT CASE WHEN a = 1 THEN 'x' ELSE 'y' END FROM t"),
        ["THEN"] = Parses("SELECT CASE WHEN a = 1 THEN 'x' ELSE 'y' END FROM t"),
        ["ELSE"] = Parses("SELECT CASE WHEN a = 1 THEN 'x' ELSE 'y' END FROM t"),
        ["END"] = Parses("SELECT CASE WHEN a = 1 THEN 'x' ELSE 'y' END FROM t"),
        // CTEs and transactions
        ["WITH"] = Parses("WITH c AS (SELECT a FROM t) SELECT a FROM c"),
        ["RECURSIVE"] = Parses("WITH RECURSIVE c (n) AS (SELECT 1 UNION ALL SELECT n+1 FROM c) SELECT n FROM c"),
        ["MATERIALIZED"] = Rejects("WITH c AS MATERIALIZED (SELECT a FROM t) SELECT * FROM c", "[NOT] MATERIALIZED"),
        ["SEARCH"] = Rejects("WITH c AS (SELECT a FROM t) SEARCH DEPTH FIRST BY a SET ord SELECT * FROM c", "SEARCH/CYCLE"),
        ["BEGIN"] = Parses("BEGIN TRANSACTION"),
        ["COMMIT"] = Parses("COMMIT"),
        ["ROLLBACK"] = Parses("ROLLBACK"),
        ["TRANSACTION"] = Parses("BEGIN TRANSACTION"),
        // T1 scalar forms, followed by recognized unsupported window calls
        ["NULLIF"] = Parses("SELECT NULLIF(a, a) FROM t"),
        ["TRIM"] = Parses("SELECT TRIM(a) FROM t"),
        ["LTRIM"] = Parses("SELECT LTRIM(a) FROM t"),
        ["RTRIM"] = Parses("SELECT RTRIM(a) FROM t"),
        ["SUBSTRING"] = Parses("SELECT SUBSTRING(a FROM 1) FROM t"),
        ["REPLACE"] = Parses("SELECT REPLACE(a, 'x', 'y') FROM t"),
        ["CONCAT"] = Parses("SELECT CONCAT(a) FROM t"),
        ["CEILING"] = Parses("SELECT CEILING(a) FROM t"),
        ["FLOOR"] = Parses("SELECT FLOOR(a) FROM t"),
        ["ROUND"] = Parses("SELECT ROUND(a) FROM t"),
        ["POWER"] = Parses("SELECT POWER(a, 2) FROM t"),
        ["SQRT"] = Parses("SELECT SQRT(a) FROM t"),
        ["MOD"] = Parses("SELECT MOD(a, 2) FROM t"),
        ["NOW"] = Parses("SELECT NOW() FROM t"),
        ["CURRENT_DATE"] = Parses("SELECT CURRENT_DATE FROM t"),
        ["CURRENT_TIME"] = Parses("SELECT CURRENT_TIME FROM t"),
        ["CURRENT_TIMESTAMP"] = Parses("SELECT CURRENT_TIMESTAMP FROM t"),
        ["LOCALTIME"] = Parses("SELECT LOCALTIME FROM t"),
        ["LOCALTIMESTAMP"] = Parses("SELECT LOCALTIMESTAMP FROM t"),
        ["EXTRACT"] = Parses("SELECT EXTRACT(YEAR FROM a) FROM t"),
        ["FIRST_VALUE"] = Parses("SELECT FIRST_VALUE(a) OVER() FROM t"),
        ["LAST_VALUE"] = Parses("SELECT LAST_VALUE(a) OVER() FROM t"),
        ["NTH_VALUE"] = Parses("SELECT NTH_VALUE(a,2) OVER() FROM t"),
        // Window functions and physical-row/offset-free peer frames execute.
        ["OVER"] = Parses("SELECT ROW_NUMBER() OVER (ORDER BY a) FROM t"),
        ["PARTITION"] = Parses("SELECT RANK() OVER(PARTITION BY a) FROM t"),
        ["WINDOW"] = Parses("SELECT a FROM t WINDOW w AS (ORDER BY a)"),
        ["RANGE"] = Parses(WindowFrame("RANGE")),
        ["ROW"] = Parses(WindowFrame("ROWS")),
        ["PRECEDING"] = Parses(WindowFrame("ROWS")),
        ["FOLLOWING"] = Parses("SELECT SUM(a) OVER (ORDER BY a ROWS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING) FROM t"),
        ["CURRENT"] = Parses(WindowFrame("ROWS")),
        ["UNBOUNDED"] = Parses(WindowFrame("ROWS")),
    };

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Keyword disposition: every keyword and recognized-unsupported entry has a case")]
    public void Corpus_EveryKeywordAndTableEntry_ShouldHaveACase()
    {
        // Act / Assert
        RequireCoverage(Words(), _corpus);
        _corpus.Keys.Except(Words(), StringComparer.OrdinalIgnoreCase)
            .ShouldBeEmpty("Corpus keys must name current keywords or recognized-unsupported entries.");
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Keyword disposition: a word added without a case fails coverage")]
    public void Corpus_WordWithoutCase_ShouldFailCoverage()
    {
        // Arrange
        var words = Words().Append("FUTUREWORD").ToArray();

        // Act / Assert
        Should.Throw<ShouldAssertException>(() => RequireCoverage(words, _corpus))
            .Message.ShouldContain("FUTUREWORD", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Keyword disposition: each word parses in a supported clause or reports one CK00001 naming its construct")]
    public void Parse_EveryCase_ShouldMatchItsDisposition()
    {
        // Arrange
        var failures = new List<string>();

        // Act
        foreach (var (word, keywordCase) in _corpus)
        {
            if (!ContainsWord(keywordCase.Sql, word))
            {
                failures.Add($"{word}: the case does not contain the word: {keywordCase.Sql}");
                continue;
            }

            var statement = (SqlQueryStatement)new SqlQueryParser().Parse(keywordCase.Sql);
            var errors = statement.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
            string found = string.Join(" | ", errors.Select(error => $"{error.Code}: {error.Message}"));
            if (keywordCase.Construct is null)
            {
                if (errors.Length != 0 || statement.SqlExpression.CommandType == SqlQueryCommandType.Unknown)
                {
                    failures.Add($"{word}: expected a supported parse of '{keywordCase.Sql}' but found {found}");
                }
            }
            else if (errors.Length != 1 || errors[0].Code != "CK00001" ||
                     errors[0].Message?.Contains(keywordCase.Construct, StringComparison.Ordinal) != true)
            {
                failures.Add($"{word}: expected one CK00001 naming '{keywordCase.Construct}' for '{keywordCase.Sql}' but found {found}");
            }
            else if (TableConstruct(word) is { } construct &&
                     !errors[0].Message!.Contains(construct, StringComparison.Ordinal))
            {
                failures.Add($"{word}: the diagnostic does not name the table's construct '{construct}': {found}");
            }
        }

        // Assert
        failures.ShouldBeEmpty();
    }

    private static string? TableConstruct(string word)
        => SqlUnsupportedVocabulary.Words
            .Where(entry => entry.Spelling.Equals(word, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Construct)
            .FirstOrDefault();

    private static void RequireCoverage(IEnumerable<string> words, IReadOnlyDictionary<string, KeywordCase> corpus)
        => words.Where(word => !corpus.ContainsKey(word))
            .ShouldBeEmpty("Every keyword and recognized-unsupported entry needs a keyword-disposition case.");

    private static string[] Words()
        => SqlLanguageProfile.Instance.Keywords.ToArray()
            .Concat(SqlUnsupportedVocabulary.Words.Select(word => word.Spelling))
            .Concat(new[] { "ILIKE", "SIMILAR", "CEILING", "CONCAT", "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP", "EXTRACT", "FLOOR", "LOCALTIME", "LOCALTIMESTAMP", "FIRST_VALUE", "LAST_VALUE", "NTH_VALUE", "LTRIM", "MOD", "NOW", "NULLIF", "POWER", "REPLACE", "ROUND", "RTRIM", "SQRT", "SUBSTRING", "TRIM" })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool ContainsWord(string sql, string word)
    {
        var lexer = new TokenLexer(sql, SqlLanguageProfile.Instance.ToLexerOptions());
        foreach (var token in lexer)
        {
            if (token.Type is not (TokenType.String or TokenType.QuotedIdentifier or TokenType.Comment) &&
                token.Value.Equals(word, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string WindowFrame(string unit)
        => $"SELECT SUM(a) OVER (ORDER BY a {unit} BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) FROM t";

    private static KeywordCase Parses(string sql) => new(sql, null);

    private static KeywordCase Rejects(string sql, string construct) => new(sql, construct);

    /// <summary>A disposition case: a supported parse when <paramref name="Construct"/> is null.</summary>
    private sealed record KeywordCase(string Sql, string? Construct);
}
