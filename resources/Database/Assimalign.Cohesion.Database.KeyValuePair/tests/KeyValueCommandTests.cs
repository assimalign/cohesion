using System.Collections.Generic;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

using static KeyValueTestHarness;

/// <summary>
/// The text-execute command grammar (docs/COMMANDS.md) — the corpus that makes
/// the grammar a contract: every supported form executes end-to-end through the
/// session's text seam, and every violation fails as a parse error.
/// </summary>
public class KeyValueCommandTests
{
    private static Dictionary<string, object?> Parameters(params (string Name, object? Value)[] values)
    {
        var parameters = new Dictionary<string, object?>();

        foreach (var (name, value) in values)
        {
            parameters[name] = value;
        }

        return parameters;
    }

    private static async Task<List<object?[]>> MaterializeAsync(QueryResult result)
    {
        var rows = new List<object?[]>();
        var set = (QueryResultSet)result;

        await using (set)
        {
            await foreach (QueryRow row in set.GetRowsAsync(TestTimeout.Token()))
            {
                var values = new object?[row.FieldCount];
                for (int ordinal = 0; ordinal < row.FieldCount; ordinal++)
                {
                    values[ordinal] = row.GetValue(ordinal);
                }

                rows.Add(values);
            }
        }

        return rows;
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Commands: PUT then GET round-trips through the text seam")]
    public async Task Execute_PutThenGet_ShouldRoundTrip()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var session = await database.CreateSessionAsync();

        // Act
        var putResult = await session.ExecuteAsync("PUT @k @v", Parameters(("k", Bytes("user:1")), ("v", Bytes("ada"))), TestTimeout.Token());
        var putRows = await MaterializeAsync(putResult);

        var getRows = await MaterializeAsync(
            await session.ExecuteAsync("GET @k", Parameters(("k", Bytes("user:1"))), TestTimeout.Token()));

        // Assert: PUT reports [applied, etag]; GET reports [key, value, etag].
        putRows.ShouldHaveSingleItem();
        putRows[0][0].ShouldBe(true);
        long etag = putRows[0][1].ShouldBeOfType<long>();

        getRows.ShouldHaveSingleItem();
        getRows[0][0].ShouldBe(Bytes("user:1"));
        getRows[0][1].ShouldBe(Bytes("ada"));
        getRows[0][2].ShouldBe(etag);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Commands: Keywords are case-insensitive")]
    public async Task Execute_LowercaseVerbs_ShouldParse()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var session = await database.CreateSessionAsync();

        // Act
        await session.ExecuteAsync("put @k @v", Parameters(("k", Bytes("k")), ("v", Bytes("v"))), TestTimeout.Token());
        var rows = await MaterializeAsync(
            await session.ExecuteAsync("exists @k", Parameters(("k", Bytes("k"))), TestTimeout.Token()));

        // Assert
        rows.ShouldHaveSingleItem()[0].ShouldBe(true);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Commands: PUT IF ABSENT and PUT IF @etag express the conditional writes")]
    public async Task Execute_ConditionalPuts_ShouldHonorConditions()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var session = await database.CreateSessionAsync();

        var first = await MaterializeAsync(
            await session.ExecuteAsync("PUT @k @v IF ABSENT", Parameters(("k", Bytes("k")), ("v", Bytes("v1"))), TestTimeout.Token()));
        long etag = (long)first[0][1]!;

        // Act: a second IF ABSENT misses; a CAS on the current etag applies.
        var absentMiss = await MaterializeAsync(
            await session.ExecuteAsync("PUT @k @v IF ABSENT", Parameters(("k", Bytes("k")), ("v", Bytes("v2"))), TestTimeout.Token()));
        var swap = await MaterializeAsync(
            await session.ExecuteAsync("PUT @k @v IF @etag", Parameters(("k", Bytes("k")), ("v", Bytes("v3")), ("etag", etag)), TestTimeout.Token()));

        // Assert
        first[0][0].ShouldBe(true);
        absentMiss[0][0].ShouldBe(false);
        absentMiss[0][1].ShouldBe(etag);
        swap[0][0].ShouldBe(true);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Commands: DELETE reports its affected count and honors IF @etag")]
    public async Task Execute_Delete_ShouldReportAffectedCount()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var session = await database.CreateSessionAsync();
        var put = await MaterializeAsync(
            await session.ExecuteAsync("PUT @k @v", Parameters(("k", Bytes("k")), ("v", Bytes("v"))), TestTimeout.Token()));
        long etag = (long)put[0][1]!;

        // Act / Assert: stale etag deletes nothing; the current one deletes.
        var staleDelete = await session.ExecuteAsync("DELETE @k IF @etag", Parameters(("k", Bytes("k")), ("etag", etag + 1000)), TestTimeout.Token());
        staleDelete.AffectedCount.ShouldBe(0);

        var delete = await session.ExecuteAsync("DELETE @k IF @etag", Parameters(("k", Bytes("k")), ("etag", etag)), TestTimeout.Token());
        delete.AffectedCount.ShouldBe(1);

        var missingDelete = await session.ExecuteAsync("DELETE @k", Parameters(("k", Bytes("k"))), TestTimeout.Token());
        missingDelete.AffectedCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Commands: SCAN supports FROM/TO, PREFIX, and LIMIT clauses")]
    public async Task Execute_ScanClauses_ShouldBoundTheScan()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var session = await database.CreateSessionAsync();

        foreach (string key in new[] { "a:1", "a:2", "b:1", "b:2", "c:1" })
        {
            await session.ExecuteAsync("PUT @k @v", Parameters(("k", Bytes(key)), ("v", Bytes("v"))), TestTimeout.Token());
        }

        // Act
        var range = await MaterializeAsync(
            await session.ExecuteAsync("SCAN FROM @s TO @e", Parameters(("s", Bytes("a:2")), ("e", Bytes("b:2"))), TestTimeout.Token()));
        var prefixed = await MaterializeAsync(
            await session.ExecuteAsync("SCAN PREFIX @p", Parameters(("p", Bytes("b:"))), TestTimeout.Token()));
        var limited = await MaterializeAsync(
            await session.ExecuteAsync("SCAN LIMIT 2", null, TestTimeout.Token()));
        var limitedByParameter = await MaterializeAsync(
            await session.ExecuteAsync("SCAN LIMIT @n", Parameters(("n", 3)), TestTimeout.Token()));

        // Assert
        range.Count.ShouldBe(2); // a:2, b:1 — start inclusive, end exclusive.
        prefixed.Count.ShouldBe(2);
        limited.Count.ShouldBe(2);
        limitedByParameter.Count.ShouldBe(3);
    }

    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Commands: Grammar violations fail as parse errors")]
    [InlineData("FETCH @k")]
    [InlineData("GET")]
    [InlineData("GET @k extra")]
    [InlineData("GET k")]
    [InlineData("PUT @k")]
    [InlineData("PUT @k @v WHEN ABSENT")]
    [InlineData("DELETE @k IF")]
    [InlineData("SCAN SIDEWAYS @k")]
    [InlineData("SCAN LIMIT -1")]
    [InlineData("SCAN LIMIT 2 LIMIT 3")]
    public async Task Execute_GrammarViolation_ShouldThrowParseException(string command)
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var session = await database.CreateSessionAsync();
        var parameters = Parameters(("k", Bytes("k")), ("v", Bytes("v")));

        // Act / Assert
        await Should.ThrowAsync<DatabaseParseException>(async () =>
            await session.ExecuteAsync(command, parameters, TestTimeout.Token()));
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Commands: A missing parameter binding fails as a parse error")]
    public async Task Execute_MissingParameter_ShouldThrowParseException()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var session = await database.CreateSessionAsync();

        // Act / Assert
        var failure = await Should.ThrowAsync<DatabaseParseException>(async () =>
            await session.ExecuteAsync("GET @unbound", null, TestTimeout.Token()));

        failure.Message.ShouldContain("unbound");
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Commands: A wrongly-typed operand fails as a parse error")]
    public async Task Execute_WrongOperandType_ShouldThrowParseException()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var session = await database.CreateSessionAsync();

        // Act / Assert: keys are byte[] operands, not strings.
        await Should.ThrowAsync<DatabaseParseException>(async () =>
            await session.ExecuteAsync("GET @k", Parameters(("k", "not-bytes")), TestTimeout.Token()));
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Commands: A non-key-value request is rejected by the typed seam")]
    public async Task Execute_ForeignRequestType_ShouldThrow()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var session = await database.CreateSessionAsync();

        // Act / Assert
        await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(new ForeignRequest(), TestTimeout.Token()));
    }

    private sealed class ForeignRequest : QueryRequest
    {
        internal ForeignRequest()
            : base(new KeyValueStatement(KeyValueOperation.Get))
        {
        }
    }
}
