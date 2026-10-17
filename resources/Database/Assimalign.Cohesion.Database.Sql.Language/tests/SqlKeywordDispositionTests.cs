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
/// rejected with exactly one <c>COHDBL001</c> naming its construct. A word added to either list
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
        ["RETURNING"] = Rejects("DELETE FROM t RETURNING a", SqlClauses.Returning),
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
        ["IF"] = Parses("DROP TABLE IF EXISTS t"),
        ["ALTER"] = Parses("ALTER TABLE t ADD COLUMN c INT"),
        ["ADD"] = Parses("ALTER TABLE t ADD COLUMN c INT"),
        ["COLUMN"] = Parses("ALTER TABLE t ADD COLUMN c INT"),
        ["INDEX"] = Parses("CREATE INDEX ix ON t (a)"),
        ["VIEW"] = Rejects("DROP VIEW v", SqlClauses.DropView),
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
        ["RIGHT"] = Rejects("SELECT * FROM t RIGHT JOIN u ON t.id = u.id", "RIGHT OUTER JOIN"),
        ["FULL"] = Rejects("SELECT * FROM t FULL JOIN u ON t.id = u.id", "FULL OUTER JOIN"),
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
        ["GROUPING"] = Rejects("SELECT a FROM t GROUP BY GROUPING SETS ((a), ())", "GROUPING SETS"),
        ["ROLLUP"] = Rejects("SELECT a FROM t GROUP BY ROLLUP (a)", "ROLLUP"),
        ["CUBE"] = Rejects("SELECT a FROM t GROUP BY CUBE (a)", "CUBE"),
        ["GROUPING_ID"] = Rejects("SELECT GROUPING_ID(a) FROM t GROUP BY a", "GROUPING_ID"),
        ["FILTER"] = Rejects("SELECT COUNT(*) FILTER (WHERE a > 1) FROM t", "FILTER"),
        ["WITHIN"] = Rejects("SELECT PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY a) FROM t", "WITHIN GROUP"),
        ["ORDER"] = Parses("SELECT a FROM t ORDER BY a"),
        ["ASC"] = Parses("SELECT a FROM t ORDER BY a ASC"),
        ["DESC"] = Parses("SELECT a FROM t ORDER BY a DESC"),
        ["NULLS"] = Rejects("SELECT a FROM t ORDER BY a NULLS FIRST", "NULLS FIRST and NULLS LAST"),
        ["LIMIT"] = Parses("SELECT a FROM t LIMIT 1"),
        ["OFFSET"] = Parses("SELECT a FROM t LIMIT 1 OFFSET 1"),
        ["FETCH"] = Rejects("SELECT a FROM t FETCH NEXT 1 ROWS ONLY", SqlClauses.Fetch),
        ["NEXT"] = Rejects("SELECT a FROM t FETCH NEXT 1 ROWS ONLY", SqlClauses.Fetch),
        ["ROWS"] = Rejects("SELECT a FROM t FETCH NEXT 1 ROWS ONLY", SqlClauses.Fetch),
        ["ONLY"] = Rejects("SELECT a FROM t FETCH NEXT 1 ROWS ONLY", SqlClauses.Fetch),
        ["DISTINCT"] = Parses("SELECT DISTINCT a FROM t"),
        ["ALL"] = Rejects("SELECT ALL a FROM t", SqlClauses.All),
        ["TOP"] = Rejects("SELECT TOP 1 a FROM t", SqlClauses.Top),
        // Set operations
        ["UNION"] = Rejects("SELECT a FROM t UNION SELECT a FROM u", SqlClauses.SetOperation),
        ["INTERSECT"] = Rejects("SELECT a FROM t INTERSECT SELECT a FROM u", SqlClauses.Intersect),
        ["EXCEPT"] = Rejects("SELECT a FROM t EXCEPT SELECT a FROM u", SqlClauses.Except),
        // Logical operators and predicates
        ["AND"] = Parses("SELECT a FROM t WHERE a = 1 AND b = 2"),
        ["OR"] = Parses("SELECT a FROM t WHERE a = 1 OR b = 2"),
        ["NOT"] = Parses("SELECT a FROM t WHERE NOT a = 1"),
        ["IN"] = Parses("SELECT a FROM t WHERE a IN (1, 2)"),
        ["EXISTS"] = Parses("SELECT a FROM t WHERE EXISTS (SELECT b FROM u)"),
        ["BETWEEN"] = Parses("SELECT a FROM t WHERE a BETWEEN 1 AND 2"),
        ["LIKE"] = Parses("SELECT a FROM t WHERE a LIKE 'x%'"),
        ["IS"] = Parses("SELECT a FROM t WHERE a IS NULL"),
        ["ANY"] = Rejects("SELECT a FROM t WHERE a = ANY (SELECT b FROM u)", "ANY quantified comparison"),
        ["SOME"] = Rejects("SELECT a FROM t WHERE a = SOME (SELECT b FROM u)", "SOME quantified comparison"),
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
        ["WITH"] = Rejects("WITH c AS (SELECT a FROM t) SELECT a FROM c", SqlClauses.Cte),
        ["RECURSIVE"] = Rejects("WITH RECURSIVE c AS (SELECT a FROM t) SELECT a FROM c", SqlClauses.Recursive),
        ["BEGIN"] = Parses("BEGIN TRANSACTION"),
        ["COMMIT"] = Parses("COMMIT"),
        ["ROLLBACK"] = Parses("ROLLBACK"),
        ["TRANSACTION"] = Parses("BEGIN TRANSACTION"),
        // Recognized unsupported function calls
        ["NULLIF"] = Rejects("SELECT NULLIF() FROM t", "NULLIF"),
        ["TRIM"] = Rejects("SELECT TRIM() FROM t", "TRIM"),
        ["LTRIM"] = Rejects("SELECT LTRIM() FROM t", "LTRIM"),
        ["RTRIM"] = Rejects("SELECT RTRIM() FROM t", "RTRIM"),
        ["SUBSTRING"] = Rejects("SELECT SUBSTRING() FROM t", "SUBSTRING"),
        ["REPLACE"] = Rejects("SELECT REPLACE() FROM t", "REPLACE"),
        ["CONCAT"] = Rejects("SELECT CONCAT() FROM t", "CONCAT"),
        ["CEILING"] = Rejects("SELECT CEILING() FROM t", "CEILING"),
        ["FLOOR"] = Rejects("SELECT FLOOR() FROM t", "FLOOR"),
        ["ROUND"] = Rejects("SELECT ROUND() FROM t", "ROUND"),
        ["POWER"] = Rejects("SELECT POWER() FROM t", "POWER"),
        ["SQRT"] = Rejects("SELECT SQRT() FROM t", "SQRT"),
        ["MOD"] = Rejects("SELECT MOD() FROM t", "MOD"),
        ["NOW"] = Rejects("SELECT NOW() FROM t", "NOW"),
        ["CURRENT_DATE"] = Rejects("SELECT CURRENT_DATE() FROM t", "CURRENT_DATE"),
        ["CURRENT_TIME"] = Rejects("SELECT CURRENT_TIME() FROM t", "CURRENT_TIME"),
        ["CURRENT_TIMESTAMP"] = Rejects("SELECT CURRENT_TIMESTAMP() FROM t", "CURRENT_TIMESTAMP"),
        ["LOCALTIME"] = Rejects("SELECT LOCALTIME() FROM t", "LOCALTIME"),
        ["LOCALTIMESTAMP"] = Rejects("SELECT LOCALTIMESTAMP() FROM t", "LOCALTIMESTAMP"),
        ["EXTRACT"] = Rejects("SELECT EXTRACT() FROM t", "EXTRACT"),
        ["ROW_NUMBER"] = Rejects("SELECT ROW_NUMBER() FROM t", "ROW_NUMBER"),
        ["RANK"] = Rejects("SELECT RANK() FROM t", "RANK"),
        ["DENSE_RANK"] = Rejects("SELECT DENSE_RANK() FROM t", "DENSE_RANK"),
        ["LEAD"] = Rejects("SELECT LEAD() FROM t", "LEAD"),
        ["LAG"] = Rejects("SELECT LAG() FROM t", "LAG"),
        ["FIRST_VALUE"] = Rejects("SELECT FIRST_VALUE() FROM t", "FIRST_VALUE"),
        ["LAST_VALUE"] = Rejects("SELECT LAST_VALUE() FROM t", "LAST_VALUE"),
        ["NTH_VALUE"] = Rejects("SELECT NTH_VALUE() FROM t", "NTH_VALUE"),
        ["NTILE"] = Rejects("SELECT NTILE() FROM t", "NTILE"),
        // Windows: the frame words belong to the OVER specification that rejects them.
        ["OVER"] = Rejects("SELECT ROW_NUMBER() OVER (ORDER BY a) FROM t", SqlClauses.Over),
        ["PARTITION"] = Rejects("SELECT a FROM t PARTITION BY a", SqlClauses.Partition),
        ["WINDOW"] = Rejects("SELECT a FROM t WINDOW w AS (ORDER BY a)", SqlClauses.Window),
        ["RANGE"] = Rejects(WindowFrame("RANGE"), SqlClauses.Over),
        ["ROW"] = Rejects(WindowFrame("ROWS"), SqlClauses.Over),
        ["PRECEDING"] = Rejects(WindowFrame("ROWS"), SqlClauses.Over),
        ["FOLLOWING"] = Rejects("SELECT SUM(a) OVER (ORDER BY a ROWS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING) FROM t", SqlClauses.Over),
        ["CURRENT"] = Rejects(WindowFrame("ROWS"), SqlClauses.Over),
        ["UNBOUNDED"] = Rejects(WindowFrame("ROWS"), SqlClauses.Over),
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

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Keyword disposition: each word parses in a supported clause or reports one COHDBL001 naming its construct")]
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
            else if (errors.Length != 1 || errors[0].Code != "COHDBL001" ||
                     errors[0].Message?.Contains(keywordCase.Construct, StringComparison.Ordinal) != true)
            {
                failures.Add($"{word}: expected one COHDBL001 naming '{keywordCase.Construct}' for '{keywordCase.Sql}' but found {found}");
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
