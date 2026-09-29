using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>Proves typed default conversion and reference validation before ADD COLUMN publication.</summary>
public sealed class SqlAddColumnDefaultTypeTests
{
    /// <summary>Exact decimal and finite approximate defaults retain their storage types for old and new rows.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - ADD COLUMN: decimal and real defaults retain typed values")]
    public async Task AddColumn_NumericDefaults_ShouldBackfillAndDefaultInsertsWithCorrectTypes()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "add-numeric-types" });
        var database = await engine.CreateDatabaseAsync("numeric-types");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT, label TEXT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1, 'original');");

        // Act
        await ExecuteAsync(session, "ALTER TABLE t ADD COLUMN exact_value DECIMAL(3, 1) DEFAULT 12.3;");
        await ExecuteAsync(session, "ALTER TABLE t ADD COLUMN approximate_value REAL DEFAULT 1.25;");

        // Assert: the first read proves backfill before any subsequent write can materialize it.
        var old = (await RowsAsync(session, "SELECT * FROM t;")).ShouldHaveSingleItem();
        old.ShouldBe(new object?[] { 1, "original", 12.3m, 1.25f });
        old[2].ShouldBeOfType<decimal>().ShouldBe(12.3m);
        old[3].ShouldBeOfType<float>().ShouldBe(1.25f);

        await ExecuteAsync(session, "INSERT INTO t (id, label) VALUES (2, 'omitted');");
        var added = (await RowsAsync(session, "SELECT * FROM t WHERE id = 2;")).ShouldHaveSingleItem();
        added.ShouldBe(new object?[] { 2, "omitted", 12.3m, 1.25f });
        added[2].ShouldBeOfType<decimal>().ShouldBe(12.3m);
        added[3].ShouldBeOfType<float>().ShouldBe(1.25f);
    }

    /// <summary>Default validation does not depend on finding a row to backfill.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - ADD COLUMN: invalid defaults reject on empty tables")]
    public async Task AddColumn_InvalidDefaultOnEmptyTable_ShouldLeaveSchemaUnchanged()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "add-empty-conversion" });
        var database = await engine.CreateDatabaseAsync("empty-conversion");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT);");

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session,
            "ALTER TABLE t ADD COLUMN extra INT DEFAULT 2147483648;"));

        // Assert: this complements populated-table backfill tests by checking unconditional validation.
        error.Message.ShouldContain("DEFAULT", Case.Sensitive);
        error.Message.ShouldContain("extra", Case.Sensitive);
        await using var result = (await session.ExecuteAsync("SELECT * FROM t;", cancellationToken: CancellationToken.None))
            .ShouldBeAssignableTo<QueryResultSet>();
        result.Columns.ShouldHaveSingleItem().Name.ShouldBe("id");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1);");
        (await RowsAsync(session, "SELECT * FROM t;")).ShouldHaveSingleItem().ShouldBe(new object?[] { 1 });
    }

    /// <summary>A finite check must also reject a nonzero literal that would silently underflow to zero.</summary>
    /// <param name="type">The approximate numeric target.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - ADD COLUMN: floating default underflow rejects atomically")]
    [InlineData("REAL")]
    [InlineData("DOUBLE")]
    public async Task AddColumn_FloatingDefaultUnderflow_ShouldPreservePopulatedTable(string type)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "add-underflow" });
        var database = await engine.CreateDatabaseAsync("underflow");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT, label TEXT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1, 'original'), (2, NULL);");

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session,
            $"ALTER TABLE t ADD COLUMN extra {type} DEFAULT 1e-1000;"));

        // Assert
        error.Message.ShouldContain("DEFAULT", Case.Sensitive);
        error.Message.ShouldContain("extra", Case.Sensitive);
        var rows = await RowsAsync(session, "SELECT * FROM t ORDER BY id;");
        rows.Count.ShouldBe(2);
        rows[0].ShouldBe(new object?[] { 1, "original" });
        rows[1].ShouldBe(new object?[] { 2, null });
    }

    /// <summary>Foreign-key defaults validate old rows before publication and apply to later omitted inserts.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - ADD COLUMN: foreign-key defaults validate old and new rows")]
    public async Task AddColumn_ReferenceDefault_ShouldRejectMissingParentThenBackfillValidParent()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "add-reference-default" });
        var database = await engine.CreateDatabaseAsync("reference-default");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE parent (id INT PRIMARY KEY);");
        await ExecuteAsync(session, "CREATE TABLE child (id INT, label TEXT);");
        await ExecuteAsync(session, "INSERT INTO parent VALUES (7);");
        await ExecuteAsync(session, "INSERT INTO child VALUES (1, 'one'), (2, 'two');");

        // Act / Assert: failure must leave the original schema and rows available under the same names.
        var error = await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session,
            "ALTER TABLE child ADD COLUMN parent_id INT NOT NULL DEFAULT 8 REFERENCES parent(id);"));
        error.ConstraintKind.ShouldBe("FOREIGN KEY");
        var unchanged = await RowsAsync(session, "SELECT * FROM child ORDER BY id;");
        unchanged.Count.ShouldBe(2);
        unchanged[0].ShouldBe(new object?[] { 1, "one" });
        unchanged[1].ShouldBe(new object?[] { 2, "two" });

        await ExecuteAsync(session, "ALTER TABLE child ADD COLUMN parent_id INT NOT NULL DEFAULT 7 REFERENCES parent(id);");
        var backfilled = await RowsAsync(session, "SELECT * FROM child ORDER BY id;");
        backfilled.Count.ShouldBe(2);
        backfilled[0].ShouldBe(new object?[] { 1, "one", 7 });
        backfilled[1].ShouldBe(new object?[] { 2, "two", 7 });
        await ExecuteAsync(session, "INSERT INTO child (id, label) VALUES (3, 'omitted');");
        (await RowsAsync(session, "SELECT * FROM child WHERE id = 3;")).ShouldHaveSingleItem()
            .ShouldBe(new object?[] { 3, "omitted", 7 });
        await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session,
            "INSERT INTO child VALUES (4, 'orphan', 8);"));
        (await RowsAsync(session, "SELECT * FROM child WHERE id = 4;")).ShouldBeEmpty();
    }

    private static async Task ExecuteAsync(IDatabaseSession session, string sql)
    {
        var result = await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        result.Status.ShouldBe(QueryResultStatus.Success);
    }

    private static async Task<List<object?[]>> RowsAsync(IDatabaseSession session, string sql)
    {
        await using var result = (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None))
            .ShouldBeAssignableTo<QueryResultSet>();
        result.Status.ShouldBe(QueryResultStatus.Success);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            var values = new object?[row.FieldCount];
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = row.GetValue(index);
            }
            rows.Add(values);
        }
        return rows;
    }
}
