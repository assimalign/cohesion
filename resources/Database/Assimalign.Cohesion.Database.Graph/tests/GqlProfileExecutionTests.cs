using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Language;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Tests;

/// <summary>Measures each advertised GQL clause against a running graph engine.</summary>
public sealed class GqlProfileExecutionTests
{
    private static readonly Guid PersonLabelId = new("13333333-3333-3333-3333-333333333333");
    private const string seed = "INSERT (a:Person {name: 'Alice', age: 42, active: TRUE})-[r:KNOWS {weight: 2}]->(b:Person {name: 'Bob', age: 17, active: FALSE}), (:Person {name: 'Isolated', age: 30})";

    // The profile drives execution. A profile addition without data here fails before any query runs.
    private static readonly IReadOnlyDictionary<string, ExecutionCase[]> Cases =
        new Dictionary<string, ExecutionCase[]>(StringComparer.OrdinalIgnoreCase)
        {
            [GqlClauses.Match] =
            [
                new("MATCH (a:Person {name: 'Alice'})-[r:KNOWS]->(b) RETURN b.name, r.weight", [["Bob", 2L]]),
                new("MATCH (b:Person {name: 'Bob'})<-[r:KNOWS]-(a) RETURN a.name", [["Alice"]]),
                new("MATCH (b:Person {name: 'Bob'})-[r:KNOWS]-(a) RETURN a.name", [["Alice"]]),
            ],
            [GqlClauses.Return] =
            [
                new("MATCH (a:Person {name: 'Alice'}) RETURN a.name AS person, a.age, a.absent", [["Alice", 42L, null]]),
            ],
            [GqlClauses.Create] =
            [
                Mutation("CREATE (:Person {name: 'Cara'})", 1,
                    new ExecutionCase("MATCH (a:Person {name: 'Cara'}) RETURN a.name", [["Cara"]])),
                Mutation("MATCH (a:Person {name: 'Bob'}), (b:Person {name: 'Isolated'}) CREATE (a)-[:KNOWS {weight: 3}]->(b)", 1,
                    new ExecutionCase("MATCH (a:Person {name: 'Bob'})-[r:KNOWS]->(b) RETURN b.name, r.weight", [["Isolated", 3L]])),
            ],
            [GqlClauses.Insert] =
            [
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
                new("MATCH (a:Person) WHERE (a.age >= 18 AND a.age <= 42) AND a.age > 17 AND a.age < 43 AND a.name <> 'Isolated' AND a.active = TRUE RETURN a.name", [["Alice"]]),
                new("MATCH (a:Person) WHERE a.absent = NULL RETURN a.name", []),
            ],
            [GqlClauses.Show] =
            [
                new("SHOW LABELS", [["audit", PersonLabelId, "Person"]]),
            ],
        };

    /// <summary>Every profile clause must have data, use that clause, and produce its expected engine result.</summary>
    [Fact(DisplayName = "Cohesion Test [Graph] - Every advertised GQL clause executes against a real engine")]
    public async Task Profile_AdvertisedClauses_AllHavePassingExecutionCasesAsync()
    {
        EnsureCoverage(GqlLanguageProfile.Instance.Clauses);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = cancellation.Token;
        foreach (string clause in GqlLanguageProfile.Instance.Clauses)
        {
            foreach (var executionCase in Cases[clause])
            {
                var statement = new GqlQueryParser().Parse(executionCase.Statement).ShouldBeOfType<GqlQueryStatement>();
                statement.Diagnostics.ShouldBeEmpty($"{clause}: {executionCase.Statement}");
                UsesClause(clause, statement.GqlExpression).ShouldBeTrue($"The {clause} case must exercise its clause: {executionCase.Statement}");
                await using var engine = GraphDatabaseEngine.Create(new());
                var database = (IGraphDatabase)await engine.CreateDatabaseAsync("audit", token);
                await using var session = await database.CreateSessionAsync(token);
                await GraphSchema.Open(database, session).SaveLabelAsync(new(PersonLabelId, "Person"), token);
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
    [InlineData("MATCH (a) RETURN count(a)")]
    [InlineData("MATCH (a) RETURN *")]
    [InlineData("MATCH (a) WHERE a.age = 17 OR a.age = 42 RETURN a")]
    [InlineData("MATCH (a) WHERE a.age = $age RETURN a")]
    [InlineData("INSERT (:Person {tags: [1, 2]})")]
    [InlineData("MATCH (a) DELETE a RETURN a")]
    [InlineData("SHOW DATABASES")]
    public async Task Profile_UnsupportedForms_ReportCapabilityDiagnosticAsync(string source)
    {
        new GqlQueryParser().Parse(source).Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "COHDBL001");
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("boundary", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var error = await Should.ThrowAsync<DatabaseParseException>(async () =>
            await session.ExecuteAsync(source, cancellationToken: CancellationToken.None));
        error.Message.ShouldContain("COHDBL001", Case.Sensitive);
    }

    private static void EnsureCoverage(IEnumerable<string> clauses)
    {
        foreach (string clause in clauses)
        {
            Cases.ContainsKey(clause).ShouldBeTrue($"Advertised GQL clause '{clause}' has no execution case.");
            Cases[clause].ShouldNotBeEmpty($"Advertised GQL clause '{clause}' has no execution case.");
        }
    }

    private static bool UsesClause(string clause, GqlQueryExpression query) => clause switch
    {
        GqlClauses.Match => query.Matches.Count != 0,
        GqlClauses.Return => query.Projections.Count != 0,
        GqlClauses.Create => query.Creates.Count != 0 && HasKeyword(query.Text ?? string.Empty, "CREATE"),
        GqlClauses.Insert => query.Creates.Count != 0 && HasKeyword(query.Text ?? string.Empty, "INSERT"),
        GqlClauses.Delete => query.DeleteVariables.Count != 0 && !query.DetachDelete,
        GqlClauses.DetachDelete => query.DeleteVariables.Count != 0 && query.DetachDelete,
        GqlClauses.Where => query.Predicate is not null,
        GqlClauses.Show => query.CatalogSurface is not null,
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

    private static async Task ExecuteAndVerifyAsync(IDatabaseSession session, ExecutionCase executionCase,
        string clause, CancellationToken cancellationToken)
    {
        string context = $"{clause}: {executionCase.Statement}";
        var result = await session.ExecuteAsync(executionCase.Statement, cancellationToken: cancellationToken);
        result.Status.ShouldBe(QueryResultStatus.Success, context);
        if (executionCase.AffectedCount is { } affected)
        {
            result.AffectedCount.ShouldBe(affected, context);
        }
        else
        {
            (result is QueryResultSet).ShouldBeTrue(context);
            await using var rowset = (QueryResultSet)result;
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
        ExecutionCase[]? Verification = null);
}
