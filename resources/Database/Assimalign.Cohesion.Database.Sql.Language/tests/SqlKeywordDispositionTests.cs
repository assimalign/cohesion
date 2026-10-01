using System;
using System.Collections.Generic;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Language;

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
        ["FROM"] = Parses("SELECT a FROM t"),
        ["WHERE"] = Parses("SELECT a FROM t WHERE a = 1"),
        ["INSERT"] = Parses("INSERT INTO t VALUES (1)"),
        ["INTO"] = Parses("INSERT INTO t VALUES (1)"),
        ["VALUES"] = Parses("INSERT INTO t VALUES (1)"),
        ["UPDATE"] = Parses("UPDATE t SET a = 1"),
        ["SET"] = Parses("UPDATE t SET a = 1"),
        ["DELETE"] = Parses("DELETE FROM t WHERE a = 1"),
        ["RETURNING"] = Rejects("DELETE FROM t RETURNING a", SqlClauses.Returning),
        // DDL
        ["CREATE"] = Parses("CREATE TABLE t (a INT)"),
        ["TABLE"] = Parses("CREATE TABLE t (a INT)"),
        ["DROP"] = Parses("DROP TABLE t"),
        ["IF"] = Parses("DROP TABLE IF EXISTS t"),
        ["ALTER"] = Parses("ALTER TABLE t ADD COLUMN c INT"),
        ["ADD"] = Parses("ALTER TABLE t ADD COLUMN c INT"),
        ["COLUMN"] = Parses("ALTER TABLE t ADD COLUMN c INT"),
        ["INDEX"] = Parses("CREATE INDEX ix ON t (a)"),
        ["VIEW"] = Rejects("CREATE VIEW v AS SELECT a FROM t", SqlClauses.CreateView),
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
        ["LEFT"] = Rejects("SELECT * FROM t LEFT JOIN u ON t.id = u.id", "LEFT OUTER JOIN"),
        ["RIGHT"] = Rejects("SELECT * FROM t RIGHT JOIN u ON t.id = u.id", "RIGHT OUTER JOIN"),
        ["FULL"] = Rejects("SELECT * FROM t FULL JOIN u ON t.id = u.id", "FULL OUTER JOIN"),
        ["OUTER"] = Rejects("SELECT * FROM t LEFT OUTER JOIN u ON t.id = u.id", "LEFT OUTER JOIN"),
        ["CROSS"] = Rejects("SELECT * FROM t CROSS JOIN u", "CROSS JOIN"),
        ["NATURAL"] = Rejects("SELECT * FROM t NATURAL JOIN u", SqlClauses.Natural),
        ["USING"] = Rejects("SELECT * FROM t JOIN u USING (id)", SqlClauses.Using),
        // Clauses and modifiers
        ["AS"] = Parses("SELECT a AS b FROM t"),
        ["GROUP"] = Parses("SELECT a FROM t GROUP BY a"),
        ["BY"] = Parses("SELECT a FROM t GROUP BY a"),
        ["HAVING"] = Parses("SELECT a FROM t GROUP BY a HAVING COUNT(*) > 1"),
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
        ["~"] = Rejects("SELECT ~a FROM t", "~ operator"),
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
        }

        // Assert
        failures.ShouldBeEmpty();
    }

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
