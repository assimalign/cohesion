using System;
using System.Collections.Generic;
using System.Linq;
using Shouldly;
using Xunit;
using Assimalign.Cohesion.Database.Graph.Language.Internal;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

/// <summary>
/// Keyword disposition (#1101): every word the GQL profile lexes as a keyword, every entry of
/// the recognized-unsupported table, contextual EXPLAIN, and every executable label-expression spelling (#1139)
/// either parses inside a supported clause or is rejected with exactly one <c>CK00001</c>
/// naming its construct. A word added to any of those lists without a case here fails the
/// coverage test.
/// </summary>
public sealed class GqlKeywordDispositionTests
{
    private static readonly Dictionary<string, KeywordCase> _corpus = new(StringComparer.OrdinalIgnoreCase)
    {
        // Supported clauses
        ["MATCH"] = Parses("MATCH (a) RETURN a"),
        ["OPTIONAL"] = Parses("OPTIONAL MATCH (a) RETURN a"),
        ["EXPLAIN"] = Parses("EXPLAIN MATCH (a) RETURN a"),
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
        ["UNKNOWN"] = Parses("MATCH (a) WHERE (a.x = 1) IS UNKNOWN RETURN a"),
        // Reserved words that name nothing unsupported are labels and property keys.
        ["NODE"] = Parses("MATCH (a:NODE) RETURN a"),
        ["EDGE"] = Parses("MATCH ()-[r:EDGE]->() RETURN r"),
        ["LABEL"] = Parses("MATCH (a) RETURN a.label"),
        ["PATH"] = Parses("MATCH (a:PATH) RETURN a.path"),
        // Clauses, predicates and expressions outside the executable subset
        ["MANDATORY"] = Rejects("MANDATORY MATCH (a) RETURN a", GqlClauses.MandatoryMatch),
        ["WITH"] = Parses("MATCH (a) WITH a RETURN a"),
        ["SET"] = Parses("MATCH (a) SET a.x = 1"),
        ["REMOVE"] = Parses("MATCH (a) REMOVE a.x"),
        ["MERGE"] = Parses("MERGE (n)"),
        ["ON"] = Parses("MERGE (n) ON CREATE SET n.x = 1 ON MATCH SET n.x = 2"),
        ["ORDER"] = Parses("MATCH (a) RETURN a ORDER BY a.x"),
        ["BY"] = Parses("MATCH (a) RETURN a ORDER BY a.x"),
        ["GROUP"] = Parses("MATCH (a) RETURN a.x, count(*) AS n GROUP BY a.x"),
        ["ASC"] = Parses("MATCH (a) RETURN a ORDER BY a.x ASC"),
        ["ASCENDING"] = Parses("MATCH (a) RETURN a ORDER BY a.x ASCENDING"),
        ["DESC"] = Parses("MATCH (a) RETURN a ORDER BY a.x DESC"),
        ["DESCENDING"] = Parses("MATCH (a) RETURN a ORDER BY a.x DESCENDING"),
        ["LIMIT"] = Parses("MATCH (a) RETURN a LIMIT 5"),
        ["OFFSET"] = Parses("MATCH (a) RETURN a OFFSET 5"),
        ["SKIP"] = Parses("MATCH (a) RETURN a SKIP 5"),
        ["LET"] = Parses("MATCH (a) LET x = a.y RETURN x"),
        ["UNWIND"] = Parses("UNWIND [1, 2] AS a RETURN a"),
        ["FOREACH"] = Rejects("MATCH (a) FOREACH (x IN [1] | SET a.y = x)", "FOREACH"),
        ["UNION"] = Rejects("MATCH (a) RETURN a UNION MATCH (b) RETURN b", "UNION"),
        ["DISTINCT"] = Parses("MATCH (a) RETURN DISTINCT a.name"),
        ["ALL"] = Parses("MATCH (a) RETURN ALL a.name"),
        ["ANY"] = Parses("MATCH ANY (a)-[]->(b) RETURN a"),
        ["SHORTEST"] = Parses("MATCH SHORTEST 2 (a)-[]->(b) RETURN a"),
        ["OR"] = Parses("MATCH (a) WHERE a.x = 1 OR a.y = 2 RETURN a"),
        ["NOT"] = Parses("MATCH (a) WHERE NOT a.x = 1 RETURN a"),
        ["XOR"] = Parses("MATCH (a) WHERE a.x = 1 XOR a.y = 2 RETURN a"),
        ["IN"] = Parses("MATCH (a) WHERE a.x IN [1, 2] RETURN a"),
        ["STARTS"] = Parses("MATCH (a) WHERE a.name STARTS WITH 'A' RETURN a"),
        ["ENDS"] = Parses("MATCH (a) WHERE a.name ENDS WITH 'A' RETURN a"),
        ["CONTAINS"] = Parses("MATCH (a) WHERE a.name CONTAINS 'A' RETURN a"),
        // ISO <is label expression> (#1139 flipped the #1101 pin); IS NULL and truth tests execute since #1140.
        ["IS"] = Parses("MATCH (n IS A) RETURN n"),
        ["EXISTS"] = Parses("MATCH (a) WHERE EXISTS { (a)-[]->(b) } RETURN a"),
        ["LIKE"] = Rejects("MATCH (a) WHERE a.name LIKE 'A%' RETURN a", "LIKE"),
        ["BETWEEN"] = Rejects("MATCH (a) WHERE a.x BETWEEN 1 AND 2 RETURN a", "BETWEEN"),
        ["NONE"] = Parses("RETURN none(x IN [1,2] WHERE x < 0)"),
        ["SINGLE"] = Parses("RETURN single(x IN [1,2] WHERE x = 1)"),
        ["CASE"] = Parses(caseExpression),
        ["WHEN"] = Parses(caseExpression),
        ["THEN"] = Parses(caseExpression),
        ["ELSE"] = Parses(caseExpression),
        ["END"] = Parses(caseExpression),
        ["COALESCE"] = Parses("RETURN COALESCE(NULL, 1)"),
        ["NULLIF"] = Parses("RETURN NULLIF(1, 1)"),
        ["GRAPH"] = Rejects("CREATE GRAPH g", "GRAPH"),
        ["CALL"] = Rejects("CALL db.labels() YIELD label", "CALL"),
        ["YIELD"] = Rejects("MATCH (a)-[]->(b) YIELD a RETURN a", "YIELD"),
        ["FILTER"] = Parses("MATCH (a) FILTER a.x = 1 RETURN a"),
        // Server, session, catalog and transaction scope
        ["USE"] = Rejects("USE g MATCH (a) RETURN a", "USE"),
        ["SESSION"] = Rejects("SESSION SET GRAPH g", "SESSION"),
        ["DROP"] = Rejects("DROP GRAPH g", "DROP"),
        ["ALTER"] = Rejects("ALTER GRAPH g", "ALTER"),
        ["GRANT"] = Rejects("GRANT READ ON GRAPH g TO u", "GRANT"),
        ["REVOKE"] = Rejects("REVOKE READ ON GRAPH g FROM u", "REVOKE"),
        ["BEGIN"] = Parses("BEGIN TRANSACTION"),
        ["START"] = Parses("START TRANSACTION"),
        ["TRANSACTION"] = Parses("BEGIN TRANSACTION"),
        ["COMMIT"] = Parses("COMMIT"),
        ["ROLLBACK"] = Parses("ROLLBACK"),
        ["FINISH"] = Rejects("MATCH (a) FINISH", "FINISH"),
        ["NEXT"] = Parses("MATCH (a) RETURN a NEXT MATCH (b) RETURN b"),
        ["FOR"] = Parses("FOR x IN [1, 2] RETURN x"),
        ["HAVING"] = Rejects("MATCH (a) RETURN a HAVING a.x = 1", "HAVING"),
        ["OVER"] = Rejects("RETURN count(*) OVER ()", "WINDOW OVER"),
        ["STDDEV_SAMP"] = Rejects("MATCH (a) RETURN STDDEV_SAMP(a.age)", "STDDEV_SAMP"),
        ["STDDEV_POP"] = Rejects("MATCH (a) RETURN STDDEV_POP(a.age)", "STDDEV_POP"),
        ["PERCENTILE_CONT"] = Rejects("MATCH (a) RETURN PERCENTILE_CONT(a.age, 0.5)", "PERCENTILE_CONT"),
        ["PERCENTILE_DISC"] = Rejects("MATCH (a) RETURN PERCENTILE_DISC(a.age, 0.5)", "PERCENTILE_DISC"),
        ["DATABASE"] = Rejects("CREATE DATABASE d", "DATABASE"),
        ["SCHEMA"] = Rejects("CREATE SCHEMA s", "SCHEMA"),
        ["INDEX"] = Rejects("CREATE INDEX ix", "INDEX"),
        ["CATALOG"] = Rejects("CREATE CATALOG c", "CATALOG"),
        // ISO/IEC 39075 delete, path and edge constructs
        ["NODETACH"] = Rejects("MATCH (n) NODETACH DELETE n", "NODETACH DELETE"),
        ["WALK"] = Parses("MATCH WALK (a)-[]->(b) RETURN a"),
        ["TRAIL"] = Parses("MATCH TRAIL (a)-[]->(b) RETURN a"),
        ["SIMPLE"] = Parses("MATCH SIMPLE (a)-[]->(b) RETURN a"),
        ["ACYCLIC"] = Parses("MATCH ACYCLIC (a)-[]->(b) RETURN a"),
        ["~"] = Rejects("MATCH (a)~[r]~(b) RETURN a", GqlUnsupportedVocabulary.UndirectedEdge),
        ["CARDINALITY"] = Rejects("RETURN cardinality(1)", "FUNCTION cardinality"),
        ["LTRIM"] = Rejects("RETURN ltrim(1)", "FUNCTION ltrim"),
        ["RTRIM"] = Rejects("RETURN rtrim(1)", "FUNCTION rtrim"),
        ["BTRIM"] = Rejects("RETURN btrim(1)", "FUNCTION btrim"),
        ["LEFT"] = Rejects("RETURN left(1)", "FUNCTION left"),
        ["RIGHT"] = Rejects("RETURN right(1)", "FUNCTION right"),
        ["LOG10"] = Rejects("RETURN log10(1)", "FUNCTION log10"),
        ["TOSTRINGLIST"] = Rejects("RETURN toStringList(1)", "FUNCTION toStringList"),
        ["TOINTEGERLIST"] = Rejects("RETURN toIntegerList(1)", "FUNCTION toIntegerList"),
        ["TOFLOATLIST"] = Rejects("RETURN toFloatList(1)", "FUNCTION toFloatList"),
        ["TOBOOLEANLIST"] = Rejects("RETURN toBooleanList(1)", "FUNCTION toBooleanList"),
        ["ID"] = Rejects("RETURN id(1)", "FUNCTION id (use element_id)"),
        ["REDUCE"] = Parses("RETURN reduce(a=0,x IN [1,2] | a+x)"),
        ["DATE"] = Rejects("RETURN date(1)", "FUNCTION date"),
        ["DATETIME"] = Rejects("RETURN datetime(1)", "FUNCTION datetime"),
        ["DURATION"] = Rejects("RETURN duration(1)", "FUNCTION duration"),
        ["TIME"] = Rejects("RETURN time(1)", "FUNCTION time"),
        ["LOCALTIME"] = Rejects("RETURN localtime(1)", "FUNCTION localtime"),
        ["ZONED_DATETIME"] = Rejects("RETURN zoned_datetime(1)", "FUNCTION zoned_datetime"),
        ["ZONED_TIME"] = Rejects("RETURN zoned_time(1)", "FUNCTION zoned_time"),
        ["LOCAL_DATETIME"] = Rejects("RETURN local_datetime(1)", "FUNCTION local_datetime"),
        ["LOCALDATETIME"] = Rejects("RETURN localdatetime(1)", "FUNCTION localdatetime"),
        ["HEAD"] = Rejects("RETURN head(1)", "FUNCTION head"),
        ["LAST"] = Rejects("RETURN last(1)", "FUNCTION last"),
        ["TAIL"] = Rejects("RETURN tail(1)", "FUNCTION tail"),
        ["REVERSE"] = Rejects("RETURN reverse(1)", "FUNCTION reverse"),
        ["RANGE"] = Rejects("RETURN range(1)", "FUNCTION range"),
        ["ELEMENTS"] = Rejects("RETURN elements(1)", "FUNCTION elements"),
        ["CURRENT_TIME"] = Rejects("RETURN CURRENT_TIME", "CURRENT_TIME"),
        ["LOCAL_TIME"] = Rejects("RETURN LOCAL_TIME", "LOCAL_TIME"),
        ["LOCAL_TIMESTAMP"] = Rejects("RETURN LOCAL_TIMESTAMP", "LOCAL_TIMESTAMP"),
        // ISO/IEC 39075 label expressions (#1139 flipped the #1101 pins)
        ["|"] = Parses("MATCH (n:A|B) RETURN n"),
        ["&"] = Parses("MATCH (n:A&B) RETURN n"),
        ["!"] = Parses("MATCH (n:!A) RETURN n"),
        ["%"] = Parses("MATCH (n:%) RETURN n"),
        ["LABELED"] = Parses("MATCH (n) WHERE n IS NOT LABELED A RETURN n"),
    };

    private const string caseExpression = "MATCH (a) RETURN CASE WHEN a.x = 1 THEN 1 ELSE 2 END";

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Keyword disposition: every keyword and recognized-unsupported entry has a case")]
    public void Corpus_EveryKeywordAndTableEntry_ShouldHaveACase()
    {
        // Act / Assert
        RequireCoverage(Words(), _corpus);
        _corpus.Keys.Except(Words(), StringComparer.OrdinalIgnoreCase)
            .ShouldBeEmpty("Corpus keys must name current keywords, contextual prefixes or recognized-unsupported entries.");
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

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Keyword disposition: each word parses in a supported clause or reports one CK00001 naming its construct")]
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
            else if (errors.Length != 1 || errors[0].Code != "CK00001" ||
                     errors[0].Message?.Contains($"The {keywordCase.Construct} clause", StringComparison.Ordinal) != true)
            {
                failures.Add($"{word}: expected one CK00001 naming '{keywordCase.Construct}' for '{keywordCase.Gql}' but found {found}");
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
            .Where(word => word.Position == GqlWordPosition.BeforeDelete && keywords.Contains(word.Spelling))
            .Select(word => word.Spelling)
            .ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Keyword disposition: EXPLAIN is contextual and remains an unquoted identifier")]
    [InlineData("MATCH (explain) RETURN explain", false)]
    [InlineData("MATCH (n) RETURN n AS explain", false)]
    [InlineData("MATCH (n:explain) RETURN n", false)]
    [InlineData("MATCH (n) RETURN n.explain", false)]
    [InlineData("MATCH explain = (n) RETURN explain", false)]
    [InlineData("MATCH ()-[explain:explain]->() RETURN explain", false)]
    [InlineData("eXpLaIn MATCH (explain:explain) RETURN explain.explain AS explain", true)]
    public void Parse_ExplainIdentifier_ShouldRecognizeOnlyStatementPrefix(string source, bool explained)
    {
        // Arrange
        var parser = new GqlQueryParser();

        // Act
        var statement = (GqlQueryStatement)parser.Parse(source);

        // Assert
        GqlLanguageProfile.Instance.Keywords.ToArray().ShouldNotContain("EXPLAIN");
        statement.Diagnostics.ShouldBeEmpty();
        statement.GqlExpression.IsExplain.ShouldBe(explained);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Keyword disposition: contextual EXPLAIN still rejects nesting and options")]
    [InlineData("EXPLAIN EXPLAIN MATCH (n) RETURN n")]
    [InlineData("EXPLAIN ANALYZE MATCH (n) RETURN n")]
    [InlineData("EXPLAIN PROFILE MATCH (n) RETURN n")]
    [InlineData("EXPLAIN (FORMAT JSON) MATCH (n) RETURN n")]
    public void Parse_ExplainOptionsOrNesting_ShouldReportCodedRejection(string source)
    {
        // Arrange
        var parser = new GqlQueryParser();

        // Act
        var statement = parser.Parse(source);

        // Assert
        statement.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK00001");
    }

    private static void RequireCoverage(IEnumerable<string> words, IReadOnlyDictionary<string, KeywordCase> corpus)
        => words.Where(word => !corpus.ContainsKey(word))
            .ShouldBeEmpty("Every keyword and recognized-unsupported entry needs a keyword-disposition case.");

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Keyword disposition: unsupported function names remain identifiers outside calls")]
    public void Parse_UnsupportedFunctionNames_ShouldPreserveBindings()
    {
        // Arrange / Act / Assert
        foreach (var word in GqlUnsupportedVocabulary.Words.Where(word => word.Position == GqlWordPosition.FunctionCall))
        {
            string name = word.Spelling.ToLowerInvariant();
            new GqlQueryParser().Parse($"MATCH ({name}:{name}) RETURN {name}.{name} AS {name}").Diagnostics.ShouldBeEmpty();
            new GqlQueryParser().Parse($"LET {name}=1 RETURN {name}").Diagnostics.ShouldBeEmpty();
            new GqlQueryParser().Parse($"FOR {name} IN [1] RETURN {name}").Diagnostics.ShouldBeEmpty();
        }
    }

    private static string[] Words()
        => GqlLanguageProfile.Instance.Keywords.ToArray()
            .Concat(GqlUnsupportedVocabulary.Words.Select(word => word.Spelling))
            .Concat(GqlLabelVocabulary.Spellings)
            .Concat(GqlTransactionVocabulary.Spellings)
            .Concat(Enum.GetNames<GqlPathMode>())
            .Append("EXPLAIN") // Contextual statement prefix; identifier cases live in this corpus too.
            .Append("REDUCE") // Lexical binding form; not an ordinary scalar function.
            .Append("ON") // Contextual MERGE branch prefix; ordinary variable spelling stays valid.
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
