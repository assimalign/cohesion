using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// Exercises the catalog's virtual relations through encoded SQL protocol exchanges.
/// </summary>
public sealed class SqlSystemViewProtocolTests
{
    /// <summary>Metadata filtering, ordering, values, and type headers survive the wire protocol.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - System views: filtered COLUMNS is queryable over the wire")]
    public async Task Columns_ShouldFilterProjectAndOrderOverTheWire()
    {
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();
        await ExecuteAsync(client, "CREATE TABLE orders (id INT NOT NULL, amount DECIMAL(12, 2) DEFAULT 0, description VARCHAR(80))");

        var query = new ProtocolExecuteMessage(
            "SELECT COLUMN_NAME, ORDINAL_POSITION, DATA_TYPE, IS_NULLABLE, COLUMN_DEFAULT FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @table ORDER BY ORDINAL_POSITION DESC",
            new Dictionary<string, byte[]> { ["table"] = DatabaseValueCodec.EncodeComponent("orders") });
        await client.SendAsync(ProtocolMessageType.Execute, query.Encode());
        var headerFrame = await client.ExpectAsync(ProtocolMessageType.ResultHeader);
        var header = ProtocolResultHeaderMessage.Decode(headerFrame.Payload.Span);
        header.Columns.Select(column => column.Item1).ShouldBe(
            ["COLUMN_NAME", "ORDINAL_POSITION", "DATA_TYPE", "IS_NULLABLE", "COLUMN_DEFAULT"]);
        header.Columns.Select(column => column.Item2).ShouldBe(
            [(byte)DatabaseType.String, (byte)DatabaseType.Int64, (byte)DatabaseType.String,
                (byte)DatabaseType.String, (byte)DatabaseType.String]);

        var rows = await ReadRowsAsync(client);
        rows.Count.ShouldBe(3);
        rows[0].ShouldBe(new object?[] { "description", 3L, "CHARACTER VARYING", "YES", null });
        rows[1].ShouldBe(new object?[] { "amount", 2L, "NUMERIC", "YES", "0" });
        rows[2].ShouldBe(new object?[] { "id", 1L, "INTEGER", "NO", null });

        var filtered = await QueryAsync(client,
            "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'orders' ORDER BY ORDINAL_POSITION");
        filtered.Select(row => row[0]).ShouldBe(new object?[] { "id", "amount", "description" });
    }

    /// <summary>Every documented metadata relation returns catalog facts through the protocol.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - System views: the complete MVP view set works over the wire")]
    public async Task ViewSet_ShouldReportTablesKeysChecksIndexesAndOwnershipOverTheWire()
    {
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();
        await ExecuteAsync(client, "CREATE TABLE customers (id INT PRIMARY KEY)");
        await ExecuteAsync(client,
            "CREATE TABLE orders (id INT PRIMARY KEY, customer_id INT, quantity INT, " +
            "CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id) REFERENCES customers(id) ON DELETE CASCADE, " +
            "CONSTRAINT ck_orders_quantity CHECK (quantity > 0))");
        await ExecuteAsync(client, "CREATE INDEX ix_orders_customer ON orders (customer_id)");

        var tables = await QueryAsync(client,
            "SELECT TABLE_CATALOG, TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'orders'");
        tables.ShouldHaveSingleItem().ShouldBe(new object?[] { "app", "dbo", "orders", "BASE TABLE" });

        var constraints = await QueryAsync(client,
            "SELECT CONSTRAINT_NAME, CONSTRAINT_TYPE FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS WHERE TABLE_NAME = 'orders' ORDER BY CONSTRAINT_NAME");
        constraints.ShouldContain(row => Equals(row[0], "fk_orders_customer") && Equals(row[1], "FOREIGN KEY"));
        constraints.ShouldContain(row => Equals(row[0], "ck_orders_quantity") && Equals(row[1], "CHECK"));
        constraints.ShouldContain(row => Equals(row[1], "PRIMARY KEY"));

        var keyColumns = await QueryAsync(client,
            "SELECT COLUMN_NAME, ORDINAL_POSITION FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE WHERE CONSTRAINT_NAME = 'fk_orders_customer'");
        keyColumns.ShouldHaveSingleItem().ShouldBe(new object?[] { "customer_id", 1L });

        var references = await QueryAsync(client,
            "SELECT UNIQUE_CONSTRAINT_NAME, UPDATE_RULE, DELETE_RULE FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_NAME = 'fk_orders_customer'");
        var reference = references.ShouldHaveSingleItem();
        reference[0].ShouldNotBeNull();
        reference[1].ShouldBe("RESTRICT");
        reference[2].ShouldBe("CASCADE");
        var parentConstraints = await QueryAsync(client,
            "SELECT CONSTRAINT_NAME FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS WHERE TABLE_NAME = 'customers' AND CONSTRAINT_TYPE = 'PRIMARY KEY'");
        reference[0].ShouldBe(parentConstraints.ShouldHaveSingleItem()[0]);

        var checks = await QueryAsync(client,
            "SELECT CHECK_CLAUSE FROM INFORMATION_SCHEMA.CHECK_CONSTRAINTS WHERE CONSTRAINT_NAME = 'ck_orders_quantity'");
        checks.ShouldHaveSingleItem()[0].ShouldBeOfType<string>().ShouldContain("quantity");

        var indexes = await QueryAsync(client,
            "SELECT COLUMN_NAME, ORDINAL_POSITION, IS_UNIQUE, IS_PRIMARY_KEY FROM COHESION_SCHEMA.INDEXES WHERE INDEX_NAME = 'ix_orders_customer'");
        indexes.ShouldHaveSingleItem().ShouldBe(new object?[] { "customer_id", 1L, "NO", "NO" });
        var owners = await QueryAsync(client,
            "SELECT OBJECT_TYPE, OWNER, OWNING_SCHEMA FROM COHESION_SCHEMA.OBJECT_OWNERSHIP WHERE TABLE_NAME = 'orders' AND OBJECT_NAME = 'orders'");
        owners.ShouldHaveSingleItem().ShouldBe(new object?[] { "TABLE", "Adhoc", null });
    }

    /// <summary>System-view mutations have a stable wire diagnostic and preserve the connection.</summary>
    /// <param name="sql">An attempted system-view mutation.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory(DisplayName = "Cohesion Test [Database.Sql] - System views: writes return the documented wire diagnostic")]
    [InlineData("INSERT INTO INFORMATION_SCHEMA.TABLES (TABLE_NAME) VALUES ('bad')")]
    [InlineData("UPDATE INFORMATION_SCHEMA.TABLES SET TABLE_NAME = 'bad'")]
    [InlineData("DELETE FROM INFORMATION_SCHEMA.TABLES")]
    [InlineData("DROP TABLE IF EXISTS INFORMATION_SCHEMA.TABLES")]
    [InlineData("CREATE TABLE IF NOT EXISTS INFORMATION_SCHEMA.TABLES (id INT)")]
    [InlineData("ALTER TABLE INFORMATION_SCHEMA.TABLES ADD COLUMN extra INT")]
    [InlineData("CREATE INDEX ix_metadata ON INFORMATION_SCHEMA.TABLES (TABLE_NAME)")]
    [InlineData("DROP INDEX IF EXISTS ix_metadata ON INFORMATION_SCHEMA.TABLES")]
    public async Task Mutation_ShouldReturnStableDiagnosticAndKeepConnectionUsable(string sql)
    {
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();

        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create(sql).Encode());
        var frame = await client.ExpectAsync(ProtocolMessageType.Error);
        var error = ProtocolErrorMessage.Decode(frame.Payload.Span);
        error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        error.Message.ShouldBe("System view 'INFORMATION_SCHEMA.TABLES' is read-only.");

        var rows = await QueryAsync(client,
            "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'users'");
        rows.ShouldHaveSingleItem().ShouldBe(new object?[] { "users" });
    }

    private static async Task ExecuteAsync(ProtocolTestClient client, string sql)
    {
        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create(sql).Encode());
        await client.ExpectAsync(ProtocolMessageType.ResultComplete);
    }

    private static async Task<List<object?[]>> QueryAsync(ProtocolTestClient client, string sql)
    {
        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create(sql).Encode());
        await client.ExpectAsync(ProtocolMessageType.ResultHeader);
        return await ReadRowsAsync(client);
    }

    private static async Task<List<object?[]>> ReadRowsAsync(ProtocolTestClient client)
    {
        var rows = new List<object?[]>();
        while (true)
        {
            var frame = await client.ReadAsync();
            frame.ShouldNotBeNull();
            if (frame.Value.Type == ProtocolMessageType.ResultComplete)
            {
                ProtocolResultCompleteMessage.Decode(frame.Value.Payload.Span).AffectedCount.ShouldBe(-1);
                return rows;
            }
            frame.Value.Type.ShouldBe(ProtocolMessageType.ResultRow);
            rows.Add(DecodeRow(frame.Value.Payload.ToArray()));
        }
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
