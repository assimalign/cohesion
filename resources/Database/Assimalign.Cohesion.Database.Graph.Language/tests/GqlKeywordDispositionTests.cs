using System;
using System.Collections.Generic;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Language.Internal;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

/// <summary>
/// Keyword disposition (#1101): every word the GQL profile lexes as a keyword, and every
/// entry of the recognized-unsupported table, either parses inside a supported clause or is
/// rejected with exactly one <c>COHDBL001</c> naming its construct. A word added to either list
/// without a case here fails the coverage test.
/// </summary>
public sealed class GqlKeywordDispositionTests
{
    private static readonly Dictionary<string, KeywordCase> _corpus = new(StringComparer.OrdinalIgnoreCase)
    {
        // Supported clauses
        ["MATCH"] = Parses("MATCH (a) RETURN a"),
        ["RETURN"] = Parses("MATCH (a) RETURN a"),
        ["AS"] = Parses("MATCH (a) RETURN a.x AS y"),
        ["WHERE"] = Parses("MATCH (a) WHERE a.x = 1 RETURN a"),
        ["AND"] = Parses("MATCH (a) WHERE a.x = 1 AND a.y = 2 RETURN a"),
        ["INSERT"] = Parses("INSERT (a:Person)"),
        ["CREATE"] = Parses("CREATE (a:Person)"),
        ["DELETE"] = Parses("MATCH (a) DELETE a"),
        ["DETACH"] = Parses("MATCH (a) DETACH DELETE a"),
        ["SHOW"] = Parses("SHOW LABELS"),
        ["RELATIONSHIP"] = Parses("SHOW RELATIONSHIP TYPES"),
        ["PROPERTY"] = Parses("SHOW PROPERTY KEYS"),
        ["NULL"] = Parses("MATCH (a) WHERE a.x = NULL RETURN a"),
        ["TRUE"] = Parses("MATCH (a) WHERE a.x = TRUE RETURN a"),
        ["FALSE"] = Parses("MATCH (a) WHERE a.x = FALSE RETURN a"),
        // Reserved words that name nothing unsupported are labels and property keys.
        ["NODE"] = Parses("MATCH (a:NODE) RETURN a"),
        ["EDGE"] = Parses("MATCH ()-[r:EDGE]->() RETURN r"),
        ["LABEL"] = Parses("MATCH (a) RETURN a.label"),
        ["PATH"] = Parses("MATCH (a:PATH) RETURN a.path"),
        // Clauses, predicates and expressions outside the executable subset
        ["OPTIONAL"] = Rejects("OPTIONAL MATCH (a) RETURN a", GqlClauses.OptionalMatch),
        ["MANDATORY"] = Rejects("MANDATORY MATCH (a) RETURN a", GqlClauses.MandatoryMatch),
        ["WITH"] = Rejects("MATCH (a) WITH a RETURN a", "WITH"),
        ["SET"] = Rejects("MATCH (a) SET a.x = 1", "SET"),
        ["REMOVE"] = Rejects("MATCH (a) REMOVE a.x", "REMOVE"),
        ["MERGE"] = Rejects("MERGE (n)", "MERGE"),
        ["ORDER"] = Rejects("MATCH (a) RETURN a ORDER BY a.x", GqlClauses.OrderBy),
        ["BY"] = Rejects("MATCH (a) RETURN a ORDER BY a.x", GqlClauses.OrderBy),
        ["ASC"] = Rejects("MATCH (a) RETURN a ORDER BY a.x ASC", GqlClauses.OrderBy),
        ["DESC"] = Rejects("MATCH (a) RETURN a ORDER BY a.x DESC", GqlClauses.OrderBy),
        ["LIMIT"] = Rejects("MATCH (a) RETURN a LIMIT 5", "LIMIT"),
        ["OFFSET"] = Rejects("MATCH (a) RETURN a OFFSET 5", "OFFSET"),
        ["SKIP"] = Rejects("MATCH (a) RETURN a SKIP 5", "SKIP"),
        ["LET"] = Rejects("MATCH (a) LET x = a.y RETURN x", "LET"),
        ["UNWIND"] = Rejects("UNWIND [1, 2] AS a RETURN a", "UNWIND"),
        ["FOREACH"] = Rejects("MATCH (a) FOREACH (x IN [1] | SET a.y = x)", "FOREACH"),
        ["UNION"] = Rejects("MATCH (a) RETURN a UNION MATCH (b) RETURN b", "UNION"),
        ["DISTINCT"] = Rejects("MATCH (a) RETURN DISTINCT a", "DISTINCT"),
        ["ALL"] = Rejects("MATCH ALL (a)-[]->(b) RETURN a", "ALL PATH SEARCH"),
        ["ANY"] = Rejects("MATCH ANY (a)-[]->(b) RETURN a", "ANY PATH SEARCH"),
        ["SHORTEST"] = Rejects("MATCH SHORTEST 2 (a)-[]->(b) RETURN a", GqlClauses.ShortestPath),
        ["OR"] = Rejects("MATCH (a) WHERE a.x = 1 OR a.y = 2 RETURN a", "OR"),
        ["NOT"] = Rejects("MATCH (a) WHERE NOT a.x = 1 RETURN a", "NOT"),
        ["XOR"] = Rejects("MATCH (a) WHERE a.x = 1 XOR a.y = 2 RETURN a", "XOR"),
        ["IN"] = Rejects("MATCH (a) WHERE a.x IN [1, 2] RETURN a", "IN"),
        ["STARTS"] = Rejects("MATCH (a) WHERE a.name STARTS WITH 'A' RETURN a", "STARTS WITH"),
        ["ENDS"] = Rejects("MATCH (a) WHERE a.name ENDS WITH 'A' RETURN a", "ENDS WITH"),
        ["CONTAINS"] = Rejects("MATCH (a) WHERE a.name CONTAINS 'A' RETURN a", "CONTAINS"),
        ["IS"] = Rejects("MATCH (n IS A) RETURN n", GqlUnsupportedVocabulary.IsLabelExpression),
        ["EXISTS"] = Rejects("MATCH (a) WHERE EXISTS { (a)-[]->(b) } RETURN a", "EXISTS"),
        ["LIKE"] = Rejects("MATCH (a) WHERE a.name LIKE 'A%' RETURN a", "LIKE"),
        ["BETWEEN"] = Rejects("MATCH (a) WHERE a.x BETWEEN 1 AND 2 RETURN a", "BETWEEN"),
        ["NONE"] = Rejects("MATCH (a) WHERE NONE(a.tags) RETURN a", "FUNCTION NONE"),
        ["SINGLE"] = Rejects("MATCH (a) WHERE SINGLE(a.tags) RETURN a", "FUNCTION SINGLE"),
        ["CASE"] = Rejects(CaseExpression, "CASE"),
        ["WHEN"] = Rejects(CaseExpression, "CASE"),
        ["THEN"] = Rejects(CaseExpression, "CASE"),
        ["ELSE"] = Rejects(CaseExpression, "CASE"),
        ["END"] = Rejects(CaseExpression, "CASE"),
        ["GRAPH"] = Rejects("CREATE GRAPH g", "GRAPH"),
        ["CALL"] = Rejects("CALL db.labels() YIELD label", "CALL"),
        ["YIELD"] = Rejects("MATCH (a)-[]->(b) YIELD a RETURN a", "YIELD"),
        ["FILTER"] = Rejects("MATCH (a) FILTER a.x = 1 RETURN a", "FILTER"),
        // Server, session, catalog and transaction scope
        ["USE"] = Rejects("USE g MATCH (a) RETURN a", "USE"),
        ["SESSION"] = Rejects("SESSION SET GRAPH g", "SESSION"),
        ["DROP"] = Rejects("DROP GRAPH g", "DROP"),
        ["ALTER"] = Rejects("ALTER GRAPH g", "ALTER"),
        ["GRANT"] = Rejects("GRANT READ ON GRAPH g TO u", "GRANT"),
        ["REVOKE"] = Rejects("REVOKE READ ON GRAPH g FROM u", "REVOKE"),
        ["BEGIN"] = Rejects("BEGIN TRANSACTION", "BEGIN"),
        ["COMMIT"] = Rejects("COMMIT", "COMMIT"),
        ["ROLLBACK"] = Rejects("ROLLBACK", "ROLLBACK"),
        ["FINISH"] = Rejects("MATCH (a) FINISH", "FINISH"),
        ["NEXT"] = Rejects("MATCH (a) RETURN a NEXT MATCH (b) RETURN b", "NEXT"),
        ["FOR"] = Rejects("FOR x IN [1, 2] RETURN x", "FOR"),
        ["GROUP"] = Rejects("MATCH (a) RETURN a.x GROUP BY a.x", "GROUP"),
        ["HAVING"] = Rejects("MATCH (a) RETURN a HAVING a.x = 1", "HAVING"),
        ["DATABASE"] = Rejects("CREATE DATABASE d", "DATABASE"),
        ["SCHEMA"] = Rejects("CREATE SCHEMA s", "SCHEMA"),
        ["INDEX"] = Rejects("CREATE INDEX ix", "INDEX"),
        ["CATALOG"] = Rejects("CREATE CATALOG c", "CATALOG"),
        // ISO/IEC 39075 delete, path and label constructs
        ["NODETACH"] = Rejects("MATCH (n) NODETACH DELETE n", "NODETACH DELETE"),
        ["WALK"] = Rejects("MATCH WALK (a)-[]->(b) RETURN a", "WALK PATH MODE"),
        ["TRAIL"] = Rejects("MATCH TRAIL (a)-[]->(b) RETURN a", "TRAIL PATH MODE"),
        ["SIMPLE"] = Rejects("MATCH SIMPLE (a)-[]->(b) RETURN a", "SIMPLE PATH MODE"),
        ["ACYCLIC"] = Rejects("MATCH ACYCLIC (a)-[]->(b) RETURN a", "ACYCLIC PATH MODE"),
        ["|"] = Rejects("MATCH (n:A|B) RETURN n", "LABEL DISJUNCTION"),
        ["&"] = Rejects("MATCH (n:A&B) RETURN n", "LABEL CONJUNCTION"),
        ["!"] = Rejects("MATCH (n:!A) RETURN n", "LABEL NEGATION"),
        ["%"] = Rejects("MATCH (n:%) RETURN n", "WILDCARD LABEL"),
    };

    private const string CaseExpression = "MATCH (a) RETURN CASE WHEN a.x = 1 THEN 1 ELSE 2 END";

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Keyword disposition: every keyword and recognized-unsupported entry has a case")]
    public void Corpus_EveryKeywordAndTableEntry_ShouldHaveACase()
    {
        // Act / Assert
        RequireCoverage(Words(), _corpus);
        _corpus.Keys.Except(Words(), StringComparer.OrdinalIgnoreCase)
            .ShouldBeEmpty("Corpus keys must name current keywords or recognized-unsupported entries.");
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Keyword disposition: a word added without a case fails coverage")]
    public void Corpus_WordWithoutCase_ShouldFailCoverage()
    {
        // Arrange
        var words = Words().Append("FUTUREWORD").ToArray();

        // Act / Assert
        Should.Throw<ShouldAssertException>(() => RequireCoverage(words, _corpus))
            .Message.ShouldContain("FUTUREWORD", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Keyword disposition: each word parses in a supported clause or reports one COHDBL001 naming its construct")]
    public void Parse_EveryCase_ShouldMatchItsDisposition()
    {
        // Arrange
        var failures = new List<string>();

        // Act
        foreach (var (word, keywordCase) in _corpus)
        {
            if (!ContainsWord(keywordCase.Gql, word))
            {
                failures.Add($"{word}: the case does not contain the word: {keywordCase.Gql}");
                continue;
            }

            var statement = new GqlQueryParser().Parse(keywordCase.Gql);
            var errors = statement.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
            string found = string.Join(" | ", errors.Select(error => $"{error.Code}: {error.Message}"));
            if (keywordCase.Construct is null)
            {
                if (errors.Length != 0)
                {
                    failures.Add($"{word}: expected a supported parse of '{keywordCase.Gql}' but found {found}");
                }
            }
            else if (errors.Length != 1 || errors[0].Code != "COHDBL001" ||
                     errors[0].Message?.Contains($"The {keywordCase.Construct} clause", StringComparison.Ordinal) != true)
            {
                failures.Add($"{word}: expected one COHDBL001 naming '{keywordCase.Construct}' for '{keywordCase.Gql}' but found {found}");
            }
            else if (GqlUnsupportedVocabulary.TryFind(word.ToUpperInvariant(), out var entry) &&
                     !errors[0].Message!.Contains(entry.Construct, StringComparison.Ordinal))
            {
                failures.Add($"{word}: the diagnostic does not name the table's construct '{entry.Construct}': {found}");
            }
        }

        // Assert
        failures.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Keyword disposition: positional words are not lexer keywords")]
    public void Table_PositionalEntries_ShouldNotBeKeywords()
    {
        // Arrange: a path mode or NODETACH stays an identifier, so it still names a variable.
        var keywords = GqlLanguageProfile.Instance.Keywords.ToArray().ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Act / Assert
        GqlUnsupportedVocabulary.Words
            .Where(word => word.Position is GqlWordPosition.PathMode or GqlWordPosition.BeforeDelete && keywords.Contains(word.Spelling))
            .Select(word => word.Spelling)
            .ShouldBeEmpty();
    }

    private static void RequireCoverage(IEnumerable<string> words, IReadOnlyDictionary<string, KeywordCase> corpus)
        => words.Where(word => !corpus.ContainsKey(word))
            .ShouldBeEmpty("Every keyword and recognized-unsupported entry needs a keyword-disposition case.");

    private static string[] Words()
        => GqlLanguageProfile.Instance.Keywords.ToArray()
            .Concat(GqlUnsupportedVocabulary.Words.Select(word => word.Spelling))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool ContainsWord(string gql, string word)
    {
        var lexer = new TokenLexer(gql, GqlLanguageProfile.Instance.ToLexerOptions());
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

    private static KeywordCase Parses(string gql) => new(gql, null);

    private static KeywordCase Rejects(string gql, string construct) => new(gql, construct);

    /// <summary>A disposition case: a supported parse when <paramref name="Construct"/> is null.</summary>
    private sealed record KeywordCase(string Gql, string? Construct);
}
