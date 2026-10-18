using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Tests;

/// <summary>Measures each advertised GQL clause against a running graph engine.</summary>
public sealed class GqlProfileExecutionTests
{
    private static readonly Guid _personLabelId = new("13333333-3333-3333-3333-333333333333");
    private static readonly Guid _robotLabelId = new("24444444-4444-4444-4444-444444444444");
    // Node identities follow insertion order: Alice, Bob, Isolated, Robo, then the unlabeled Ghost.
    private const string seed = "INSERT (a:Person {name: 'Alice', age: 42, active: TRUE})-[r:KNOWS {weight: 2}]->(b:Person {name: 'Bob', age: 17, active: FALSE}), " +
        "(:Person {name: 'Isolated', age: 30}), (:Robot {name: 'Robo'})-[:SERVES]->({name: 'Ghost'})";

    // The profile drives execution. A profile addition without data here fails before any query runs.
    private static readonly IReadOnlyDictionary<string, ExecutionCase[]> _cases =
        new Dictionary<string, ExecutionCase[]>(StringComparer.OrdinalIgnoreCase)
        {
            [GqlClauses.Explain] =
            [
                new("EXPLAIN MATCH (a:Person) RETURN a",
                    [[0, null, "Match", null, null, "filter=False; matches_nothing=False", null, null],
                     [1, 0, "ExpandPath", null, null, null, null, null],
                     [2, 1, "LabelScan", "Person", null, "node=0; property=; value=NULL", null, null],
                     [3, 0, "Project", "a", null, "kind=Node; expression=a", null, null]]),
            ],
            [GqlClauses.Match] =
            [
                new("MATCH (a:Person) MATCH (a)-[:KNOWS]->(b) RETURN a.name, b.name", [["Alice", "Bob"]]),
                new("MATCH (a)-[r:KNOWS]->(b) MATCH (a)-[s:KNOWS]->(b) RETURN r.weight, s.weight", [[2L, 2L]]),
                new("MATCH (a:Person)-[:KNOWS]->(b) WHERE b.age > 50 RETURN a.name", []),
                new("MATCH (a:Person {name: $name}) RETURN a.name", [["Alice"]],
                    Parameters: new Dictionary<string, object?> { ["name"] = "Alice" }),
                new("MATCH (a:Person {name: 'Alice'})-[r:KNOWS]->(b) RETURN b.name, r.weight", [["Bob", 2L]]),
                new("MATCH (b:Person {name: 'Bob'})<-[r:KNOWS]-(a) RETURN a.name", [["Alice"]]),
                new("MATCH (b:Person {name: 'Bob'})-[r:KNOWS]-(a) RETURN a.name", [["Alice"]]),
                // ISO/IEC 39075 left-or-right and the abbreviated edges (#1139).
                new("MATCH (b:Person {name: 'Bob'})<-[r:KNOWS]->(a) RETURN a.name", [["Alice"]]),
                new("MATCH (a:Person {name: 'Alice'})->(b) RETURN b.name", [["Bob"]]),
                new("MATCH (b:Person {name: 'Bob'})<-(a) RETURN a.name", [["Alice"]]),
                new("MATCH (b:Person {name: 'Bob'})-(a) RETURN a.name", [["Alice"]]),
                new("MATCH (a:Person {name: 'Alice'})<->(b) RETURN b.name", [["Bob"]]),
            ],
            [GqlClauses.OptionalMatch] =
            [
                new("MATCH (a:Person) OPTIONAL MATCH (a)-[:KNOWS]->(b) RETURN a.name, b.name",
                    [["Alice", "Bob"], ["Bob", null], ["Isolated", null]]),
                new("MATCH (a:Person) OPTIONAL MATCH (a)-[:KNOWS]->(b) WHERE b.age > 50 RETURN a.name, b.name",
                    [["Alice", null], ["Bob", null], ["Isolated", null]]),
                new("OPTIONAL MATCH (n:Person {name: 'Nobody'}) RETURN n.name", [[null]]),
                new("MATCH (a:Person) OPTIONAL MATCH (a)-[:KNOWS]->(b) OPTIONAL MATCH (a)<-[:KNOWS]-(c) RETURN a.name, b.name, c.name",
                    [["Alice", "Bob", null], ["Bob", null, "Alice"], ["Isolated", null, null]]),
                new("MATCH (a:Person) OPTIONAL MATCH (a)-[:KNOWS]->(b) OPTIONAL MATCH (b)-[:KNOWS]->(c) RETURN a.name, b.name, c.name",
                    [["Alice", "Bob", null], ["Bob", null, null], ["Isolated", null, null]]),
                new("MATCH (a:Person) OPTIONAL MATCH (a)-[:KNOWS]->(b) MATCH (b)-[:KNOWS]->(c) RETURN a.name", []),
                new("MATCH (a:Person)-[r:KNOWS]->(b) OPTIONAL MATCH (a)-[s:KNOWS]->(b) RETURN r.weight, s.weight", [[2L, 2L]]),
                new("MATCH (a:Person {name:'Alice'}) OPTIONAL MATCH (a)-[r:KNOWS]->(b), (b)-[s:KNOWS]->(c) RETURN a.name, b.name, r.weight, c.name",
                    [["Alice", null, null, null]]),
                new("MATCH (a:Person) OPTIONAL MATCH (a) WHERE FALSE RETURN a.name", [["Alice"], ["Bob"], ["Isolated"]]),
                new("MATCH (a:Person) OPTIONAL MATCH (a)-[:KNOWS]->(b {name: $name}) RETURN a.name, b.name",
                    [["Alice", "Bob"], ["Bob", null], ["Isolated", null]], Parameters: new Dictionary<string, object?> { ["name"] = "Bob" }),
                new("MATCH (a:Person) OPTIONAL MATCH (a)-[:KNOWS]->(b) RETURN a.name, b IS NULL, b:Person, b.age + 1",
                    [["Alice", false, true, 18L], ["Bob", true, null, null], ["Isolated", true, null, null]]),
                Mutation("MATCH (a:Person) OPTIONAL MATCH (a)-[r:KNOWS]->(b) DELETE r", 1,
                    new ExecutionCase("MATCH ()-[r:KNOWS]->() RETURN r.weight", [])),
            ],
            [GqlClauses.Case] =
            [
                new("MATCH (a:Person {name: 'Alice'}) RETURN CASE WHEN a.age = 42 THEN a.name ELSE 'other' END", [["Alice"]]),
                new("RETURN CASE 1 WHEN 1.0 THEN 'match' ELSE 'other' END", [["match"]]),
                new("RETURN COALESCE(NULL, 5), NULLIF(42, 42)", [[5L, null]]),
            ],
            [GqlClauses.Return] =
            [
                new("RETURN [10,20,30][-1], {k:{j:1}}.k.j, [1] IN [[1]], [NULL] = [NULL]", [[30L, 1L, true, null]]),
                new("MATCH (a:Person) WHERE a.age IN $ages RETURN a.name", [["Alice"], ["Bob"]],
                    Parameters: new Dictionary<string, object?> { ["ages"] = new List<long> { 17, 42 } }),
                new("MATCH (a:Person {name: 'Alice'}) RETURN a.name AS person, a.age, a.absent", [["Alice", 42L, null]]),
                new("MATCH (a:Person), (b:Person) RETURN DISTINCT a.active", [[true], [false], [null]], Columns: ["a.active"]),
                new("MATCH (a:Person), (b:Person) RETURN ALL a.active",
                    [[true], [true], [true], [false], [false], [false], [null], [null], [null]]),
                new("MATCH (a:Person {name: 'Alice'}) RETURN a.age + 1 AS next, a.name || '!'", [[43L, "Alice!"]],
                    Columns: ["next", "a.name || '!'"]),
                new("RETURN 1 + 1 AS two", [[2L]], Columns: ["two"]),
                new("MATCH (a:Person {name: 'Alice'}) RETURN a IS LABELED Person, a.active IS TRUE, a.absent IS NULL",
                    [[true, true, true]]),
                new("MATCH (a:Person) RETURN $p AS p", [[5], [5], [5]], Parameters: new Dictionary<string, object?> { ["p"] = 5 }),
                new("MATCH (a:Person {name: 'Alice'}) RETURN a.age + $d AS next", [[43L]],
                    Parameters: new Dictionary<string, object?> { ["d"] = 1 }, Columns: ["next"]),
                new("RETURN $p AS p", [[5]], Parameters: new Dictionary<string, object?> { ["p"] = 5 }),
                new("MATCH (a) RETURN $p", [[5], [5], [5], [5], [5]],
                    Parameters: new Dictionary<string, object?> { ["p"] = 5 }, Columns: ["$p"]),
            ],
            [GqlClauses.Next] =
            [
                new("LET x = $value RETURN x NEXT RETURN x + 1 AS y", [[6L]], Parameters: new Dictionary<string, object?> { ["value"] = 5L }),
            ],
            [GqlClauses.With] =
            [
                new("MATCH (a:Person) WITH a WHERE a.age > $age RETURN a.name ORDER BY a.name", [["Alice"], ["Isolated"]], Parameters: new Dictionary<string, object?> { ["age"] = 18L }),
                new("MATCH (a:Person) WITH count(a) AS c RETURN c", [[3L]]),
                new("MATCH (a:Person) WITH DISTINCT a.active AS act RETURN act", [[true], [false], [null]]),
                new("MATCH (a)-[r:KNOWS]->(b) WITH * RETURN b.name", [["Bob"]]),
            ],
            [GqlClauses.Let] =
            [
                new("LET x = $value RETURN x", [[5L]], Parameters: new Dictionary<string, object?> { ["value"] = 5L }),
            ],
            [GqlClauses.Filter] =
            [
                new("MATCH (a:Person) FILTER a.age > $age RETURN a.name", [["Alice"], ["Isolated"]], Parameters: new Dictionary<string, object?> { ["age"] = 18L }),
            ],
            [GqlClauses.For] =
            [
                new("FOR x IN $xs RETURN x", [[1L], [2L]], Parameters: new Dictionary<string, object?> { ["xs"] = new object?[] { 1L, 2L } }),
            ],
            [GqlClauses.Unwind] =
            [
                new("UNWIND $xs AS x RETURN x", [[1L], [2L]], Parameters: new Dictionary<string, object?> { ["xs"] = new List<long> { 1, 2 } }),
            ],
            [GqlClauses.GroupBy] =
            [
                new("MATCH (a:Person) RETURN a.active, count(*) AS n GROUP BY a.active ORDER BY n DESC",
                    [[true, 1L], [false, 1L], [null, 1L]]),
            ],
            [GqlClauses.OrderBy] =
            [
                new("MATCH (a:Person) RETURN a.name ORDER BY a.age", [["Bob"], ["Isolated"], ["Alice"]]),
            ],
            [GqlClauses.Offset] =
            [
                new("MATCH (a:Person) RETURN a.name ORDER BY a.name OFFSET 1", [["Bob"], ["Isolated"]]),
            ],
            [GqlClauses.Skip] =
            [
                new("MATCH (a:Person) RETURN a.name ORDER BY a.name SKIP 1", [["Bob"], ["Isolated"]]),
            ],
            [GqlClauses.Limit] =
            [
                new("MATCH (a:Person) RETURN a.name ORDER BY a.name LIMIT 1", [["Alice"]]),
            ],
            [GqlClauses.Create] =
            [
                new("CREATE (:Person {name: $name})", [], 1,
                    [new("MATCH (a:Person {name: 'Cara'}) RETURN a.name", [["Cara"]])],
                    new Dictionary<string, object?> { ["name"] = "Cara" }),
                Mutation("CREATE (:Person {name: 'Cara'})", 1,
                    new ExecutionCase("MATCH (a:Person {name: 'Cara'}) RETURN a.name", [["Cara"]])),
                Mutation("MATCH (a:Person {name: 'Bob'}), (b:Person {name: 'Isolated'}) CREATE (a)-[:KNOWS {weight: 3}]->(b)", 1,
                    new ExecutionCase("MATCH (a:Person {name: 'Bob'})-[r:KNOWS]->(b) RETURN b.name, r.weight", [["Isolated", 3L]])),
            ],
            [GqlClauses.Insert] =
            [
                new("INSERT (:Person {name: $name})", [], 1,
                    [new("MATCH (a:Person {name: 'Cara'}) RETURN a.name", [["Cara"]])],
                    new Dictionary<string, object?> { ["name"] = "Cara" }),
                Mutation("INSERT (:Person {name: 'Cara'})", 1,
                    new ExecutionCase("MATCH (a:Person {name: 'Cara'}) RETURN a.name", [["Cara"]])),
                Mutation("MATCH (a:Person {name: 'Bob'}), (b:Person {name: 'Isolated'}) INSERT (a)<-[:KNOWS {weight: 3}]-(b)", 1,
                    new ExecutionCase("MATCH (a:Person {name: 'Isolated'})-[r:KNOWS]->(b) RETURN b.name, r.weight", [["Bob", 3L]])),
            ],
            [GqlClauses.Delete] =
            [
                Mutation("MATCH (a:Person {name: 'Isolated'}) DELETE a", 1,
                    new ExecutionCase("MATCH (a:Person {name: 'Isolated'}) RETURN a.name", [])),
                Mutation("MATCH (a:Person {name: 'Alice'})-[r:KNOWS]->(b) DELETE a, r", 2,
                    new ExecutionCase("MATCH (a:Person {name: 'Alice'}) RETURN a.name", []),
                    new ExecutionCase("MATCH (a)-[r:KNOWS]->(b) RETURN r.weight", []),
                    new ExecutionCase("MATCH (b:Person {name: 'Bob'}) RETURN b.name", [["Bob"]])),
            ],
            [GqlClauses.DetachDelete] =
            [
                Mutation("MATCH (a:Person {name: 'Alice'}) DETACH DELETE a", 1,
                    new ExecutionCase("MATCH (a:Person {name: 'Alice'}) RETURN a.name", []),
                    new ExecutionCase("MATCH (a)-[r:KNOWS]->(b) RETURN r.weight", []),
                    new ExecutionCase("MATCH (b:Person {name: 'Bob'}) RETURN b.name", [["Bob"]])),
            ],
            [GqlClauses.Where] =
            [
                new("MATCH (a:Person) WHERE a.age = @age RETURN a.name", [["Alice"]],
                    Parameters: new Dictionary<string, object?> { ["age"] = 42 }),
                new("MATCH (a:Person) WHERE (a.age >= 18 AND a.age <= 42) AND a.age > 17 AND a.age < 43 AND a.name <> 'Isolated' AND a.active = TRUE RETURN a.name", [["Alice"]]),
                new("MATCH (a:Person) WHERE a.absent = NULL RETURN a.name", []),
                new("MATCH (a:Person) WHERE UNKNOWN RETURN a.name", []),
                new("MATCH (a:Person) WHERE UNKNOWN IS UNKNOWN RETURN a.name", [["Alice"], ["Bob"], ["Isolated"]]),
                new("MATCH (a:Person) WHERE a.age = 17 OR a.age = 42 RETURN a.name", [["Alice"], ["Bob"]]),
                new("MATCH (a:Person) WHERE a.age > 20 XOR a.age = 30 RETURN a.name", [["Alice"]]),
                new("MATCH (a:Person) WHERE TRUE OR TRUE XOR TRUE RETURN a.name", []),
                new("MATCH (a:Person) WHERE TRUE XOR TRUE OR TRUE RETURN a.name", [["Alice"], ["Bob"], ["Isolated"]]),
                new("MATCH (a:Person) WHERE NOT a.active RETURN a.name", [["Bob"]]),
                new("MATCH (a:Person) WHERE a.active RETURN a.name", [["Alice"]]),
                new("MATCH (a:Person) WHERE a.active IS NULL RETURN a.name", [["Isolated"]]),
                new("MATCH (a:Person) WHERE a.age IS NOT NULL RETURN a.name", [["Alice"], ["Bob"], ["Isolated"]]),
                new("MATCH (a:Person) WHERE a.active IS TRUE RETURN a.name", [["Alice"]]),
                new("MATCH (a:Person) WHERE a.active IS NOT TRUE RETURN a.name", [["Bob"], ["Isolated"]]),
                new("MATCH (a:Person) WHERE a.active IS FALSE RETURN a.name", [["Bob"]]),
                new("MATCH (a:Person) WHERE a.active IS NOT FALSE RETURN a.name", [["Alice"], ["Isolated"]]),
                new("MATCH (a:Person) WHERE a.active IS UNKNOWN RETURN a.name", [["Isolated"]]),
                new("MATCH (a:Person) WHERE a.active IS NOT UNKNOWN RETURN a.name", [["Alice"], ["Bob"]]),
                new("MATCH (a:Person) WHERE a.age IN [17, 42] RETURN a.name", [["Alice"], ["Bob"]]),
                new("MATCH (a:Person) WHERE 1 IN [1, NULL] RETURN a.name", [["Alice"], ["Bob"], ["Isolated"]]),
                new("MATCH (a:Person) WHERE (1 IN [2, NULL]) IS UNKNOWN RETURN a.name", [["Alice"], ["Bob"], ["Isolated"]]),
                new("MATCH (a:Person) WHERE a.name STARTS WITH 'Al' RETURN a.name", [["Alice"]]),
                new("MATCH (a:Person) WHERE a.name ENDS WITH 'ob' RETURN a.name", [["Bob"]]),
                new("MATCH (a:Person) WHERE a.name CONTAINS 'sol' RETURN a.name", [["Isolated"]]),
                new("MATCH (a:Person) WHERE a.name || 'x' = 'Alicex' RETURN a.name", [["Alice"]]),
                new("MATCH (a:Person) WHERE a.name + 'x' = 'Bobx' RETURN a.name", [["Bob"]]),
                new("MATCH (a:Person) WHERE a.age + 1 = 43 RETURN a.name", [["Alice"]]),
                new("MATCH (a:Person) WHERE a.age - 2 = 40 RETURN a.name", [["Alice"]]),
                new("MATCH (a:Person) WHERE a.age * 2 = 84 RETURN a.name", [["Alice"]]),
                new("MATCH (a:Person) WHERE a.age / 5 = 8 RETURN a.name", [["Alice"]]),
                new("MATCH (a:Person) WHERE -a.age / 5 = -8 AND -a.age % 5 = -2 RETURN a.name", [["Alice"]]),
                new("MATCH (a:Person) WHERE a.age / -5 = -8 AND a.age % -5 = 2 RETURN a.name", [["Alice"]]),
                new("MATCH (a:Person) WHERE a.age / 5.0 = 8.4 RETURN a.name", [["Alice"]]),
                new("MATCH (a:Person) WHERE a.age % 5 = 2 RETURN a.name", [["Alice"], ["Bob"]]),
                new("MATCH (a:Person) WHERE -9223372036854775808 % -1 = 0 RETURN a.name", [["Alice"], ["Bob"], ["Isolated"]]),
                new("MATCH (a:Person) WHERE NOT (a.absent = 1) RETURN a.name", []),
                new("MATCH (a:Person) WHERE a.absent = 1 OR a.age = 42 RETURN a.name", [["Alice"]]),
                new("MATCH (a:Person) WHERE (2 IN [1, NULL]) IS UNKNOWN RETURN a.name", [["Alice"], ["Bob"], ["Isolated"]]),
                new("MATCH (a:Person) WHERE (NULL IN []) IS FALSE RETURN a.name", [["Alice"], ["Bob"], ["Isolated"]]),
                new("MATCH (a:Person) WHERE NOT (a.name < 1) RETURN a.name", []),
                new("MATCH (a:Person) WHERE a.name <> 1 RETURN a.name", [["Alice"], ["Bob"], ["Isolated"]]),
                new("MATCH (a:Person) WHERE (1 STARTS WITH '1') IS UNKNOWN RETURN a.name", [["Alice"], ["Bob"], ["Isolated"]]),
                new("MATCH (a:Person) WHERE ('1' ENDS WITH 1) IS UNKNOWN RETURN a.name", [["Alice"], ["Bob"], ["Isolated"]]),
                new("MATCH (a:Person) WHERE (NULL CONTAINS '1') IS UNKNOWN RETURN a.name", [["Alice"], ["Bob"], ["Isolated"]]),
                new("MATCH (a:Person) WHERE a.name STARTS WITH 'al' RETURN a.name", []),
                new("MATCH (a:Person) WHERE NOT a:Robot OR a:Missing RETURN a.name", [["Alice"], ["Bob"], ["Isolated"]]),
                new("MATCH (a:Person) WHERE a:Missing OR a.age = 42 RETURN a.name", [["Alice"]]),
                new("MATCH (a:Person) WHERE NOT a:Missing RETURN a.name", [["Alice"], ["Bob"], ["Isolated"]]),
            ],
            [GqlClauses.Show] =
            [
                new("SHOW LABELS", [["audit", _personLabelId, "Person"], ["audit", _robotLabelId, "Robot"]]),
            ],
            [GqlClauses.LabelExpression] =
            [
                new("MATCH (n:Person|Robot) RETURN n.name", [["Alice"], ["Bob"], ["Isolated"], ["Robo"]]),
                new("MATCH (n:!Person) RETURN n.name", [["Robo"], ["Ghost"]]),
                new("MATCH (n:!%) RETURN n.name", [["Ghost"]]),
                new("MATCH (n IS %&!Robot {active: TRUE}) RETURN n.name", [["Alice"]]),
                new("MATCH (a)-[r:KNOWS|SERVES]->(b) RETURN a.name, b.name", [["Alice", "Bob"], ["Robo", "Ghost"]]),
                new("MATCH (a)-[r:!KNOWS]->(b) RETURN a.name", [["Robo"]]),
                new("MATCH (n) WHERE n IS LABELED Robot|!% RETURN n.name", [["Robo"], ["Ghost"]]),
                new("MATCH (n) WHERE n:Person AND n IS NOT LABELED Robot AND n.age > 20 RETURN n.name", [["Alice"], ["Isolated"]]),
            ],
        };

    /// <summary>Every profile clause must have data, use that clause, and produce its expected engine result.</summary>
    [Fact(DisplayName = "Cohesion Test [Graph] - Every advertised GQL clause executes against a real engine")]
    public async Task Profile_AdvertisedClauses_AllHavePassingExecutionCasesAsync()
    {
        EnsureCoverage(GqlLanguageProfile.Instance.Clauses);
        foreach (string clause in GqlLanguageProfile.Instance.Clauses)
        {
            foreach (var executionCase in _cases[clause])
            {
                // Isolate each case's parsing, seeding and execution deadline: repeated engine
                // setup must not consume the following case's budget (timings in DESIGN.md).
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var token = cancellation.Token;
                var statement = new GqlQueryParser().Parse(executionCase.Statement).ShouldBeOfType<GqlQueryStatement>();
                statement.Diagnostics.ShouldBeEmpty($"{clause}: {executionCase.Statement}");
                UsesClause(clause, statement.GqlExpression).ShouldBeTrue($"The {clause} case must exercise its clause: {executionCase.Statement}");
                await using var engine = GraphDatabaseEngine.Create("graph-engine", new());
                var database = await engine.CreateDatabaseAsync("audit", token);
                await using var session = await database.CreateSessionAsync(token);
                await GraphSchema.Open(database, session).SaveLabelAsync(new(_personLabelId, "Person"), token);
                await GraphSchema.Open(database, session).SaveLabelAsync(new(_robotLabelId, "Robot"), token);
                await session.ExecuteAsync(seed, cancellationToken: token);

                await ExecuteAndVerifyAsync(session, executionCase, clause, token);
            }
        }
    }

    /// <summary>Demonstrates that a newly advertised clause cannot silently escape the execution matrix.</summary>
    [Fact(DisplayName = "Cohesion Test [Graph] - A profile addition without execution data fails conformance")]
    public void Profile_AdditionalUnmappedClause_FailsCoverage()
    {
        var clauses = GqlLanguageProfile.Instance.Clauses.Append("FUTURE GQL CLAUSE");
        var failure = Should.Throw<ShouldAssertException>(() => EnsureCoverage(clauses));
        failure.Message.ShouldContain("FUTURE GQL CLAUSE", Case.Sensitive);
    }

    /// <summary>The supported subset rejects broader GQL forms at parse time, before they reach planning.</summary>
    [Theory(DisplayName = "Cohesion Test [Graph] - Unsupported forms fail at parse time against a live engine")]
    [InlineData("MATCH (a)-[r*1..3]->(b) RETURN a")]
    [InlineData("MATCH (a) RETURN a {.name}")]
    [InlineData("INSERT (:Person {tags: [1, 2]})")]
    [InlineData("MATCH (a) DELETE a RETURN a")]
    [InlineData("SHOW DATABASES")]
    [InlineData("MATCH (a)~[r]~(b) RETURN a")]
    [InlineData("MATCH (a)<~(b) RETURN a")]
    public async Task Profile_UnsupportedForms_ReportCapabilityDiagnosticAsync(string source)
    {
        new GqlQueryParser().Parse(source).Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "COHDBL001");
        await using var engine = GraphDatabaseEngine.Create("graph-engine", new());
        var database = await engine.CreateDatabaseAsync("boundary", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var error = await Should.ThrowAsync<DatabaseParseException>(async () =>
            await session.ExecuteAsync(source, cancellationToken: CancellationToken.None));
        error.Message.ShouldContain("COHDBL001", Case.Sensitive);
    }

    private static readonly IReadOnlyDictionary<string, ExecutionCase> _functionCases =
        new Dictionary<string, ExecutionCase>(StringComparer.OrdinalIgnoreCase)
        {
            ["count"] = new("MATCH (a:Person) RETURN count(a)", [[3L]]),
            ["sum"] = new("MATCH (a:Person) RETURN sum(a.age)", [[89L]]),
            ["avg"] = new("MATCH (a:Person) RETURN avg(a.age)", [[89d / 3]]),
            ["min"] = new("MATCH (a:Person) RETURN min(a.age)", [[17L]]),
            ["max"] = new("MATCH (a:Person) RETURN max(a.age)", [[42L]]),
            ["collect_list"] = new("MATCH (a:Person) RETURN collect_list(a.active)", [[new object?[] { true, false }]]),
            ["collect"] = new("MATCH (a:Person) RETURN collect(a.active)", [[new object?[] { true, false }]]),
        };

    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: every declared function has passing live evidence")]
    public async Task Profile_DeclaredFunctions_ShouldExecuteEveryCaseAsync()
    {
        // Arrange
        RequireFunctionCoverage(GqlLanguageProfile.Instance.Functions.ToArray(), _functionCases.Keys);
        await using var engine = GraphDatabaseEngine.Create("graph-functions", new());
        var database = await engine.CreateDatabaseAsync("functions", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);

        // Act / Assert
        foreach (string name in GqlLanguageProfile.Instance.Functions.ToArray())
        {
            await ExecuteAndVerifyAsync(session, _functionCases[name], name, CancellationToken.None);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Functions: coverage rejects an unmeasured declaration or a retired case")]
    [InlineData(true)]
    [InlineData(false)]
    public void Profile_FunctionCoverageMismatch_ShouldFail(bool addedDeclaration)
    {
        // Arrange
        var declarations = GqlLanguageProfile.Instance.Functions.ToArray();
        var cases = _functionCases.Keys;
        // Act / Assert
        Should.Throw<ShouldAssertException>(() => RequireFunctionCoverage(
            addedDeclaration ? declarations.Append("FUTURE") : declarations,
            addedDeclaration ? cases : cases.Append("RETIRED")));
    }

    private static void RequireFunctionCoverage(IEnumerable<string> declarations, IEnumerable<string> cases)
    {
        declarations.Except(cases, StringComparer.OrdinalIgnoreCase).ShouldBeEmpty();
        cases.Except(declarations, StringComparer.OrdinalIgnoreCase).ShouldBeEmpty();
    }

    private static void EnsureCoverage(IEnumerable<string> clauses)
    {
        foreach (string clause in clauses)
        {
            _cases.ContainsKey(clause).ShouldBeTrue($"Advertised GQL clause '{clause}' has no execution case.");
            _cases[clause].ShouldNotBeEmpty($"Advertised GQL clause '{clause}' has no execution case.");
        }
    }

    private static bool UsesClause(string clause, GqlQueryExpression query) => clause switch
    {
        GqlClauses.Explain => query.IsExplain,
        GqlClauses.Match => query.MatchClauses.Any(clause => !clause.Optional),
        GqlClauses.OptionalMatch => query.MatchClauses.Any(clause => clause.Optional),
        GqlClauses.Case => query.Projections.Any(item => item.Expression is GqlCaseExpression or GqlCoalesceExpression or GqlNullIfExpression),
        GqlClauses.Let => query.Stages.Any(stage => stage is GqlLetStage),
        GqlClauses.Filter => query.Stages.Any(stage => stage is GqlFilterStage),
        GqlClauses.For => query.Stages.Any(stage => stage is GqlForStage { IsUnwind: false }),
        GqlClauses.Unwind => query.Stages.Any(stage => stage is GqlForStage { IsUnwind: true }),
        GqlClauses.Next => query.Stages.Any(stage => stage is GqlNextStage),
        GqlClauses.With => query.Stages.Any(stage => stage is GqlReturnStage { IsWith: true }),
        GqlClauses.Return => query.Projections.Count != 0 || query.ReturnStar,
        GqlClauses.GroupBy => query.GroupBy is not null,
        GqlClauses.OrderBy => query.OrderBy.Count != 0,
        GqlClauses.Offset => query.Offset is not null && HasKeyword(query.Text ?? string.Empty, "OFFSET"),
        GqlClauses.Skip => query.Offset is not null && HasKeyword(query.Text ?? string.Empty, "SKIP"),
        GqlClauses.Limit => query.Limit is not null,
        GqlClauses.Create => query.Creates.Count != 0 && HasKeyword(query.Text ?? string.Empty, "CREATE"),
        GqlClauses.Insert => query.Creates.Count != 0 && HasKeyword(query.Text ?? string.Empty, "INSERT"),
        GqlClauses.Delete => query.DeleteVariables.Count != 0 && !query.DetachDelete,
        GqlClauses.DetachDelete => query.DeleteVariables.Count != 0 && query.DetachDelete,
        GqlClauses.Where => query.MatchClauses.Any(clause => clause.Predicate is not null),
        GqlClauses.Show => query.CatalogSurface is not null,
        GqlClauses.LabelExpression => query.MatchClauses.SelectMany(clause => clause.Paths).Concat(query.Creates).Any(UsesLabelOperators) || query.MatchClauses.Any(clause => HasLabeledPredicate(clause.Predicate)),
        _ => false,
    };

    // A label expression beyond the conjunction :A:B the profile always ran: an operator, the
    // wildcard or a type alternative on a pattern element.
    private static bool UsesLabelOperators(GqlPathPattern path)
        => path.Nodes.Any(node => node.LabelExpression is { } labels && !IsNameConjunction(labels)) ||
           path.Relationships.Any(edge => edge.LabelExpression is { } labels && labels is not GqlLabelName);

    private static bool IsNameConjunction(GqlLabelExpression expression) => expression switch
    {
        GqlLabelName => true,
        GqlLabelConjunction conjunction => conjunction.Operands.All(IsNameConjunction),
        _ => false,
    };

    private static bool HasLabeledPredicate(GqlExpression? expression) => expression switch
    {
        GqlLabeledPredicate => true,
        GqlLogicalExpression logical => logical.Operands.Any(HasLabeledPredicate),
        GqlBinaryExpression binary => HasLabeledPredicate(binary.Left) || HasLabeledPredicate(binary.Right),
        _ => false,
    };

    private static bool HasKeyword(string source, string keyword)
    {
        var lexer = new TokenLexer(source, GqlLanguageProfile.Instance.ToLexerOptions());
        foreach (var token in lexer)
        {
            if (token.Type == TokenType.Keyword && token.Value.Equals(keyword, StringComparison.OrdinalIgnoreCase)) { return true; }
        }
        return false;
    }

    private static ExecutionCase Mutation(string statement, long affectedCount, params ExecutionCase[] verification)
        => new(statement, [], affectedCount, verification);

    private static async Task ExecuteAndVerifyAsync(GraphDatabaseSession session, ExecutionCase executionCase,
        string clause, CancellationToken cancellationToken)
    {
        string context = $"{clause}: {executionCase.Statement}";
        var result = await session.ExecuteAsync(executionCase.Statement, executionCase.Parameters, cancellationToken);
        result.Status.ShouldBe(DatabaseResultStatus.Success, context);
        if (executionCase.AffectedCount is { } affected)
        {
            result.AffectedCount.ShouldBe(affected, context);
        }
        else
        {
            (result is DatabaseResultSet).ShouldBeTrue(context);
            await using var rowset = (DatabaseResultSet)result;
            if (executionCase.Columns is { } columns) { rowset.Columns.Select(column => column.Name).ShouldBe(columns, context); }
            var rows = new List<object?[]>();
            await foreach (var row in rowset.GetRowsAsync(cancellationToken))
            {
                rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
            }
            rows.Count.ShouldBe(executionCase.Rows.Length, context);
            for (int index = 0; index < rows.Count; index++) { rows[index].ShouldBe(executionCase.Rows[index], context); }
        }
        foreach (var verification in executionCase.Verification ?? [])
        {
            await ExecuteAndVerifyAsync(session, verification, clause, cancellationToken);
        }
    }

    private sealed record ExecutionCase(string Statement, object?[][] Rows, long? AffectedCount = null,
        ExecutionCase[]? Verification = null, IReadOnlyDictionary<string, object?>? Parameters = null, string[]? Columns = null);
}
