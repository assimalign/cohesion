using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>Verifies one SQL value order across predicates, sorting, grouping and distinct projection.</summary>
public sealed class SqlScalarComparisonTests
{
    /// <summary>Finite doubles remain comparable on both sides of the decimal range.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Comparison: WHERE preserves the full finite double range")]
    public async Task Where_LargeFiniteDoubles_ShouldCompareWithoutDecimalOverflow()
    {
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("comparison");
        await using var session = await database.CreateSessionAsync();
        await ExecuteAsync(session, "CREATE TABLE samples (id INT, amount FLOAT);");
        await ExecuteAsync(session,
            "INSERT INTO samples VALUES (1, @smaller), (2, @larger), (3, @negative), (4, @maximum), (5, @minimum);",
            new Dictionary<string, object?>
            {
                ["smaller"] = 1e99, ["larger"] = 1e100, ["negative"] = -1e100,
                ["maximum"] = double.MaxValue, ["minimum"] = double.MinValue,
            });

        (await RowsAsync(session, "SELECT id FROM samples WHERE amount > @threshold ORDER BY id;",
            new Dictionary<string, object?> { ["threshold"] = 1e99 }))
            .Select(row => row[0]).ShouldBe(new object?[] { 2, 4 });
        (await RowsAsync(session, "SELECT id FROM samples WHERE amount < @threshold ORDER BY id;",
            new Dictionary<string, object?> { ["threshold"] = -1e99 }))
            .Select(row => row[0]).ShouldBe(new object?[] { 3, 5 });
    }

    /// <summary>Adjacent doubles and nonzero subnormal values must not become equal after decimal rounding.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Comparison: WHERE distinguishes adjacent and subnormal doubles")]
    public async Task Where_DistinctDoubles_ShouldNotCollapseToOneDecimal()
    {
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("comparison");
        await using var session = await database.CreateSessionAsync();
        await ExecuteAsync(session, "CREATE TABLE samples (id INT, amount FLOAT);");
        await ExecuteAsync(session, "INSERT INTO samples VALUES (1, @one), (2, @next), (3, @tiny), (4, @zero);",
            new Dictionary<string, object?>
            {
                ["one"] = 1d, ["next"] = Math.BitIncrement(1d), ["tiny"] = double.Epsilon, ["zero"] = 0d,
            });

        (await RowsAsync(session, "SELECT id FROM samples WHERE id IN (1, 2) AND amount <> @one;",
            new Dictionary<string, object?> { ["one"] = 1d }))
            .ShouldHaveSingleItem()[0].ShouldBe(2);
        (await RowsAsync(session, "SELECT id FROM samples WHERE id IN (3, 4) AND amount <> @zero;",
            new Dictionary<string, object?> { ["zero"] = 0d }))
            .ShouldHaveSingleItem()[0].ShouldBe(3);
    }

    /// <summary>Binary predicates inspect late bytes as unsigned values and compare separate arrays by content.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Comparison: WHERE orders binary operands by late unsigned bytes")]
    public async Task Where_BinaryValues_ShouldCompareUnsignedBytesThroughTheEntireSequence()
    {
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("comparison");
        await using var session = await database.CreateSessionAsync();
        await ExecuteAsync(session, "CREATE TABLE samples (id INT, payload BINARY);");
        await ExecuteAsync(session,
            "INSERT INTO samples VALUES (1, @lower), (2, @higher), (3, @highest), (4, @prefix), (5, @empty);",
            new Dictionary<string, object?>
            {
                ["lower"] = BinaryValue(0x7f), ["higher"] = BinaryValue(0x80), ["highest"] = BinaryValue(0xff),
                ["prefix"] = BinaryValue(0x7f)[..128], ["empty"] = Array.Empty<byte>(),
            });

        (await RowsAsync(session, "SELECT id FROM samples WHERE payload > @pivot ORDER BY id;",
            new Dictionary<string, object?> { ["pivot"] = BinaryValue(0x7f) }))
            .Select(row => row[0]).ShouldBe(new object?[] { 2, 3 });
        (await RowsAsync(session, "SELECT id FROM samples WHERE payload = @pivot;",
            new Dictionary<string, object?> { ["pivot"] = BinaryValue(0x80) }))
            .ShouldHaveSingleItem()[0].ShouldBe(2);
        (await RowsAsync(session, "SELECT id FROM samples WHERE payload <= @pivot ORDER BY payload;",
            new Dictionary<string, object?> { ["pivot"] = BinaryValue(0x7f)[..128] }))
            .Select(row => row[0]).ShouldBe(new object?[] { 5, 4 });
    }

    /// <summary>One shuffled dataset with duplicates proves predicate equality and order agree with both grouping paths.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Comparison: WHERE ORDER BY DISTINCT and GROUP BY agree on the same data")]
    public async Task Comparison_OneDataset_ShouldAgreeAcrossPredicatesOrderingDistinctAndGrouping()
    {
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("comparison");
        await using var session = await database.CreateSessionAsync();
        await ExecuteAsync(session, "CREATE TABLE samples (id INT, amount FLOAT, payload BINARY);");
        double[] numbers = [-1e100, -double.Epsilon, 0d, double.Epsilon, 1d, Math.BitIncrement(1d), 1e99, 1e100];
        byte[] finalBytes = [0x00, 0x01, 0x7f, 0x80, 0x81, 0x82, 0xfe, 0xff];
        int[] insertionOrder = [7, 5, 2, 0, 6, 3, 1, 4, 5, 7];
        for (int id = 0; id < insertionOrder.Length; id++)
        {
            int rank = insertionOrder[id];
            await ExecuteAsync(session, "INSERT INTO samples VALUES (@id, @amount, @payload);",
                new Dictionary<string, object?>
                {
                    ["id"] = id, ["amount"] = numbers[rank], ["payload"] = BinaryValue(finalBytes[rank]),
                });
        }

        // Both columns encode the same ranks. Every pivot exercises scalar equality and
        // ordering against the exact rows that sorting and duplicate elimination consume.
        foreach (string column in new[] { "amount", "payload" })
        {
            var ordered = await RowsAsync(session, $"SELECT id FROM samples ORDER BY {column}, id;");
            var distinct = await RowsAsync(session, $"SELECT DISTINCT {column} FROM samples ORDER BY {column};");
            var groups = await RowsAsync(session,
                $"SELECT {column}, COUNT(*) FROM samples GROUP BY {column} ORDER BY {column};");
            for (int rank = 0; rank < numbers.Length; rank++)
            {
                object pivot = column == "amount" ? numbers[rank] : BinaryValue(finalBytes[rank]);
                var parameters = new Dictionary<string, object?> { ["pivot"] = pivot };
                var below = await RowsAsync(session,
                    $"SELECT id FROM samples WHERE {column} < @pivot ORDER BY {column}, id;", parameters);
                var equal = await RowsAsync(session,
                    $"SELECT id FROM samples WHERE {column} = @pivot ORDER BY id;", parameters);
                var above = await RowsAsync(session,
                    $"SELECT id FROM samples WHERE {column} > @pivot ORDER BY {column}, id;", parameters);
                int[] expectedBelow = Enumerable.Range(0, insertionOrder.Length)
                    .Where(id => insertionOrder[id] < rank).OrderBy(id => insertionOrder[id]).ThenBy(id => id).ToArray();
                int[] expectedEqual = Enumerable.Range(0, insertionOrder.Length)
                    .Where(id => insertionOrder[id] == rank).ToArray();
                int[] expectedAbove = Enumerable.Range(0, insertionOrder.Length)
                    .Where(id => insertionOrder[id] > rank).OrderBy(id => insertionOrder[id]).ThenBy(id => id).ToArray();

                below.Select(row => (int)row[0]!).ShouldBe(expectedBelow);
                equal.Select(row => (int)row[0]!).ShouldBe(expectedEqual);
                above.Select(row => (int)row[0]!).ShouldBe(expectedAbove);
                below.Concat(equal).Concat(above).Select(row => row[0])
                    .ShouldBe(ordered.Select(row => row[0]));
                distinct.Count.ShouldBe(numbers.Length);
                groups.Count.ShouldBe(numbers.Length);
                if (pivot is byte[] binary)
                {
                    ((byte[])distinct[rank][0]!).ShouldBe(binary);
                    ((byte[])groups[rank][0]!).ShouldBe(binary);
                }
                else
                {
                    distinct[rank][0].ShouldBe(pivot);
                    groups[rank][0].ShouldBe(pivot);
                }
                groups[rank][1].ShouldBe((long)equal.Count);
            }
        }
    }

    /// <summary>NaN payloads form one first class, signed zeros are equal, and infinities bound finite values.</summary>
    /// <param name="useIndex">Whether a physical floating-point index is present during predicate execution.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Comparison: IEEE values have documented predicate and grouping semantics")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Comparison_IeeeSpecialValues_ShouldUseOneTotalOrder(bool useIndex)
    {
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("comparison");
        await using var session = await database.CreateSessionAsync();
        await ExecuteAsync(session, "CREATE TABLE samples (id INT, amount FLOAT);");
        double[] values =
        [
            double.NaN, BitConverter.Int64BitsToDouble(0x7ff8000000000001), double.NegativeInfinity,
            double.MinValue, -0d, 0d, double.MaxValue, double.PositiveInfinity,
        ];
        for (int id = 0; id < values.Length; id++)
        {
            await ExecuteAsync(session, "INSERT INTO samples VALUES (@id, @amount);",
                new Dictionary<string, object?> { ["id"] = id, ["amount"] = values[id] });
        }
        if (useIndex)
        {
            await ExecuteAsync(session, "CREATE INDEX ix_samples_amount ON samples(amount);");
        }

        var parameters = new Dictionary<string, object?>
        {
            ["nan"] = double.NaN, ["negativeInfinity"] = double.NegativeInfinity,
            ["positiveInfinity"] = double.PositiveInfinity, ["zero"] = 0d,
        };
        (await RowsAsync(session, "SELECT id FROM samples WHERE amount = @nan ORDER BY id;", parameters))
            .Select(row => row[0]).ShouldBe(new object?[] { 0, 1 });
        (await RowsAsync(session, "SELECT id FROM samples WHERE amount <> @nan ORDER BY id;", parameters))
            .Select(row => row[0]).ShouldBe(new object?[] { 2, 3, 4, 5, 6, 7 });
        (await RowsAsync(session, "SELECT id FROM samples WHERE amount < @negativeInfinity ORDER BY id;", parameters))
            .Select(row => row[0]).ShouldBe(new object?[] { 0, 1 });
        (await RowsAsync(session, "SELECT id FROM samples WHERE amount > @nan AND amount < @positiveInfinity ORDER BY id;", parameters))
            .Select(row => row[0]).ShouldBe(new object?[] { 2, 3, 4, 5, 6 });
        (await RowsAsync(session, "SELECT id FROM samples WHERE amount = @zero ORDER BY id;", parameters))
            .Select(row => row[0]).ShouldBe(new object?[] { 4, 5 });
        (await RowsAsync(session, "SELECT DISTINCT amount FROM samples WHERE amount >= @nan ORDER BY amount;", parameters))
            .Select(row => row[0]).ShouldBe(new object?[]
            {
                double.NaN, double.NegativeInfinity, double.MinValue, 0d, double.MaxValue, double.PositiveInfinity,
            });
        (await RowsAsync(session, "SELECT COUNT(*) FROM samples WHERE amount >= @nan GROUP BY amount ORDER BY amount;", parameters))
            .Select(row => row[0]).ShouldBe(new object?[] { 2L, 1L, 1L, 2L, 1L, 1L });
    }

    /// <summary>Cross-type comparison uses actual numeric values without decimal rounding or double coercion.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Comparison: mixed numeric values preserve exact equality and order")]
    public async Task Where_MixedNumericTypes_ShouldKeepExactNumericOrder()
    {
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("comparison");
        await using var session = await database.CreateSessionAsync();
        await ExecuteAsync(session, "CREATE TABLE samples (id INT);");
        await ExecuteAsync(session, "INSERT INTO samples VALUES (1);");
        var parameters = new Dictionary<string, object?>
        {
            ["binaryTenth"] = 0.1d, ["decimalTenth"] = 0.1m,
            ["largeDouble"] = 9007199254740992d, ["equalInteger"] = 9007199254740992L,
            ["nextInteger"] = 9007199254740993L, ["oneDouble"] = 1d, ["oneDecimal"] = 1m, ["oneInteger"] = 1,
        };

        (await RowsAsync(session,
            "SELECT id FROM samples WHERE @binaryTenth > @decimalTenth AND @decimalTenth < @binaryTenth;", parameters))
            .ShouldHaveSingleItem()[0].ShouldBe(1);
        (await RowsAsync(session,
            "SELECT id FROM samples WHERE @largeDouble = @equalInteger AND @nextInteger > @largeDouble " +
            "AND @oneDouble = @oneDecimal AND @oneDecimal = @oneInteger;", parameters))
            .ShouldHaveSingleItem()[0].ShouldBe(1);
    }

    private static SqlDatabaseEngine CreateEngine()
        => SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "comparison-tests" });

    private static byte[] BinaryValue(byte finalByte)
    {
        var bytes = new byte[129];
        Array.Fill(bytes, (byte)0xa5);
        bytes[^1] = finalByte;
        return bytes;
    }

    private static Task<QueryResult> ExecuteAsync(IDatabaseSession session, string statement,
        IReadOnlyDictionary<string, object?>? parameters = null)
        => session.ExecuteAsync(statement, parameters, CancellationToken.None).AsTask();

    private static async Task<List<object?[]>> RowsAsync(IDatabaseSession session, string statement,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        await using var result = (await ExecuteAsync(session, statement, parameters)).ShouldBeAssignableTo<QueryResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
        }
        return rows;
    }
}
