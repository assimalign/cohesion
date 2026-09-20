using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.KeyValuePair.Internal;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

/// <summary>
/// Key-space discovery through the same commands clients execute over the wire.
/// </summary>
public sealed class KeyValueIntrospectionTests
{
    /// <summary>Both request seams describe the implicit space before it contains data.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Introspection: Text and typed requests discover the implicit key space")]
    public async Task KeySpaces_ShouldDescribeTheExistingImplicitSpace()
    {
        var (engine, database) = await KeyValueTestHarness.CreateAsync();
        await using var ownedEngine = engine;
        await using var session = await database.CreateSessionAsync();

        var result = (await session.ExecuteAsync("keyspaces", cancellationToken: TestTimeout.Token()))
            .ShouldBeAssignableTo<QueryResultSet>();
        result.Columns.Select(column => column.Name).ShouldBe(new[]
        {
            "database_name", "keyspace_id", "entry_space_format_version", "primary_index_name", "index_kind", "is_unique",
        });
        result.Columns.Select(column => column.Type).ShouldBe(new[]
        {
            DatabaseType.String, DatabaseType.Int64, DatabaseType.Int32, DatabaseType.String, DatabaseType.String, DatabaseType.Boolean,
        });
        var expected = new object?[] { "kv", 1L, 1, "key", "BTree", true };
        (await MaterializeAsync(result)).ShouldHaveSingleItem().ShouldBe(expected);
        (await MaterializeAsync(await session.ExecuteAsync(new KeyValueKeySpacesRequest(), TestTimeout.Token())))
            .ShouldHaveSingleItem().ShouldBe(expected);

        // Discovery never creates an entry in the ordinary record space.
        (await MaterializeAsync(await session.ExecuteAsync("SCAN", cancellationToken: TestTimeout.Token())))
            .ShouldBeEmpty();
    }

    /// <summary>Each command captures fresh catalog state, while existing results remain stable.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Introspection: Commands capture current catalog metadata")]
    public async Task KeySpaces_ShouldReadCatalogAtCommandExecution()
    {
        var (engine, database) = await KeyValueTestHarness.CreateAsync();
        await using var ownedEngine = engine;
        await using var session = await database.CreateSessionAsync();
        await using var transaction = await session.BeginTransactionAsync(TestTimeout.Token());
        var previous = await session.ExecuteAsync("KEYSPACES", cancellationToken: TestTimeout.Token());

        // A metadata publication after execution cannot alter its returned rows.
        var catalog = database.ShouldBeOfType<KeyValueDatabaseInstance>().Catalog;
        await catalog.SetEntrySpaceFormatVersionAsync(2, TestTimeout.Token());
        (await MaterializeAsync(previous)).ShouldHaveSingleItem()[2].ShouldBe(1);
        (await MaterializeAsync(await session.ExecuteAsync("KEYSPACES", cancellationToken: TestTimeout.Token())))
            .ShouldHaveSingleItem()[2].ShouldBe(2);
        await catalog.SetEntrySpaceFormatVersionAsync(1, TestTimeout.Token());
    }

    /// <summary>Both databases have the same implicit id, but their catalog values remain isolated.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Introspection scope: Discovery is bound to the session database")]
    public async Task KeySpaces_ShouldRemainDatabaseScoped()
    {
        await using var engine = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions { EngineName = "scope" });
        var first = await engine.CreateDatabaseAsync("first");
        var second = await engine.CreateDatabaseAsync("second");
        await using var session = await first.CreateSessionAsync();
        await using var other = await second.CreateSessionAsync();
        await second.ShouldBeOfType<KeyValueDatabaseInstance>().Catalog.SetEntrySpaceFormatVersionAsync(2, TestTimeout.Token());

        (await MaterializeAsync(await session.ExecuteAsync("KEYSPACES", cancellationToken: TestTimeout.Token())))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { "first", 1L, 1, "key", "BTree", true });
        (await MaterializeAsync(await other.ExecuteAsync(new KeyValueKeySpacesRequest(), TestTimeout.Token())))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { "second", 1L, 2, "key", "BTree", true });

        await Should.ThrowAsync<DatabaseParseException>(async () =>
            await session.ExecuteAsync("KEYSPACES FROM second", cancellationToken: TestTimeout.Token()));
        session.Database.ShouldBeSameAs(first);
    }

    /// <summary>All supported write verbs reject the reserved introspection target.</summary>
    /// <param name="command">The mutation attempt.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Introspection: Mutations receive the stable read-only diagnostic")]
    [InlineData("PUT KEYSPACES @value")]
    [InlineData("delete keyspaces")]
    public async Task KeySpaces_Write_ShouldRefuseMutation(string command)
    {
        var (engine, database) = await KeyValueTestHarness.CreateAsync();
        await using var ownedEngine = engine;
        await using var session = await database.CreateSessionAsync();

        var error = await Should.ThrowAsync<DatabaseParseException>(async () =>
            await session.ExecuteAsync(command, cancellationToken: TestTimeout.Token()));
        error.Message.ShouldBe("The KEYSPACES catalog surface is read-only.");
        (await MaterializeAsync(await session.ExecuteAsync("KEYSPACES", cancellationToken: TestTimeout.Token())))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { "kv", 1L, 1, "key", "BTree", true });

        var key = KeyValueTestHarness.Bytes("KEYSPACES");
        await database.PutAsync(session, key, KeyValueTestHarness.Bytes("data"), cancellationToken: TestTimeout.Token());
        (await database.GetAsync(session, key, TestTimeout.Token())).ShouldNotBeNull();
    }

    /// <summary>Discovery has no data operands, clauses, or database selectors.</summary>
    /// <param name="command">The malformed command.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Introspection: KEYSPACES rejects trailing operands")]
    [InlineData("KEYSPACES @name")]
    [InlineData("KEYSPACES LIMIT 1")]
    [InlineData("KEYSPACES FROM other")]
    public async Task KeySpaces_WithOperand_ShouldReject(string command)
    {
        var (engine, database) = await KeyValueTestHarness.CreateAsync();
        await using var ownedEngine = engine;
        await using var session = await database.CreateSessionAsync();

        var error = await Should.ThrowAsync<DatabaseParseException>(async () =>
            await session.ExecuteAsync(command, cancellationToken: TestTimeout.Token()));
        error.Message.ShouldBe("KEYSPACES takes the form: KEYSPACES.");
    }

    /// <summary>A protocol client receives metadata and the stable mutation diagnostic.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Introspection: Discovery and read-only errors travel over the wire")]
    public async Task KeySpaces_ShouldBeDiscoverableByAClient()
    {
        await using var harness = await KeyValueServerHarness.StartAsync();
        await harness.Engine.CreateDatabaseAsync("other");
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync(database: "other");

        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create("KEYSPACES").Encode());
        var header = ProtocolResultHeaderMessage.Decode((await client.ExpectAsync(ProtocolMessageType.ResultHeader)).Payload.Span);
        header.Columns.Count.ShouldBe(6);
        header.Columns[0].ShouldBe(("database_name", (byte)DatabaseType.String));
        header.Columns[1].ShouldBe(("keyspace_id", (byte)DatabaseType.Int64));
        var row = DecodeRow((await client.ExpectAsync(ProtocolMessageType.ResultRow)).Payload.ToArray());
        row.ShouldBe(new object?[] { "other", 1L, 1, "key", "BTree", true });
        await client.ExpectAsync(ProtocolMessageType.ResultComplete);

        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create("DELETE KEYSPACES").Encode());
        var error = ProtocolErrorMessage.Decode((await client.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);
        error.Code.ShouldBe(ProtocolErrorCode.ParseFailure);
        error.Message.ShouldBe("The KEYSPACES catalog surface is read-only.");

        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create("KEYSPACES").Encode());
        await client.ExpectAsync(ProtocolMessageType.ResultHeader);
        DecodeRow((await client.ExpectAsync(ProtocolMessageType.ResultRow)).Payload.ToArray()).ShouldBe(row);
        await client.ExpectAsync(ProtocolMessageType.ResultComplete);
    }

    private static async Task<List<object?[]>> MaterializeAsync(QueryResult result)
    {
        await using var set = result.ShouldBeAssignableTo<QueryResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in set.GetRowsAsync(TestTimeout.Token()))
        {
            var values = new object?[row.FieldCount];
            for (int ordinal = 0; ordinal < values.Length; ordinal++)
            {
                values[ordinal] = row.GetValue(ordinal);
            }
            rows.Add(values);
        }
        return rows;
    }

    private static object?[] DecodeRow(byte[] payload)
    {
        var values = new List<object?>();
        var reader = new DatabaseKeyReader(payload);
        while (!reader.IsAtEnd)
        {
            values.Add(DatabaseValueCodec.Read(ref reader));
        }
        return [.. values];
    }
}
