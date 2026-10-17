using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
            [OqlClauses.Select] =
            [
                new("SELECT d.name AS label, d.details.score + @increment AS adjusted, d.tags[0] AS tag FROM items AS d WHERE d.name = 'alpha'",
                    [["alpha", 3m, "blue"]]),
                new("SELECT COUNT(*) AS total, COUNT(amount) AS present, SUM(amount) AS sum, AVG(amount) AS average, MIN(amount) AS minimum, MAX(amount) AS maximum FROM items",
                    [[4m, 3m, 12m, 4m, 2m, 6m]]),
                new("SELECT COUNT(*) AS total, SUM(amount) AS sum, AVG(amount) AS average, MIN(amount) AS minimum, MAX(amount) AS maximum FROM items WHERE amount > 100",
                    [[0m, null, null, null, null]]),
            ],
            [OqlClauses.From] =
            [
                new("SELECT d.name FROM items d", [["alpha"], ["beta"], ["gamma"], ["delta"]]),
                new("SELECT OBJECT_NAME FROM COHESION_SCHEMA.OBJECT_OWNERSHIP", [["items"]]),
            ],
            [OqlClauses.Where] =
            [
                new("SELECT name FROM items WHERE (amount >= @minimum AND amount < 6) OR amount IS NULL",
                    [["gamma"], ["delta"]]),
                new("SELECT name FROM items WHERE details.score IS NULL AND NOT (amount IS NOT NULL)", [["delta"]]),
            ],
            [OqlClauses.GroupBy] =
            [
                new("SELECT category, COUNT(*) AS total, COUNT(amount) AS present, SUM(amount) AS sum, AVG(amount) AS average, MIN(amount) AS minimum, MAX(amount) AS maximum FROM items GROUP BY category ORDER BY category",
                    [["a", 3m, 2m, 8m, 4m, 2m, 6m], ["b", 1m, 1m, 4m, 4m, 4m, 4m]]),
                new("SELECT category, tags[0] AS tag, COUNT(*) AS total FROM items GROUP BY category, tags[0] ORDER BY category, tag",
                    [["a", null, 1m], ["a", "blue", 1m], ["a", "green", 1m], ["b", "blue", 1m]]),
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = timeout.Token;

        foreach (string clause in profile.Clauses)
        {
            foreach (var execution in _cases[clause])
            {
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
                var parameters = new Dictionary<string, object?> { ["increment"] = 2, ["minimum"] = 3 };
                var result = await session.ExecuteAsync(execution.Statement, parameters, cancellationToken);

                // Assert: DDL must change catalog state; queries must return the exact values and order.
                result.Status.ShouldBe(DatabaseResultStatus.Success, context);
                if (execution.VerificationStatement is string verification)
                {
                    result.AffectedCount.ShouldBe(0L, context);
                    result = await session.ExecuteAsync(verification, cancellationToken: cancellationToken);
                }
                await VerifyRowsAsync(result, execution.ExpectedRows, context, cancellationToken);
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

    // #1126 deletes this exact temporary ODMG list when SQL++ replaces those declarations.
    private static readonly string[] _functionExemptions = ["ELEMENT", "FLATTEN", "FIRST", "LAST", "LISTTOSET", "UNIQUE"];
    private static readonly IReadOnlyDictionary<string, FunctionCase> _functionCases =
        new Dictionary<string, FunctionCase>(StringComparer.OrdinalIgnoreCase)
        {
            ["COUNT"] = new("COUNT(*)", 4m),
            ["SUM"] = new("SUM(amount)", 12m),
            ["AVG"] = new("AVG(amount)", 4m),
            ["MIN"] = new("MIN(amount)", 2m),
            ["MAX"] = new("MAX(amount)", 6m),
        };

    [Fact(DisplayName = "Cohesion Test [Documents] - Functions: declarations have live evidence or the exact temporary ODMG pin")]
    public async Task Profile_DeclaredFunctions_ShouldExecuteEveryCaseAsync()
    {
        // Arrange
        RequireFunctionCoverage(OqlLanguageProfile.Instance.Functions.ToArray(), _functionCases.Keys, _functionExemptions);
        await using var engine = DocumentDatabaseEngine.Create("document-functions", new());
        var database = await engine.CreateDatabaseAsync("functions", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await SeedAsync(collection, session, CancellationToken.None);

        // Act / Assert
        foreach (string name in OqlLanguageProfile.Instance.Functions.ToArray().Except(_functionExemptions))
        {
            var execution = _functionCases[name];
            var result = await session.ExecuteAsync($"SELECT {execution.Expression} FROM items", cancellationToken: CancellationToken.None);
            await VerifyRowsAsync(result, [[execution.Expected]], name, CancellationToken.None);
        }
        foreach (string name in _functionExemptions)
        {
            var errors = new OqlQueryParser().Parse($"SELECT {name}(amount) FROM items").Diagnostics.ToArray();
            errors.ShouldHaveSingleItem().Code.ShouldBe("COHDBL001");
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Functions: coverage rejects missing evidence, retired cases and additional exemptions")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Profile_FunctionCoverageMismatch_ShouldFail(int mismatch)
    {
        // Arrange
        var declarations = OqlLanguageProfile.Instance.Functions.ToArray();
        // Act / Assert
        Should.Throw<ShouldAssertException>(() => RequireFunctionCoverage(
            mismatch == 0 ? declarations.Append("FUTURE") : declarations,
            mismatch == 1 ? _functionCases.Keys.Append("RETIRED") : _functionCases.Keys,
            mismatch == 2 ? _functionExemptions.Append("ABS") : _functionExemptions));
    }

    private static void RequireFunctionCoverage(IEnumerable<string> declarations, IEnumerable<string> cases, IEnumerable<string> exemptions)
    {
        exemptions.Order().ShouldBe(new[] { "ELEMENT", "FLATTEN", "FIRST", "LAST", "LISTTOSET", "UNIQUE" }.Order());
        exemptions.Except(declarations, StringComparer.OrdinalIgnoreCase).ShouldBeEmpty();
        declarations.Except(exemptions, StringComparer.OrdinalIgnoreCase).Except(cases, StringComparer.OrdinalIgnoreCase).ShouldBeEmpty();
        cases.Except(declarations, StringComparer.OrdinalIgnoreCase).ShouldBeEmpty();
        cases.Intersect(exemptions, StringComparer.OrdinalIgnoreCase).ShouldBeEmpty();
    }

    private sealed record FunctionCase(string Expression, object Expected);

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
        OqlClauses.Select => expression is OqlSelectExpression { Projections.Count: > 0 },
        OqlClauses.From => expression is OqlSelectExpression { Collection.Length: > 0 },
        OqlClauses.Where => expression is OqlSelectExpression { Predicate: not null },
        OqlClauses.GroupBy => expression is OqlSelectExpression { GroupBy.Count: > 0 },
        OqlClauses.Having => expression is OqlSelectExpression { Having: not null },
        OqlClauses.OrderBy => expression is OqlSelectExpression { OrderBy.Count: > 0 },
        OqlClauses.CreateCollection => expression is OqlCreateCollectionExpression,
        OqlClauses.DropCollection => expression is OqlDropCollectionExpression,
        OqlClauses.CreateIndex => expression is OqlCreateIndexExpression,
        OqlClauses.DropIndex => expression is OqlDropIndexExpression,
        _ => false,
    };

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

    private static async Task VerifyRowsAsync(DatabaseResult result, object?[][] expected, string context, CancellationToken cancellationToken)
    {
        await using var rows = result.ShouldBeAssignableTo<DatabaseResultSet>(context);
        var actual = new List<object?[]>();
        await foreach (var row in rows.GetRowsAsync(cancellationToken))
        {
            actual.Add(Enumerable.Range(0, rows.Columns.Count).Select(row.GetValue).ToArray());
        }
        actual.Count.ShouldBe(expected.Length, context);
        for (int index = 0; index < expected.Length; index++)
        {
            actual[index].ShouldBe(expected[index], context);
        }
    }

    private sealed record ExecutionCase(string Statement, object?[][] ExpectedRows,
        string[]? Setup = null, string? VerificationStatement = null);
}
