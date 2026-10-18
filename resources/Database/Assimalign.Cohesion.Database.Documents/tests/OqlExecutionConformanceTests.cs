using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Tests;

/// <summary>Measures every advertised OQL clause through a real document session.</summary>
public sealed class OqlExecutionConformanceTests
{
    private static readonly IReadOnlyDictionary<string, ExecutionCase[]> _cases =
        new Dictionary<string, ExecutionCase[]>(StringComparer.OrdinalIgnoreCase)
        {
            [OqlClauses.Explain] =
            [
                new("EXPLAIN SELECT name FROM items",
                    [[0, null, "Select", "items", null, "filter=False; grouped=False; order_keys=0", null, null],
                     [1, 0, "Project", "items", null, null, null, null],
                     [2, 1, "CollectionScan", "items", null, null, null, null]]),
            ],
            [OqlClauses.Insert] =
            [
                InsertCase(),
                new("INSERT INTO items (KEY, VALUE) VALUES ('object', {'absent': MISSING, 'null_value': NULL})", [[true, true]],
                    VerificationStatement: "SELECT absent IS MISSING AS absent_value, null_value IS NULL AS null_value FROM items WHERE META().id = 'object'",
                    ExpectedAffectedCount: 1),
            ],
            [OqlClauses.Upsert] =
            [
                UpsertCase(),
            ],
            [OqlClauses.Update] =
            [
                new("UPDATE items SET amount = amount + 1, details.score = 9 WHERE category = 'a'",
                    [["alpha", 3m, 9m], ["beta", 7m, 9m], ["delta", null, 9m], ["gamma", 4m, 2m]],
                    VerificationStatement: "SELECT name, amount, details.score AS score FROM items ORDER BY name", ExpectedAffectedCount: 3),
            ],
            [OqlClauses.Unset] =
            [
                new("UPDATE items UNSET tags WHERE name = 'delta'", [[true]],
                    VerificationStatement: "SELECT tags IS MISSING FROM items WHERE name = 'delta'", ExpectedAffectedCount: 1),
            ],
            [OqlClauses.Delete] =
            [
                new("DELETE FROM items WHERE category = 'a'", [["gamma"]],
                    VerificationStatement: "SELECT name FROM items", ExpectedAffectedCount: 3),
                new("DELETE FROM items", [], VerificationStatement: "SELECT name FROM items", ExpectedAffectedCount: 4),
                new("DELETE FROM items", [["by_score"]], ["CREATE INDEX by_score ON items (details.score)"],
                    "SELECT INDEX_NAME FROM COHESION_SCHEMA.INDEXES", ExpectedAffectedCount: 4),
            ],
            [OqlClauses.Values] =
            [
                InsertCase(),
            ],
            [OqlClauses.Select] =
            [
                new("SELECT d.name AS label, d.details.score + @increment AS adjusted, d.tags[0] AS tag FROM items AS d WHERE d.name = 'alpha'",
                    [["alpha", 3m, "blue"]], Parameters: new Dictionary<string, object?> { ["increment"] = 2 }),
                new("SELECT COUNT(*) AS total, COUNT(amount) AS present, SUM(amount) AS sum, AVG(amount) AS average, MIN(amount) AS minimum, MAX(amount) AS maximum FROM items",
                    [[4m, 3m, 12m, 4m, 2m, 6m]]),
                new("SELECT COUNT(*) AS total, SUM(amount) AS sum, AVG(amount) AS average, MIN(amount) AS minimum, MAX(amount) AS maximum FROM items WHERE amount > 100",
                    [[0m, null, null, null, null]]),
            ],
            [OqlClauses.Distinct] =
            [
                new("SELECT DISTINCT category FROM items ORDER BY category", [["a"], ["b"]]),
            ],
            [OqlClauses.SelectValue] =
            [
                new("SELECT VALUE name FROM items WHERE category = 'b'", [["gamma"]]),
            ],
            [OqlClauses.ObjectConstructor] =
            [
                new("SELECT VALUE {\"n\": name, \"t\": tags} FROM items WHERE name = 'alpha'", [[Json("{\"n\":\"alpha\",\"t\":[\"blue\"]}")]]),
            ],
            [OqlClauses.ArrayConstructor] =
            [
                new("SELECT [name, amount] AS pair FROM items WHERE name = 'beta'", [[Json("[\"beta\",6]")]]),
            ],
            [OqlClauses.From] =
            [
                new("SELECT d.name FROM items d", [["alpha"], ["beta"], ["gamma"], ["delta"]]),
                new("SELECT OBJECT_NAME FROM COHESION_SCHEMA.OBJECT_OWNERSHIP", [["items"]]),
            ],
            [OqlClauses.Where] =
            [
                new("SELECT name FROM items WHERE (amount >= @minimum AND amount < 6) OR amount IS NULL",
                    [["gamma"], ["delta"]], Parameters: new Dictionary<string, object?> { ["minimum"] = 3 }),
                new("SELECT name FROM items WHERE details.score IS NULL AND NOT (amount IS NOT NULL)", []),
                new("SELECT name FROM items WHERE details.score IS MISSING", [["delta"]]),
                new("SELECT name FROM items WHERE amount IS MISSING", []),
                new("SELECT name FROM items WHERE details.score IS NOT MISSING", [["alpha"], ["beta"], ["gamma"]]),
                new("SELECT name FROM items WHERE amount IS NOT VALUED", [["delta"]]),
                new("SELECT name FROM items WHERE details.score IS VALUED", [["alpha"], ["beta"], ["gamma"]]),
            ],
            [OqlClauses.In] =
            [
                new("SELECT name FROM items WHERE category IN ('b', 'z')", [["gamma"]]),
            ],
            [OqlClauses.Between] =
            [
                new("SELECT name FROM items WHERE amount NOT BETWEEN 3 AND 5", [["alpha"], ["beta"]]),
            ],
            [OqlClauses.Like] =
            [
                new("SELECT name FROM items WHERE name LIKE '%ta'", [["beta"], ["delta"]]),
            ],
            [OqlClauses.Case] =
            [
                new("SELECT name, CASE WHEN amount > 3 THEN 'big' WHEN amount IS NULL THEN 'none' ELSE 'small' END AS size FROM items",
                    [["alpha", "small"], ["beta", "big"], ["gamma", "big"], ["delta", "none"]]),
                new("SELECT CASE WHEN amount > 3 THEN 'big' ELSE 'small' END AS size, COUNT(*) AS n FROM items GROUP BY CASE WHEN amount > 3 THEN 'big' ELSE 'small' END",
                    [["big", 2m], ["small", 2m]]),
                new("SELECT SUM(CASE WHEN amount > 3 THEN 1 ELSE 0 END) AS big FROM items", [[2m]]),
            ],
            [OqlClauses.GroupBy] =
            [
                new("SELECT category, COUNT(*) AS total, COUNT(amount) AS present, SUM(amount) AS sum, AVG(amount) AS average, MIN(amount) AS minimum, MAX(amount) AS maximum FROM items GROUP BY category ORDER BY category",
                    [["a", 3m, 2m, 8m, 4m, 2m, 6m], ["b", 1m, 1m, 4m, 4m, 4m, 4m]]),
                new("SELECT category, tags[0] AS tag, COUNT(*) AS total FROM items GROUP BY category, tags[0] ORDER BY category, tag",
                    [["a", null, 1m], ["a", "blue", 1m], ["a", "green", 1m], ["b", "blue", 1m]], Missing: [(0, 1)]),
            ],
            [OqlClauses.Having] =
            [
                new("SELECT category, SUM(amount) AS total FROM items GROUP BY category HAVING SUM(amount) > 5", [["a", 8m]]),
                new("SELECT COUNT(*) AS total FROM items HAVING COUNT(*) > 3", [[4m]]),
                new("SELECT COUNT(*) AS total FROM items HAVING COUNT(*) > 4", []),
            ],
            [OqlClauses.OrderBy] =
            [
                new("SELECT name AS label, amount FROM items ORDER BY amount DESC, label ASC",
                    [["beta", 6m], ["gamma", 4m], ["alpha", 2m], ["delta", null]]),
            ],
            [OqlClauses.CreateCollection] =
            [
                new("CREATE COLLECTION extra", [["extra", "Adhoc"], ["items", "Adhoc"]],
                    VerificationStatement: "SELECT OBJECT_NAME, OWNER FROM COHESION_SCHEMA.OBJECT_OWNERSHIP ORDER BY OBJECT_NAME"),
                new("CREATE COLLECTION IF NOT EXISTS extra", [["extra", "Adhoc"], ["items", "Adhoc"]],
                    VerificationStatement: "SELECT OBJECT_NAME, OWNER FROM COHESION_SCHEMA.OBJECT_OWNERSHIP ORDER BY OBJECT_NAME"),
            ],
            [OqlClauses.DropCollection] =
            [
                new("DROP COLLECTION items", [],
                    VerificationStatement: "SELECT OBJECT_NAME, OWNER FROM COHESION_SCHEMA.OBJECT_OWNERSHIP ORDER BY OBJECT_NAME"),
                new("DROP COLLECTION IF EXISTS items", [],
                    VerificationStatement: "SELECT OBJECT_NAME, OWNER FROM COHESION_SCHEMA.OBJECT_OWNERSHIP ORDER BY OBJECT_NAME"),
            ],
            [OqlClauses.CreateIndex] =
            [
                new("CREATE INDEX by_score ON items (details.score)", [["by_score", "details.score", false]],
                    VerificationStatement: "SELECT INDEX_NAME, PATH, IS_UNIQUE FROM COHESION_SCHEMA.INDEXES"),
                new("CREATE INDEX by_tag ON items (tags[0])", [["by_tag", "tags[0]", false]],
                    VerificationStatement: "SELECT INDEX_NAME, PATH, IS_UNIQUE FROM COHESION_SCHEMA.INDEXES"),
            ],
            [OqlClauses.DropIndex] =
            [
                new("DROP INDEX by_score ON items", [], ["CREATE INDEX by_score ON items (details.score)"],
                    "SELECT INDEX_NAME FROM COHESION_SCHEMA.INDEXES"),
            ],
        };

    /// <summary>Fails on an unmapped capability and executes every mapped representative with result assertions.</summary>
    [Fact(DisplayName = "Cohesion Test [Documents] - Profile: Every advertised OQL clause executes")]
    public async Task Profile_AdvertisedClauses_AllExecuteAsync()
    {
        // Arrange: the profile drives enumeration; the case table cannot omit a new capability.
        var profile = OqlLanguageProfile.Instance;
        VerifyCoverage(profile);
        foreach (string clause in profile.Clauses)
        {
            foreach (var execution in _cases[clause])
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var cancellationToken = timeout.Token;
                string context = $"OQL clause '{clause}': {execution.Statement}";
                var parsed = new OqlQueryParser().Parse(execution.Statement).ShouldBeOfType<OqlQueryStatement>();
                parsed.Diagnostics.ShouldBeEmpty(context);
                ContainsClause(parsed.OqlExpression, clause).ShouldBeTrue(context);

                await using var engine = DocumentDatabaseEngine.Create("document-engine", new());
                var database = await engine.CreateDatabaseAsync("conformance", cancellationToken);
                await using var session = await database.CreateSessionAsync(cancellationToken);
                var collection = await session.CreateCollectionAsync("items", cancellationToken);
                await SeedAsync(collection, session, cancellationToken);
                foreach (string setup in execution.Setup ?? [])
                {
                    (await session.ExecuteAsync(setup, cancellationToken: cancellationToken)).Status.ShouldBe(DatabaseResultStatus.Success, context);
                }

                // Act: text takes the production parse, planner, transaction and executor path.
                var result = await session.ExecuteAsync(execution.Statement, execution.Parameters, cancellationToken);

                // Assert: DDL must change catalog state; queries must return the exact values and order.
                result.Status.ShouldBe(DatabaseResultStatus.Success, context);
                if (execution.VerificationStatement is string verification)
                {
                    result.AffectedCount.ShouldBe(execution.ExpectedAffectedCount, context);
                    result = await session.ExecuteAsync(verification, cancellationToken: cancellationToken);
                }
                await VerifyRowsAsync(result, execution.ExpectedRows, context, cancellationToken, execution.Missing);
            }
        }
    }

    /// <summary>Proves that advertising a clause without an execution case fails the coverage guard.</summary>
    [Fact(DisplayName = "Cohesion Test [Documents] - Profile: Added clause without execution evidence fails")]
    public void Profile_AddedClauseWithoutCase_FailsCoverage()
    {
        var profile = OqlLanguageProfile.Instance;
        var extended = new QueryLanguageProfile(profile.Language, profile.Keywords.ToArray(), profile.Functions.ToArray(),
            [.. profile.Clauses, "UNMEASURED CLAUSE"]);

        var failure = Should.Throw<ShouldAssertException>(() => VerifyCoverage(extended));

        failure.Message.ShouldContain("UNMEASURED CLAUSE", Case.Sensitive);
    }

    /// <summary>Distinguishes intentional semantic validation from an unimplemented executor path.</summary>
    /// <param name="statement">The syntactically accepted but semantically invalid statement.</param>
    /// <param name="message">The exact intended planning error.</param>
    [Theory(DisplayName = "Cohesion Test [Documents] - Profile: Invalid aggregate contexts return intended errors")]
    [InlineData("SELECT amount, SUM(amount) FROM items", "An OQL grouped query may only read grouping expressions outside aggregate calls.")]
    [InlineData("SELECT name FROM items HAVING name = 'alpha'", "OQL HAVING requires grouping or an aggregate.")]
    [InlineData("SELECT name FROM items WHERE COUNT(*) > 1", "The expression is not valid in this OQL planning context.")]
    [InlineData("SELECT SUM(COUNT(*)) FROM items", "The expression is not valid in this OQL planning context.")]
    public async Task Execute_InvalidAggregateContext_ReturnsIntendedErrorAsync(string statement, string message)
    {
        new OqlQueryParser().Parse(statement).Diagnostics.ShouldBeEmpty();
        await using var engine = DocumentDatabaseEngine.Create("document-engine", new());
        var database = await engine.CreateDatabaseAsync("conformance", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);

        var exception = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(statement, cancellationToken: CancellationToken.None));

        exception.Message.ShouldBe(message);
    }

    private static readonly IReadOnlyDictionary<string, FunctionCase> _functionCases =
        new Dictionary<string, FunctionCase>(StringComparer.OrdinalIgnoreCase)
        {
            ["META"] = new("META().id", [["a"], ["b"], ["c"], ["d"]]),
            ["COUNT"] = new("COUNT(*)", [[4m]]),
            ["SUM"] = new("SUM(amount)", [[12m]]),
            ["AVG"] = new("AVG(amount)", [[4m]]),
            ["MIN"] = new("MIN(amount)", [[2m]]),
            ["MAX"] = new("MAX(amount)", [[6m]]),
        };

    [Fact(DisplayName = "Cohesion Test [Documents] - Functions: every declaration has live execution evidence")]
    public async Task Profile_DeclaredFunctions_ShouldExecuteEveryCaseAsync()
    {
        // Arrange
        RequireFunctionCoverage(OqlLanguageProfile.Instance.Functions.ToArray(), _functionCases.Keys);
        await using var engine = DocumentDatabaseEngine.Create("document-functions", new());
        var database = await engine.CreateDatabaseAsync("functions", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await SeedAsync(collection, session, CancellationToken.None);

        // Act / Assert
        foreach (string name in OqlLanguageProfile.Instance.Functions.ToArray())
        {
            var execution = _functionCases[name];
            var result = await session.ExecuteAsync($"SELECT {execution.Expression} FROM items", cancellationToken: CancellationToken.None);
            await VerifyRowsAsync(result, execution.ExpectedRows, name, CancellationToken.None);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Functions: coverage rejects missing evidence and retired cases")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Profile_FunctionCoverageMismatch_ShouldFail(int mismatch)
    {
        // Arrange
        var declarations = OqlLanguageProfile.Instance.Functions.ToArray();
        // Act / Assert
        Should.Throw<ShouldAssertException>(() => RequireFunctionCoverage(
            mismatch == 0 ? declarations.Append("FUTURE") : mismatch == 2 ? declarations.Where(name => name != "COUNT") : declarations,
            mismatch == 1 ? _functionCases.Keys.Append("RETIRED") : _functionCases.Keys));
    }

    private static void RequireFunctionCoverage(IEnumerable<string> declarations, IEnumerable<string> cases)
    {
        declarations.Except(cases, StringComparer.OrdinalIgnoreCase).ShouldBeEmpty();
        cases.Except(declarations, StringComparer.OrdinalIgnoreCase).ShouldBeEmpty();
    }

    private static JsonElement Json(string text)
    {
        using var json = JsonDocument.Parse(text);
        return json.RootElement.Clone();
    }

    private sealed record FunctionCase(string Expression, object?[][] ExpectedRows);

    private static void VerifyCoverage(QueryLanguageProfile profile)
    {
        profile.Clauses.ShouldNotBeEmpty();
        foreach (string clause in profile.Clauses)
        {
            _cases.TryGetValue(clause, out var executions).ShouldBeTrue($"Advertised OQL clause '{clause}' has no execution case.");
            executions.ShouldNotBeNull().ShouldNotBeEmpty($"Advertised OQL clause '{clause}' has no execution case.");
        }
        _cases.Keys.Except(profile.Clauses, StringComparer.OrdinalIgnoreCase).ShouldBeEmpty("Retired capabilities must not remain positive conformance cases.");
    }

    private static bool ContainsClause(OqlExpression expression, string clause) => clause switch
    {
        OqlClauses.Explain => expression is OqlExplainExpression,
        OqlClauses.Insert => expression is OqlInsertExpression,
        OqlClauses.Upsert => expression is OqlUpsertExpression,
        OqlClauses.Delete => expression is OqlDeleteExpression,
        OqlClauses.Update => expression is OqlUpdateExpression,
        OqlClauses.Unset => expression is OqlUpdateExpression { Unset.Count: > 0 },
        OqlClauses.Values => expression is OqlInsertExpression { Values.Count: > 0 },
        OqlClauses.Select => expression is OqlSelectExpression { Projections.Count: > 0 },
        OqlClauses.Distinct => expression is OqlSelectExpression { IsDistinct: true },
        OqlClauses.SelectValue => expression is OqlSelectExpression { IsValue: true },
        OqlClauses.ObjectConstructor => expression is OqlSelectExpression objectQuery && objectQuery.Projections.Any(p => p.Expression is OqlObjectExpression),
        OqlClauses.ArrayConstructor => expression is OqlSelectExpression arrayQuery && arrayQuery.Projections.Any(p => p.Expression is OqlArrayExpression),
        OqlClauses.From => expression is OqlSelectExpression { Collection.Length: > 0 },
        OqlClauses.Where => expression is OqlSelectExpression { Predicate: not null },
        OqlClauses.In => expression is OqlSelectExpression { Predicate: OqlInExpression },
        OqlClauses.Between => expression is OqlSelectExpression { Predicate: OqlBetweenExpression },
        OqlClauses.Like => expression is OqlSelectExpression { Predicate: OqlLikeExpression },
        OqlClauses.Case => expression is OqlSelectExpression select && select.Projections.Any(projection => ContainsCase(projection.Expression)),
        OqlClauses.GroupBy => expression is OqlSelectExpression { GroupBy.Count: > 0 },
        OqlClauses.Having => expression is OqlSelectExpression { Having: not null },
        OqlClauses.OrderBy => expression is OqlSelectExpression { OrderBy.Count: > 0 },
        OqlClauses.CreateCollection => expression is OqlCreateCollectionExpression,
        OqlClauses.DropCollection => expression is OqlDropCollectionExpression,
        OqlClauses.CreateIndex => expression is OqlCreateIndexExpression,
        OqlClauses.DropIndex => expression is OqlDropIndexExpression,
        _ => false,
    };

    private static bool ContainsCase(OqlExpression expression) => expression is OqlCaseExpression ||
        expression is OqlCallExpression call && call.Arguments.Any(ContainsCase);

    private static async Task SeedAsync(DocumentCollection collection, DocumentDatabaseSession session, CancellationToken cancellationToken)
    {
        (string Id, string Json)[] documents =
        [
            ("a", """{"name":"alpha","category":"a","amount":2,"details":{"score":1},"tags":["blue"]}"""),
            ("b", """{"name":"beta","category":"a","amount":6,"details":{"score":3},"tags":["green"]}"""),
            ("c", """{"name":"gamma","category":"b","amount":4,"details":{"score":2},"tags":["blue"]}"""),
            ("d", """{"name":"delta","category":"a","amount":null,"tags":[]}"""),
        ];
        foreach (var (id, json) in documents)
        {
            await collection.PutAsync(session, id, Encoding.UTF8.GetBytes(json), cancellationToken: cancellationToken);
        }
    }

    private static async Task VerifyRowsAsync(DatabaseResult result, object?[][] expected, string context, CancellationToken cancellationToken,
        (int Row, int Column)[]? missing = null)
    {
        await using var rows = result.ShouldBeAssignableTo<DatabaseResultSet>(context);
        var actual = new List<object?[]>();
        await foreach (var row in rows.GetRowsAsync(cancellationToken))
        {
            for (int ordinal = 0; ordinal < row.FieldCount; ordinal++)
            {
                // MISSING is an optional row capability (#1126): a row that cannot report it, such as
                // an EXPLAIN description row (#1104), has every field present.
                bool isMissing = row.SupportsMissing && row.IsMissing(ordinal);
                isMissing.ShouldBe(missing?.Contains((actual.Count, ordinal)) ?? false, context);
            }
            actual.Add(Enumerable.Range(0, rows.Columns.Count).Select(row.GetValue).ToArray());
        }
        actual.Count.ShouldBe(expected.Length, context);
        for (int index = 0; index < expected.Length; index++)
        {
            actual[index].Length.ShouldBe(expected[index].Length, context);
            for (int ordinal = 0; ordinal < expected[index].Length; ordinal++)
            {
                if (expected[index][ordinal] is JsonElement json)
                {
                    JsonElement.DeepEquals(actual[index][ordinal].ShouldBeOfType<JsonElement>(), json).ShouldBeTrue(context);
                }
                else { actual[index][ordinal].ShouldBe(expected[index][ordinal], context); }
            }
        }
    }

    private sealed record ExecutionCase(string Statement, object?[][] ExpectedRows,
        string[]? Setup = null, string? VerificationStatement = null, (int Row, int Column)[]? Missing = null,
        long ExpectedAffectedCount = 0, IReadOnlyDictionary<string, object?>? Parameters = null);

    private static ExecutionCase UpsertCase()
    {
        using var replacement = JsonDocument.Parse("""{"name":"alpha2","category":"a"}""");
        using var added = JsonDocument.Parse("""{"name":"zeta","category":"z"}""");
        return new("UPSERT INTO items (KEY, VALUE) VALUES ('a', $replacement), ('z', $new)",
            [["a", "alpha2", null], ["b", "beta", 6m], ["c", "gamma", 4m], ["d", "delta", null], ["z", "zeta", null]],
            VerificationStatement: "SELECT META(d).id AS id, d.name AS name, d.amount AS amount FROM items d ORDER BY id",
            Missing: [(0, 2), (4, 2)], ExpectedAffectedCount: 2,
            Parameters: new Dictionary<string, object?> { ["replacement"] = replacement.RootElement.Clone(), ["new"] = added.RootElement.Clone() });
    }

    private static ExecutionCase InsertCase()
    {
        using var e = JsonDocument.Parse("""{"name":"epsilon","category":"z"}""");
        using var f = JsonDocument.Parse("""{"name":"phi","category":"z"}""");
        return new("INSERT INTO items (KEY, VALUE) VALUES ('e', $e), ('f', $f)", [["e", "epsilon"], ["f", "phi"]],
            VerificationStatement: "SELECT META(d).id AS id, d.name AS name FROM items d WHERE d.category = 'z' ORDER BY id",
            ExpectedAffectedCount: 2, Parameters: new Dictionary<string, object?> { ["e"] = e.RootElement.Clone(), ["f"] = f.RootElement.Clone() });
    }
}
