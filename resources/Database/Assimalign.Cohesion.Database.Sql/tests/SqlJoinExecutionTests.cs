using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// Proves two-table INNER JOIN binding, relational results, snapshot visibility,
/// and secondary-index adoption through the live SQL session pipeline.
/// </summary>
public sealed class SqlJoinExecutionTests
{
    private const string MotivatingQuery =
        "SELECT usr.Users.FirstName, usr.Users.LastName, usr.UsersProfile.Email " +
        "FROM usr.Users INNER JOIN usr.UsersProfile ON usr.Users.Id = usr.UsersProfile.UserId";

    /// <summary>Runs the owner's exact query and preserves every matching pair, without assuming implicit order.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - JOIN: schema-qualified owner query returns matching pairs")]
    public async Task Join_OwnerQuery_ShouldReturnMatchingPairs()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("join");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedUsersAsync(session);

        // Act
        await using var result = (await ExecuteAsync(session, MotivatingQuery)).ShouldBeAssignableTo<QueryResultSet>();
        var rows = await ReadRowsAsync(result);

        // Assert: no ORDER BY means the result is compared as a collection of pairs.
        result.Columns.Select(column => column.Name).ShouldBe(["FirstName", "LastName", "Email"]);
        result.Columns.Select(column => column.Type).ShouldBe([DatabaseType.String, DatabaseType.String, DatabaseType.String]);
        rows.Select(row => string.Join("|", row)).OrderBy(value => value, StringComparer.Ordinal).ShouldBe(
            ["Ada|Lovelace|ada-alt@example.test", "Ada|Lovelace|ada@example.test", "Grace|Hopper|grace@example.test"]);
    }

    /// <summary>Either empty input produces no inner-join rows and preserves projection metadata.</summary>
    /// <param name="emptyLeft">Whether the FROM side is empty.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - JOIN: either empty side returns an empty typed result")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Join_EmptySide_ShouldReturnNoRows(bool emptyLeft)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("join");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE a (id INT);");
        await ExecuteAsync(session, "CREATE TABLE b (id INT);");
        await ExecuteAsync(session, $"INSERT INTO {(emptyLeft ? "b" : "a")} VALUES (1);");

        // Act
        await using var result = (await ExecuteAsync(session, "SELECT a.id, b.id FROM a INNER JOIN b ON a.id = b.id;"))
            .ShouldBeAssignableTo<QueryResultSet>();

        // Assert
        (await ReadRowsAsync(result)).ShouldBeEmpty();
        result.Columns.Select(column => column.Type).ShouldBe([DatabaseType.Int32, DatabaseType.Int32]);
    }

    /// <summary>Filtering, sorting, parameters, and pagination consume columns from both inputs.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - JOIN: WHERE ORDER BY LIMIT and OFFSET compose")]
    public async Task Join_Clauses_ShouldFilterSortAndWindowPairs()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("join");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedUsersAsync(session);

        // Act
        var rows = await RowsAsync(session,
            "SELECT u.FirstName, p.Email FROM usr.Users u JOIN usr.UsersProfile p ON u.Id = p.UserId " +
            "WHERE u.Id >= @minimum AND p.Email LIKE '%example.test' ORDER BY u.Id DESC, p.Email ASC LIMIT 1 OFFSET 1;",
            new Dictionary<string, object?> { ["minimum"] = 1 });

        // Assert
        rows.ShouldHaveSingleItem().ShouldBe(new object?[] { "Ada", "ada-alt@example.test" });
    }

    /// <summary>Null equality never creates a match; arbitrary ON predicates still use SQL truth semantics.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - JOIN: null and non-equality ON predicates follow SQL semantics")]
    public async Task Join_NullAndNonEqualityPredicate_ShouldRetainOnlyTruePairs()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("join");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE a (id INT);");
        await ExecuteAsync(session, "CREATE TABLE b (id INT);");
        await ExecuteAsync(session, "INSERT INTO a VALUES (1), (2), (NULL);");
        await ExecuteAsync(session, "INSERT INTO b VALUES (2), (3), (NULL);");

        // Act / Assert
        (await RowsAsync(session, "SELECT a.id, b.id FROM a JOIN b ON a.id = b.id;"))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { 2, 2 });
        var pairs = await RowsAsync(session, "SELECT a.id, b.id FROM a JOIN b ON a.id < b.id ORDER BY a.id, b.id;");
        pairs.Count.ShouldBe(3);
        pairs[0].ShouldBe(new object?[] { 1, 2 });
        pairs[1].ShouldBe(new object?[] { 1, 3 });
        pairs[2].ShouldBe(new object?[] { 2, 3 });
        (await RowsAsync(session, "SELECT a.id, b.id FROM a JOIN b ON a.id IS NULL AND b.id IS NULL;"))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { null, null });
    }

    /// <summary>Ambiguous names fail during binding, including when the inputs have no rows.</summary>
    /// <param name="statement">The ambiguous expression position.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - JOIN: ambiguous unqualified columns fail explicitly")]
    [InlineData("SELECT id FROM a JOIN b ON a.id = b.id;")]
    [InlineData("SELECT a.id FROM a JOIN b ON id = b.id;")]
    [InlineData("SELECT a.id FROM a JOIN b ON a.id = b.id WHERE id > 0;")]
    [InlineData("SELECT a.id FROM a JOIN b ON a.id = b.id ORDER BY id;")]
    public async Task Join_AmbiguousColumn_ShouldRejectBeforeReadingRows(string statement)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("join");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE a (id INT);");
        await ExecuteAsync(session, "CREATE TABLE b (id INT);");

        // Act / Assert
        var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, statement));
        error.Message.ShouldContain("ambiguous");
        error.Message.ShouldContain("id", Case.Sensitive);
    }

    /// <summary>A nonexistent qualifier cannot accidentally resolve against a same-named column.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - JOIN: unknown qualifiers fail explicitly")]
    public async Task Join_UnknownQualifier_ShouldReject()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("join");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedUsersAsync(session);

        // Act / Assert
        var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session,
            "SELECT absent.FirstName FROM usr.Users u JOIN usr.UsersProfile p ON u.Id = p.UserId;"));
        error.Message.ShouldContain("absent", Case.Sensitive);
    }

    /// <summary>Self-join aliases distinguish both occurrences and unqualified stars retain FROM-first column order.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - JOIN: aliases and unqualified stars bind self joins")]
    public async Task Join_SelfJoinAliases_ShouldKeepInputOrdinalsSeparate()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("join");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE people (id INT, manager_id INT, name TEXT);");
        await ExecuteAsync(session, "INSERT INTO people VALUES (1, NULL, 'Manager'), (2, 1, 'Employee');");

        // Act
        var rows = await RowsAsync(session,
            "SELECT employee.id, employee.manager_id, employee.name, manager.name FROM people employee JOIN people manager ON employee.manager_id = manager.id;");

        // Assert
        rows.ShouldHaveSingleItem().ShouldBe(new object?[] { 2, 1, "Employee", "Manager" });
        (await RowsAsync(session, "SELECT * FROM people employee JOIN people manager ON employee.manager_id = manager.id;"))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { 2, 1, "Employee", 1, null, "Manager" });
        (await RowsAsync(session, "SELECT COUNT(*) FROM people a JOIN people b ON a.id = b.id;"))
            .ShouldHaveSingleItem()[0].ShouldBe(2L);
    }

    /// <summary>Foreign-key joins remain ordinary reads and preserve schema ownership enforcement.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - JOIN: foreign-key and schema-owned tables remain readable")]
    public async Task Join_ForeignKeyAndOwnership_ShouldReadWithoutChangingEnforcement()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("join");
        await using (var schema = ((SqlDatabaseInstance)database).CreateSchemaSession("people", CancellationToken.None))
        {
            await ExecuteAsync(schema, "CREATE TABLE parents (id INT PRIMARY KEY, name TEXT);");
            await ExecuteAsync(schema, "CREATE TABLE children (id INT PRIMARY KEY, parent_id INT REFERENCES parents(id));");
        }
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "INSERT INTO parents VALUES (1, 'Ada'), (2, 'Grace');");
        await ExecuteAsync(session, "INSERT INTO children VALUES (10, 1), (11, NULL);");

        // Act / Assert
        (await RowsAsync(session, "SELECT p.name, c.id FROM parents p JOIN children c ON p.id = c.parent_id;"))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { "Ada", 10 });
        (await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "INSERT INTO children VALUES (12, 99);")))
            .ConstraintKind.ShouldBe("FOREIGN KEY");
        await Should.ThrowAsync<DatabaseObjectLockedException>(() => ExecuteAsync(session, "DROP TABLE parents;"));
    }

    /// <summary>Both input versions and ON operands share the pinned snapshot for indexed and scanned joins.</summary>
    /// <param name="indexed">Whether the right join key has a secondary index.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - JOIN: both inputs share MVCC snapshot visibility")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Join_ConcurrentCommittedChanges_ShouldUseOneSnapshot(bool indexed)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("join");
        await using var reader = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await using var writer = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(writer, "CREATE TABLE a (id INT, value TEXT);");
        await ExecuteAsync(writer, "CREATE TABLE b (id INT, value TEXT);");
        if (indexed) { await ExecuteAsync(writer, "CREATE INDEX ix_b_id ON b(id);"); }
        await ExecuteAsync(writer, "INSERT INTO a VALUES (1, 'old-a');");
        await ExecuteAsync(writer, "INSERT INTO b VALUES (1, 'old-b');");
        var pinned = await reader.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);
        const string query = "SELECT a.value, b.value FROM a JOIN b ON a.id = b.id;";

        // Act: one committed transaction changes both keys and values after the reader's snapshot.
        var update = await writer.BeginTransactionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(writer, "UPDATE a SET id = 2, value = 'new-a';");
        await ExecuteAsync(writer, "UPDATE b SET id = 2, value = 'new-b';");
        await update.CommitAsync(CancellationToken.None);

        // Assert
        (await RowsAsync(reader, query)).ShouldHaveSingleItem().ShouldBe(new object?[] { "old-a", "old-b" });
        await pinned.RollbackAsync(CancellationToken.None);
        (await RowsAsync(reader, query)).ShouldHaveSingleItem().ShouldBe(new object?[] { "new-a", "new-b" });
    }

    /// <summary>ReadCommitted captures once per statement even when execution begins after another transaction commits.</summary>
    /// <param name="indexed">Whether the joined table has a secondary join-key index.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - JOIN: ReadCommitted executes both inputs under its captured statement snapshot")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Join_ReadCommittedStatement_ShouldKeepCapturedSnapshotAcrossInputs(bool indexed)
    {
        // Arrange: freeze the statement deterministically before a concurrent commit.
        await using var engine = CreateEngine();
        var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("join");
        await using var writer = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(writer, "CREATE TABLE a (id INT, value TEXT);");
        await ExecuteAsync(writer, "CREATE TABLE b (id INT, value TEXT);");
        if (indexed) { await ExecuteAsync(writer, "CREATE INDEX ix_b_id ON b(id);"); }
        await ExecuteAsync(writer, "INSERT INTO a VALUES (1, 'old-a');");
        await ExecuteAsync(writer, "INSERT INTO b VALUES (1, 'old-b');");
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        try
        {
            var captured = new SqlStatementContext(transaction, database.Coordinator);
            var request = SqlQueryRequest.FromSql("SELECT a.value, b.value FROM a JOIN b ON a.id = b.id;");
            var executor = new SqlQueryExecutor(database.DataStorage, database.Catalog, database.IndexManager);

            // Act: replace both ON operands atomically after this statement captured its view.
            var update = await writer.BeginTransactionAsync(cancellationToken: CancellationToken.None);
            await ExecuteAsync(writer, "UPDATE a SET id = 2, value = 'new-a';");
            await ExecuteAsync(writer, "UPDATE b SET id = 2, value = 'new-b';");
            await update.CommitAsync(CancellationToken.None);
            await using var oldResult = (await executor.ExecuteAsync(request, captured, CancellationToken.None))
                .ShouldBeAssignableTo<QueryResultSet>();
            await using var newResult = (await executor.ExecuteAsync(request,
                new SqlStatementContext(transaction, database.Coordinator), CancellationToken.None))
                .ShouldBeAssignableTo<QueryResultSet>();

            // Assert: recapturing Transaction.Snapshot inside either input would violate the old result.
            (await ReadRowsAsync(oldResult)).ShouldHaveSingleItem().ShouldBe(new object?[] { "old-a", "old-b" });
            (await ReadRowsAsync(newResult)).ShouldHaveSingleItem().ShouldBe(new object?[] { "new-a", "new-b" });
        }
        finally
        {
            await database.Coordinator.RollbackAsync(transaction, CancellationToken.None);
        }
    }

    /// <summary>Uncommitted rows from both inputs are visible only to their writer and disappear on rollback.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - JOIN: own writes are visible and other writers remain hidden")]
    public async Task Join_UncommittedWrites_ShouldRespectWriterVisibility()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("join");
        await using var writer = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await using var reader = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(writer, "CREATE TABLE a (id INT);");
        await ExecuteAsync(writer, "CREATE TABLE b (id INT);");
        await ExecuteAsync(writer, "CREATE INDEX ix_b_id ON b(id);");
        var transaction = await writer.BeginTransactionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(writer, "INSERT INTO a VALUES (1);");
        await ExecuteAsync(writer, "INSERT INTO b VALUES (1);");
        const string query = "SELECT a.id, b.id FROM a JOIN b ON a.id = b.id;";

        // Act / Assert
        (await RowsAsync(writer, query)).ShouldHaveSingleItem().ShouldBe(new object?[] { 1, 1 });
        (await RowsAsync(reader, query)).ShouldBeEmpty();
        await transaction.RollbackAsync(CancellationToken.None);
        (await RowsAsync(reader, query)).ShouldBeEmpty();
    }

    /// <summary>A secondary join-key index on either input avoids decoding the large table.</summary>
    /// <param name="indexedLeft">Whether the indexed table occurs in FROM instead of JOIN.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - JOIN: index on either input avoids a full scan")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Join_IndexedKey_ShouldExamineOnlyOuterRowsAndMatches(bool indexedLeft)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("join");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE probes (id INT);");
        await ExecuteAsync(session, "CREATE TABLE large (key_id INT, note TEXT);");
        await ExecuteAsync(session, "INSERT INTO probes VALUES (7), (201);");
        const int size = 256;
        for (int start = 0; start < size; start += 64)
        {
            string values = string.Join(", ", Enumerable.Range(start, 64).Select(id => $"({id}, 'n{id}')"));
            await ExecuteAsync(session, $"INSERT INTO large VALUES {values};");
        }
        await ExecuteAsync(session, "CREATE INDEX ix_large_key ON large(key_id);");
        string from = indexedLeft ? "large b JOIN probes p" : "probes p JOIN large b";
        string query = $"SELECT p.id, b.note FROM {from} ON p.id = b.key_id ORDER BY p.id;";

        // Act
        var seekRows = await RowsAsync(session, query);
        var seekMetrics = MetricsOf(session);
        seekMetrics.AccessPath.ShouldBe("join-seek:ix_large_key");
        long seekExamined = seekMetrics.RecordsExamined;
        await ExecuteAsync(session, "DROP INDEX ix_large_key ON large;");
        var scanRows = await RowsAsync(session, query);
        var scanMetrics = MetricsOf(session);

        // Assert: matching results alone cannot satisfy the access-path requirement.
        seekRows.Count.ShouldBe(2);
        seekRows[0].ShouldBe(new object?[] { 7, "n7" });
        seekRows[1].ShouldBe(new object?[] { 201, "n201" });
        scanRows.Select(row => string.Join("|", row)).ShouldBe(seekRows.Select(row => string.Join("|", row)));
        seekExamined.ShouldBe(4);
        scanMetrics.AccessPath.ShouldBe("join-scan");
        scanMetrics.RecordsExamined.ShouldBeGreaterThanOrEqualTo(size);
    }

    /// <summary>Composite equality prefixes narrow candidates while residual ON terms keep the result exact.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - JOIN: composite index and residual ON terms preserve matching pairs")]
    public async Task Join_CompositeIndexAndResidualPredicate_ShouldFilterCandidates()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("join");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE requests (tenant INT, item INT);");
        await ExecuteAsync(session, "CREATE TABLE items (tenant INT, item INT, enabled BOOLEAN);");
        await ExecuteAsync(session, "INSERT INTO requests VALUES (1, 2), (2, 2);");
        await ExecuteAsync(session, "INSERT INTO items VALUES (1, 1, TRUE), (1, 2, TRUE), (1, 2, FALSE), (2, 2, TRUE);");
        await ExecuteAsync(session, "CREATE INDEX ix_items_tenant_item ON items(tenant, item);");

        // Act
        var rows = await RowsAsync(session,
            "SELECT r.tenant, i.item FROM requests r JOIN items i ON r.tenant = i.tenant AND i.item = r.item AND i.enabled = TRUE ORDER BY r.tenant;");

        // Assert
        rows.Count.ShouldBe(2);
        rows[0].ShouldBe(new object?[] { 1, 2 });
        rows[1].ShouldBe(new object?[] { 2, 2 });
        MetricsOf(session).AccessPath.ShouldBe("join-seek:ix_items_tenant_item");
        MetricsOf(session).RecordsExamined.ShouldBe(5);
    }

    private static SqlDatabaseEngine CreateEngine()
        => SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "join-tests" });

    private static async Task SeedUsersAsync(IDatabaseSession session)
    {
        await ExecuteAsync(session, "CREATE TABLE usr.Users (Id INT PRIMARY KEY, FirstName TEXT, LastName TEXT);");
        await ExecuteAsync(session, "CREATE TABLE usr.UsersProfile (Id INT PRIMARY KEY, UserId INT, Email TEXT);");
        await ExecuteAsync(session, "INSERT INTO usr.Users VALUES (1, 'Ada', 'Lovelace'), (2, 'Grace', 'Hopper'), (3, 'Alan', 'Turing');");
        await ExecuteAsync(session, "INSERT INTO usr.UsersProfile VALUES (10, 1, 'ada@example.test'), (11, 1, 'ada-alt@example.test'), (20, 2, 'grace@example.test'), (99, 99, 'orphan@example.test');");
    }

    private static SqlStatementMetrics MetricsOf(IDatabaseSession session)
        => ((SqlDatabaseSession)session).LastStatementMetrics.ShouldNotBeNull();

    private static Task<QueryResult> ExecuteAsync(IDatabaseSession session, string sql, IReadOnlyDictionary<string, object?>? parameters = null)
        => session.ExecuteAsync(sql, parameters, CancellationToken.None).AsTask();

    private static async Task<List<object?[]>> RowsAsync(IDatabaseSession session, string sql, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        await using var result = (await ExecuteAsync(session, sql, parameters)).ShouldBeAssignableTo<QueryResultSet>();
        return await ReadRowsAsync(result);
    }

    private static async Task<List<object?[]>> ReadRowsAsync(QueryResultSet result)
    {
        result.Status.ShouldBe(QueryResultStatus.Success);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            var values = new object?[row.FieldCount];
            for (int index = 0; index < values.Length; index++) { values[index] = row.GetValue(index); }
            rows.Add(values);
        }
        return rows;
    }
}
