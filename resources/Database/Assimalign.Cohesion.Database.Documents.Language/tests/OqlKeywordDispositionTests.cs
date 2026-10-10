using System;
using System.Collections.Generic;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Language.Internal;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

/// <summary>
/// Keyword disposition (#1101): every word the OQL profile lexes as a keyword, and every
/// entry of the recognized-unsupported table, either parses inside a supported clause or is
/// rejected with exactly one <c>COHDBL001</c> naming its construct. A word added to either list
/// without a case here fails the coverage test.
/// </summary>
public sealed class OqlKeywordDispositionTests
{
    private static readonly Dictionary<string, KeywordCase> _corpus = new(StringComparer.OrdinalIgnoreCase)
    {
        // Supported query and index-definition vocabulary
        ["SELECT"] = Parses("SELECT a FROM c"),
        ["FROM"] = Parses("SELECT a FROM c"),
        ["WHERE"] = Parses("SELECT a FROM c WHERE a = 1"),
        ["GROUP"] = Parses("SELECT a, COUNT(*) FROM c GROUP BY a"),
        ["BY"] = Parses("SELECT a, COUNT(*) FROM c GROUP BY a"),
        ["HAVING"] = Parses("SELECT a, COUNT(*) FROM c GROUP BY a HAVING COUNT(*) > 1"),
        ["ORDER"] = Parses("SELECT a FROM c ORDER BY a"),
        ["ASC"] = Parses("SELECT a FROM c ORDER BY a ASC"),
        ["DESC"] = Parses("SELECT a FROM c ORDER BY a DESC"),
        ["AS"] = Parses("SELECT a AS b FROM c AS d"),
        ["CREATE"] = Parses("CREATE INDEX ix ON c (a)"),
        ["INDEX"] = Parses("CREATE INDEX ix ON c (a)"),
        ["ON"] = Parses("CREATE INDEX ix ON c (a)"),
        ["DROP"] = Parses("DROP INDEX ix ON c"),
        ["AND"] = Parses("SELECT a FROM c WHERE a = 1 AND b = 2"),
        ["OR"] = Parses("SELECT a FROM c WHERE a = 1 OR b = 2"),
        ["NOT"] = Parses("SELECT a FROM c WHERE NOT a = 1"),
        ["IS"] = Parses("SELECT a FROM c WHERE a IS NOT NULL"),
        ["NULL"] = Parses("SELECT a FROM c WHERE a IS NULL"),
        ["NIL"] = Parses("SELECT NIL FROM c"),
        ["TRUE"] = Parses("SELECT a FROM c WHERE b = TRUE"),
        ["FALSE"] = Parses("SELECT a FROM c WHERE b = FALSE"),
        // ODMG vocabulary the planner does not run (oql-sqlpp-basis owns its removal)
        ["DEFINE"] = Rejects("DEFINE people AS SELECT * FROM c", OqlClauses.Define),
        ["ELEMENT"] = Rejects("SELECT ELEMENT(a) FROM c", OqlClauses.Element),
        ["FLATTEN"] = Rejects("SELECT FLATTEN(a) FROM c", OqlClauses.Flatten),
        ["DISTINCT"] = Rejects("SELECT DISTINCT a FROM c", "DISTINCT"),
        ["ALL"] = Rejects("SELECT ALL a FROM c", "ALL"),
        ["IN"] = Rejects("SELECT * FROM c WHERE a IN (1, 2)", "IN"),
        ["EXISTS"] = Rejects("SELECT * FROM c WHERE EXISTS x IN c.items: x > 1", "EXISTS"),
        ["LIKE"] = Rejects("SELECT * FROM c WHERE a LIKE 'x%'", "LIKE"),
        ["BETWEEN"] = Rejects("SELECT * FROM c WHERE a BETWEEN 1 AND 2", "BETWEEN"),
        ["FOR"] = Rejects("SELECT * FROM c WHERE FOR ALL x IN c.items: x > 1", "FOR ALL"),
        ["SOME"] = Rejects("SELECT * FROM c WHERE SOME x IN c.items SATISFIES x > 1 END", "SOME ... SATISFIES"),
        ["ANY"] = Rejects("SELECT * FROM c WHERE ANY x IN c.items SATISFIES x > 1 END", "ANY ... SATISFIES"),
        ["UNDEFINED"] = Rejects("SELECT UNDEFINED FROM c", "UNDEFINED"),
        ["STRUCT"] = Rejects("SELECT STRUCT(a: 1) FROM c", "STRUCT"),
        ["LIST"] = Rejects("SELECT LIST(1, 2) FROM c", "LIST"),
        ["SET"] = Rejects("SELECT SET(1, 2) FROM c", "SET"),
        ["BAG"] = Rejects("SELECT BAG(1, 2) FROM c", "BAG"),
        ["ARRAY"] = Rejects("SELECT ARRAY(1, 2) FROM c", "ARRAY"),
        ["COLLECTION"] = Rejects("SELECT COLLECTION(1, 2) FROM c", "COLLECTION"),
        ["FIRST"] = Rejects("SELECT FIRST(a) FROM c", "FIRST"),
        ["LAST"] = Rejects("SELECT LAST(a) FROM c", "LAST"),
        ["UNIQUE"] = Rejects("SELECT UNIQUE(a) FROM c", "UNIQUE"),
        ["LISTTOSET"] = Rejects("SELECT LISTTOSET(a) FROM c", "LISTTOSET"),
        ["TYPEOF"] = Rejects("SELECT TYPEOF(a) FROM c", "TYPEOF"),
        ["ABS"] = Rejects("SELECT ABS(a) FROM c", "ABS"),
        // Statements and clauses of other languages
        ["ALTER"] = Rejects("ALTER INDEX ix ON c", "ALTER"),
        ["INSERT"] = Rejects("INSERT INTO c VALUES (1)", "INSERT"),
        ["UPDATE"] = Rejects("UPDATE c SET a = 1", "UPDATE"),
        ["DELETE"] = Rejects("DELETE FROM c WHERE a IN (1)", "DELETE"),
        ["USE"] = Rejects("USE other", "USE"),
        ["BEGIN"] = Rejects("BEGIN TRANSACTION", "BEGIN"),
        ["COMMIT"] = Rejects("COMMIT", "COMMIT"),
        ["ROLLBACK"] = Rejects("ROLLBACK", "ROLLBACK"),
        ["WITH"] = Rejects("WITH x AS (SELECT a FROM c) SELECT a FROM x", "WITH"),
        ["JOIN"] = Rejects("SELECT * FROM c JOIN d ON c.a = d.a", "JOIN"),
        ["UNION"] = Rejects("SELECT a FROM c UNION SELECT a FROM d", "UNION"),
        ["INTERSECT"] = Rejects("SELECT a FROM c INTERSECT SELECT a FROM d", "INTERSECT"),
        ["EXCEPT"] = Rejects("SELECT a FROM c EXCEPT SELECT a FROM d", "EXCEPT"),
        // SQL++ words
        ["LIMIT"] = Rejects("SELECT * FROM c WHERE a = 1 LIMIT 5", "LIMIT"),
        ["OFFSET"] = Rejects("SELECT * FROM c ORDER BY a DESC OFFSET 2", "OFFSET"),
        ["UPSERT"] = Rejects("UPSERT INTO c (KEY, VALUE) VALUES ('k', {'a': 1})", "UPSERT"),
        ["MERGE"] = Rejects("MERGE INTO c USING d ON c.id = d.id WHEN MATCHED THEN UPDATE SET c.a = d.a", "MERGE"),
        ["UNNEST"] = Rejects("SELECT * FROM c UNNEST c.items AS i", "UNNEST"),
        ["EVERY"] = Rejects("SELECT * FROM c WHERE EVERY x IN c.items SATISFIES x > 1 END", "EVERY ... SATISFIES"),
        ["SATISFIES"] = Rejects("SELECT * FROM c WHERE EVERY x IN c.items SATISFIES x > 1 END", "EVERY ... SATISFIES"),
    };

    [Fact(DisplayName = "Cohesion Test [Documents.Language] - Keyword disposition: every keyword and recognized-unsupported entry has a case")]
    public void Corpus_EveryKeywordAndTableEntry_ShouldHaveACase()
    {
        // Act / Assert
        RequireCoverage(Words(), _corpus);
        _corpus.Keys.Except(Words(), StringComparer.OrdinalIgnoreCase)
            .ShouldBeEmpty("Corpus keys must name current keywords or recognized-unsupported entries.");
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Language] - Keyword disposition: a word added without a case fails coverage")]
    public void Corpus_WordWithoutCase_ShouldFailCoverage()
    {
        // Arrange
        var words = Words().Append("FUTUREWORD").ToArray();

        // Act / Assert
        Should.Throw<ShouldAssertException>(() => RequireCoverage(words, _corpus))
            .Message.ShouldContain("FUTUREWORD", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Language] - Keyword disposition: each word parses in a supported clause or reports one COHDBL001 naming its construct")]
    public void Parse_EveryCase_ShouldMatchItsDisposition()
    {
        // Arrange
        var failures = new List<string>();

        // Act
        foreach (var (word, keywordCase) in _corpus)
        {
            if (!ContainsWord(keywordCase.Oql, word))
            {
                failures.Add($"{word}: the case does not contain the word: {keywordCase.Oql}");
                continue;
            }

            var statement = new OqlQueryParser().Parse(keywordCase.Oql);
            var errors = statement.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
            string found = string.Join(" | ", errors.Select(error => $"{error.Code}: {error.Message}"));
            if (keywordCase.Construct is null)
            {
                if (errors.Length != 0)
                {
                    failures.Add($"{word}: expected a supported parse of '{keywordCase.Oql}' but found {found}");
                }
            }
            else if (errors.Length != 1 || errors[0].Code != "COHDBL001" ||
                     errors[0].Message?.Contains($"The {keywordCase.Construct} clause", StringComparison.Ordinal) != true)
            {
                failures.Add($"{word}: expected one COHDBL001 naming '{keywordCase.Construct}' for '{keywordCase.Oql}' but found {found}");
            }
            else if (OqlUnsupportedVocabulary.TryFind(word.ToUpperInvariant(), out var entry) &&
                     !errors[0].Message!.Contains(entry.Construct, StringComparison.Ordinal))
            {
                failures.Add($"{word}: the diagnostic does not name the table's construct '{entry.Construct}': {found}");
            }
        }

        // Assert
        failures.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Language] - Keyword disposition: words matched anywhere are lexer keywords or functions")]
    public void Table_AnywhereEntries_ShouldBeKeywordsOrFunctions()
    {
        // Arrange: only a reserved word may name a construct wherever it appears; any other
        // word must stay positional so a field of that name still parses.
        var reserved = OqlLanguageProfile.Instance.Keywords.ToArray()
            .Concat(OqlLanguageProfile.Instance.Functions.ToArray())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Act / Assert
        OqlUnsupportedVocabulary.Words
            .Where(word => word.Position == OqlWordPosition.Anywhere && !reserved.Contains(word.Spelling))
            .Select(word => word.Spelling)
            .ShouldBeEmpty();
        OqlUnsupportedVocabulary.Words
            .Where(word => word.Position != OqlWordPosition.Anywhere && reserved.Contains(word.Spelling))
            .Select(word => word.Spelling)
            .ShouldBeEmpty("Recognized-but-unsupported words stay positional rather than becoming lexer keywords.");
    }

    private static void RequireCoverage(IEnumerable<string> words, IReadOnlyDictionary<string, KeywordCase> corpus)
        => words.Where(word => !corpus.ContainsKey(word))
            .ShouldBeEmpty("Every keyword and recognized-unsupported entry needs a keyword-disposition case.");

    private static string[] Words()
        => OqlLanguageProfile.Instance.Keywords.ToArray()
            .Concat(OqlUnsupportedVocabulary.Words.Select(word => word.Spelling))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool ContainsWord(string oql, string word)
    {
        var lexer = new TokenLexer(oql, OqlLanguageProfile.Instance.ToLexerOptions());
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

    private static KeywordCase Parses(string oql) => new(oql, null);

    private static KeywordCase Rejects(string oql, string construct) => new(oql, construct);

    /// <summary>A disposition case: a supported parse when <paramref name="Construct"/> is null.</summary>
    private sealed record KeywordCase(string Oql, string? Construct);
}
