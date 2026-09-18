using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Storage;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlStorageSemanticsParityTests
{
    [Fact]
    public async Task StorageDerivedDurability_ShouldPreserveFilteredRowsAndConstraintEnforcement()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cohesion-sql-storage-parity", Guid.NewGuid().ToString("N"));
        try
        {
            var durable = await ObserveAsync(directory, StorageCommitDurability.Synchronous);
            var nonDurable = await ObserveAsync(null, StorageCommitDurability.None);

            ChildRow[] expected =
            [
                new(10, 1, 2, "alpha"),
                new(11, 1, 4, "beta"),
                new(12, 2, 3, "gamma"),
                new(13, null, 5, "unparented"),
            ];
            durable.Rows.ShouldBe(expected);
            nonDurable.Rows.ShouldBe(durable.Rows);
            durable.FilteredRows.ShouldBe(new[] { expected[1], expected[2], expected[3] });
            nonDurable.FilteredRows.ShouldBe(durable.FilteredRows);
            nonDurable.Violations.ShouldBe(durable.Violations);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task<Observation> ObserveAsync(string? directory, StorageCommitDurability expectedDurability)
    {
        // The same SQL and transaction boundaries exercise the production
        // physical and memory strategies. Durability is deliberately unset.
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions
        {
            EngineName = "storage-parity",
            RootPath = directory,
        });
        var database = await engine.CreateDatabaseAsync("db");
        var instance = database.ShouldBeOfType<SqlDatabaseInstance>();
        instance.DataStorage.CommitDurability.ShouldBe(expectedDurability);
        instance.CatalogStorage.CommitDurability.ShouldBe(expectedDurability);
        await using var session = await database.CreateSessionAsync();

        await session.ExecuteAsync("CREATE TABLE parent (id INT PRIMARY KEY)");
        await session.ExecuteAsync("CREATE TABLE child (id INT PRIMARY KEY, parent_id INT, qty INT, email TEXT, CONSTRAINT fk_child_parent FOREIGN KEY(parent_id) REFERENCES parent(id), CONSTRAINT ck_child_qty CHECK(qty > 0), CONSTRAINT uq_child_email UNIQUE(email))");
        await session.ExecuteAsync("INSERT INTO parent VALUES (1), (2), (3)");
        await session.ExecuteAsync("BEGIN");
        await session.ExecuteAsync("INSERT INTO child VALUES (10, 1, 2, 'alpha'), (11, 1, 4, 'beta'), (12, 2, 3, 'gamma'), (13, NULL, 5, 'unparented')");
        await session.ExecuteAsync("COMMIT");

        var rows = await ReadRowsAsync(session);
        var filteredRows = await ReadRowsAsync(session, filtered: true);
        var violations = new List<ConstraintFailure>();
        (string Sql, string Kind, string Name)[] rejectedStatements =
        [
            ("INSERT INTO child VALUES (20, 99, 1, 'orphan')", "FOREIGN KEY", "fk_child_parent"),
            ("UPDATE child SET qty = 0 WHERE id = 10", "CHECK", "ck_child_qty"),
            ("INSERT INTO child VALUES (21, 1, 1, 'alpha')", "UNIQUE", "uq_child_email"),
        ];
        foreach (var statement in rejectedStatements)
        {
            var failure = await Should.ThrowAsync<SqlConstraintViolationException>(async () => await session.ExecuteAsync(statement.Sql));
            failure.ConstraintKind.ShouldBe(statement.Kind);
            failure.ConstraintName.ShouldBe(statement.Name);
            failure.Table.ShouldBe("dbo.child");
            violations.Add(new(failure.ConstraintKind, failure.ConstraintName, failure.Table, failure.OffendingValue, failure.Message));
            (await ReadRowsAsync(session)).ShouldBe(rows);
            (await ReadRowsAsync(session, filtered: true)).ShouldBe(filteredRows);
        }

        return new(rows, filteredRows, violations);
    }

    private static async Task<List<ChildRow>> ReadRowsAsync(IDatabaseSession session, bool filtered = false)
    {
        string query = filtered
            ? "SELECT id, parent_id, qty, email FROM child WHERE qty >= 3 ORDER BY id"
            : "SELECT id, parent_id, qty, email FROM child ORDER BY id";
        var result = (await session.ExecuteAsync(query))
            .ShouldBeAssignableTo<QueryResultSet>();
        var rows = new List<ChildRow>();
        await foreach (var row in result.GetRowsAsync())
        {
            rows.Add(new((int)row.GetValue(0)!, (int?)row.GetValue(1), (int)row.GetValue(2)!, (string)row.GetValue(3)!));
        }
        return rows;
    }

    private sealed record ChildRow(int Id, int? ParentId, int Quantity, string Email);
    private sealed record ConstraintFailure(string Kind, string Name, string Table, object? OffendingValue, string Message);
    private sealed record Observation(List<ChildRow> Rows, List<ChildRow> FilteredRows, List<ConstraintFailure> Violations);
}
