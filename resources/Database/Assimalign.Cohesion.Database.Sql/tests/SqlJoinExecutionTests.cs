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
        await using var result = (await ExecuteAsync(session, MotivatingQuery)).ShouldBeAssignableTo<DatabaseResultSet>();
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
            .ShouldBeAssignableTo<DatabaseResultSet>();

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
        await using (var schema = database.CreateSchemaSession("people", CancellationToken.None))
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
        (await SqlConstraintAssertions.ThrowAsync(() => ExecuteAsync(session, "INSERT INTO children VALUES (12, 99);")))
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
        await ExecuteAsync(writer, "CREATE TABLE c (id INT, value TEXT);");
        if (indexed)
        {
            await ExecuteAsync(writer, "CREATE INDEX ix_b_id ON b(id);");
            await ExecuteAsync(writer, "CREATE INDEX ix_c_id ON c(id);");
        }
        await ExecuteAsync(writer, "INSERT INTO a VALUES (1, 'old-a');");
        await ExecuteAsync(writer, "INSERT INTO b VALUES (1, 'old-b');");
        await ExecuteAsync(writer, "INSERT INTO c VALUES (1, 'old-c');");
        var pinned = await reader.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);
        const string query = "SELECT a.value, b.value, c.value FROM a JOIN b ON a.id = b.id LEFT JOIN c ON b.id = c.id;";

        // Act: one committed transaction changes both keys and values after the reader's snapshot.
        var update = await writer.BeginTransactionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(writer, "UPDATE a SET id = 2, value = 'new-a';");
        await ExecuteAsync(writer, "UPDATE b SET id = 2, value = 'new-b';");
        await ExecuteAsync(writer, "UPDATE c SET id = 2, value = 'new-c';");
        await update.CommitAsync(CancellationToken.None);

        // Assert
        (await RowsAsync(reader, query)).ShouldHaveSingleItem().ShouldBe(new object?[] { "old-a", "old-b", "old-c" });
        await pinned.RollbackAsync(CancellationToken.None);
        (await RowsAsync(reader, query)).ShouldHaveSingleItem().ShouldBe(new object?[] { "new-a", "new-b", "new-c" });
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
        var database = await engine.CreateDatabaseAsync("join");
        await using var writer = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(writer, "CREATE TABLE a (id INT, value TEXT);");
        await ExecuteAsync(writer, "CREATE TABLE b (id INT, value TEXT);");
        await ExecuteAsync(writer, "CREATE TABLE c (id INT, value TEXT);");
        if (indexed)
        {
            await ExecuteAsync(writer, "CREATE INDEX ix_b_id ON b(id);");
            await ExecuteAsync(writer, "CREATE INDEX ix_c_id ON c(id);");
        }
        await ExecuteAsync(writer, "INSERT INTO a VALUES (1, 'old-a');");
        await ExecuteAsync(writer, "INSERT INTO b VALUES (1, 'old-b');");
        await ExecuteAsync(writer, "INSERT INTO c VALUES (1, 'old-c');");
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        try
        {
            var captured = new SqlStatementContext(transaction, database.Coordinator);
            var request = SqlQueryRequest.FromSql("SELECT a.value, b.value, c.value FROM a JOIN b ON a.id = b.id LEFT JOIN c ON b.id = c.id;");
            var executor = new SqlQueryExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);

            // Act: replace both ON operands atomically after this statement captured its view.
            var update = await writer.BeginTransactionAsync(cancellationToken: CancellationToken.None);
            await ExecuteAsync(writer, "UPDATE a SET id = 2, value = 'new-a';");
            await ExecuteAsync(writer, "UPDATE b SET id = 2, value = 'new-b';");
            await ExecuteAsync(writer, "UPDATE c SET id = 2, value = 'new-c';");
            await update.CommitAsync(CancellationToken.None);
            await using var oldResult = (await executor.ExecuteAsync(request, captured, CancellationToken.None))
                .ShouldBeAssignableTo<DatabaseResultSet>();
            await using var newResult = (await executor.ExecuteAsync(request,
                new SqlStatementContext(transaction, database.Coordinator), CancellationToken.None))
                .ShouldBeAssignableTo<DatabaseResultSet>();

            // Assert: recapturing Transaction.Snapshot inside either input would violate the old result.
            (await ReadRowsAsync(oldResult)).ShouldHaveSingleItem().ShouldBe(new object?[] { "old-a", "old-b", "old-c" });
            (await ReadRowsAsync(newResult)).ShouldHaveSingleItem().ShouldBe(new object?[] { "new-a", "new-b", "new-c" });
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
        await ExecuteAsync(writer, "CREATE TABLE c (id INT);");
        await ExecuteAsync(writer, "CREATE INDEX ix_b_id ON b(id);");
        var transaction = await writer.BeginTransactionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(writer, "INSERT INTO a VALUES (1);");
        await ExecuteAsync(writer, "INSERT INTO b VALUES (1);");
        await ExecuteAsync(writer, "INSERT INTO c VALUES (1);");
        const string query = "SELECT a.id, b.id, c.id FROM a JOIN b ON a.id = b.id LEFT JOIN c ON b.id = c.id;";

        // Act / Assert
        (await RowsAsync(writer, query)).ShouldHaveSingleItem().ShouldBe(new object?[] { 1, 1, 1 });
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
        seekMetrics.JoinAccessPaths.ShouldBe(["join-seek:ix_large_key"]);
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
        scanMetrics.JoinAccessPaths.ShouldBe(["join-hash"]);
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
        MetricsOf(session).JoinAccessPaths.ShouldBe(["join-seek:ix_items_tenant_item"]);
        MetricsOf(session).RecordsExamined.ShouldBe(5);
    }

    /// <summary>A selective order key bounds stored reads independently of the total order count.</summary>
    /// <param name="size">The number of orders, each with three lines.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - JOIN: selective order key seeks both inputs (#1521)")]
    [InlineData(64)]
    [InlineData(512)]
    public async Task Join_SelectiveOrderKey_ShouldExamineOnlyOrderAndLines(int size)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("join");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE orders (id INT PRIMARY KEY);");
        await ExecuteAsync(session, "CREATE TABLE lines (order_id INT, line INT);");
        for (int start = 0; start < size; start += 64)
        {
            await ExecuteAsync(session, "INSERT INTO orders VALUES " + string.Join(",", Enumerable.Range(start, 64).Select(id => $"({id})")));
            await ExecuteAsync(session, "INSERT INTO lines VALUES " + string.Join(",", Enumerable.Range(start, 64)
                .SelectMany(id => Enumerable.Range(1, 3).Select(line => $"({id},{line})"))));
        }
        await ExecuteAsync(session, "CREATE INDEX ix_lines_order ON lines(order_id);");

        // Act
        var rows = await RowsAsync(session,
            "SELECT o.id, l.line FROM orders o JOIN lines l ON o.id = l.order_id WHERE o.id = @id ORDER BY l.line;",
            new Dictionary<string, object?> { ["id"] = 7 });
        var metrics = MetricsOf(session);

        // Assert
        rows.Select(row => string.Join("|", row)).ShouldBe(["7|1", "7|2", "7|3"]);
        metrics.JoinAccessPaths.ShouldBe(["join-seek:ix_lines_order"]);
        metrics.RecordsExamined.ShouldBe(4);
    }

    /// <summary>Unindexed input restrictions prevent Cartesian pair evaluation and allocation.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - JOIN: unindexed restrictions filter before constructing pairs (#1521)")]
    public async Task Join_UnindexedRestrictions_ShouldEvaluateOnlyFilteredPairs()
    {
        // Arrange
        int pairs = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("join-filter-pairs");
        builder.Functions.Add(SqlScalarFunction.Create("pair_seen", (long left, long right) =>
        {
            pairs++;
            return left + right;
        }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("join", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE a (id INT)");
        await ExecuteAsync(session, "CREATE TABLE b (id INT)");
        string values = string.Join(",", Enumerable.Range(0, 64).Select(id => $"({id})"));
        await ExecuteAsync(session, "INSERT INTO a VALUES " + values);
        await ExecuteAsync(session, "INSERT INTO b VALUES " + values);

        // Act
        var rows = await RowsAsync(session,
            "SELECT a.id,b.id FROM a JOIN b ON pair_seen(a.id,b.id) >= 0 WHERE a.id = 7 AND b.id = 7");

        // Assert
        rows.ShouldHaveSingleItem().ShouldBe(new object?[] { 7, 7 });
        pairs.ShouldBe(1);
        MetricsOf(session).RecordsExamined.ShouldBe(128);
    }

    /// <summary>A WHERE unique seek can drive the first INNER pair in either written direction.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - JOIN: WHERE unique seek drives the first pair and appears in EXPLAIN (#1521)")]
    public async Task Join_UniqueRestrictionOnSecondInput_ShouldReverseDriver()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("join");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE orders (id INT PRIMARY KEY)");
        await ExecuteAsync(session, "CREATE TABLE lines (order_id INT, line INT)");
        await ExecuteAsync(session, "INSERT INTO orders VALUES (1),(2)");
        await ExecuteAsync(session, "INSERT INTO lines VALUES (1,10),(1,11),(2,20)");
        await ExecuteAsync(session, "CREATE INDEX ix_lines ON lines(order_id)");
        const string sql = "SELECT o.id,l.line FROM lines l JOIN orders o ON l.order_id=o.id WHERE o.id=1 ORDER BY l.line";
        var plan = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql(sql).Statement.SqlExpression).ShouldBeOfType<SqlJoinPlan>();

        // Act
        var rows = await RowsAsync(session, sql);
        await using var description = SqlPlanner.Describe(plan);
        var described = await ReadRowsAsync(description);

        // Assert
        plan.FirstBinding.ShouldBe(1);
        plan.Inputs[1].Access.ShouldBeOfType<SqlIndexSeekPath>().EqualityValues.ShouldBe(new object?[] { 1 });
        rows.Select(row => string.Join("|", row)).ShouldBe(["1|10", "1|11"]);
        MetricsOf(session).RecordsExamined.ShouldBe(3);
        described.ShouldContain(row => Equals(row[1], 0) && Equals(row[2], "IndexSeek") &&
            Equals(row[3], SqlPlanner.DefaultSchema + ".orders") && Equals(row[5], "id = 1"));
    }

    /// <summary>Nullable-side WHERE cannot manufacture unmatched rows by removing existing matches.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - JOIN: LEFT separates nullable WHERE from nullable ON restrictions (#1521)")]
    public async Task Join_LeftRestrictions_ShouldPreserveNullExtensionBoundary()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("join");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE a (id INT PRIMARY KEY)");
        await ExecuteAsync(session, "CREATE TABLE b (id INT, marker TEXT)");
        await ExecuteAsync(session, "INSERT INTO a VALUES (1),(2),(3)");
        await ExecuteAsync(session, "INSERT INTO b VALUES (1,'keep'),(2,'other')");
        await ExecuteAsync(session, "CREATE INDEX ix_b ON b(id)");
        const string where = "SELECT a.id,b.marker FROM a LEFT JOIN b ON a.id=b.id WHERE b.marker='keep' OR b.id IS NULL ORDER BY a.id";
        const string on = "SELECT a.id,b.marker FROM a LEFT JOIN b ON a.id=b.id AND b.marker='keep' WHERE a.id>=2 ORDER BY a.id";
        SqlJoinPlan Plan(string sql) => new SqlPlanner(database.Catalog, null)
            .Plan(SqlQueryRequest.FromSql(sql).Statement.SqlExpression).ShouldBeOfType<SqlJoinPlan>();

        // Act
        var whereRows = await RowsAsync(session, where);
        var onRows = await RowsAsync(session, on);
        var laterInner = await RowsAsync(session,
            "SELECT a.id FROM a LEFT JOIN b ON a.id=b.id JOIN a c ON b.id IS NULL AND c.id=a.id ORDER BY a.id");

        // Assert
        Plan(where).Inputs[1].Filter.ShouldBeNull();
        Plan(on).Inputs[1].Filter.ShouldNotBeNull();
        Plan(on).Inputs[0].Access.ShouldBeOfType<SqlIndexSeekPath>().Lower.ShouldNotBeNull();
        whereRows.Select(row => string.Join("|", row)).ShouldBe(["1|keep", "3|"]);
        onRows.Select(row => string.Join("|", row)).ShouldBe(["2|", "3|"]);
        laterInner.ShouldHaveSingleItem().ShouldBe(new object?[] { 3 });
    }

    /// <summary>Volatile predicates retain joined-row cardinality; subquery predicates remain above the join.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - JOIN: volatile and subquery predicates retain their evaluation level (#1521)")]
    public async Task Join_VolatileAndSubqueryRestrictions_ShouldRemainResidual()
    {
        // Arrange
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("join-volatile");
        builder.Functions.Add(SqlScalarFunction.Create("visit", (long value) => { calls++; return value; }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("join", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE a (id BIGINT)");
        await ExecuteAsync(session, "CREATE TABLE b (id BIGINT)");
        await ExecuteAsync(session, "INSERT INTO a VALUES (1),(2)");
        await ExecuteAsync(session, "INSERT INTO b VALUES (1),(2),(3)");
        const string subquery = "SELECT a.id FROM a CROSS JOIN b WHERE a.id = (SELECT MAX(id) FROM a)";
        var plan = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql(subquery).Statement.SqlExpression)
            .ShouldBeOfType<SqlSubqueryPlan>().Input.ShouldBeOfType<SqlJoinPlan>();

        // Act
        var volatileRows = await RowsAsync(session, "SELECT a.id FROM a CROSS JOIN b WHERE COALESCE(visit(a.id),0)>0");
        var subqueryRows = await RowsAsync(session, subquery);

        // Assert
        volatileRows.Count.ShouldBe(6);
        calls.ShouldBe(6);
        plan.Inputs.ShouldAllBe(input => input.Filter == null && input.Access is SqlScanPath);
        subqueryRows.Select(row => row[0]).ShouldBe(new object?[] { 2L, 2L, 2L });
    }

    private static SqlDatabaseEngine CreateEngine()
        => SqlDatabaseEngine.Create("join-tests", new SqlDatabaseEngineOptions());

    private static async Task SeedUsersAsync(SqlDatabaseSession session)
    {
        await ExecuteAsync(session, "CREATE SCHEMA usr;");
        await ExecuteAsync(session, "CREATE TABLE usr.Users (Id INT PRIMARY KEY, FirstName TEXT, LastName TEXT);");
        await ExecuteAsync(session, "CREATE TABLE usr.UsersProfile (Id INT PRIMARY KEY, UserId INT, Email TEXT);");
        await ExecuteAsync(session, "INSERT INTO usr.Users VALUES (1, 'Ada', 'Lovelace'), (2, 'Grace', 'Hopper'), (3, 'Alan', 'Turing');");
        await ExecuteAsync(session, "INSERT INTO usr.UsersProfile VALUES (10, 1, 'ada@example.test'), (11, 1, 'ada-alt@example.test'), (20, 2, 'grace@example.test'), (99, 99, 'orphan@example.test');");
    }

    private static SqlStatementMetrics MetricsOf(SqlDatabaseSession session)
        => session.LastStatementMetrics.ShouldNotBeNull();

    private static Task<DatabaseResult> ExecuteAsync(SqlDatabaseSession session, string sql, IReadOnlyDictionary<string, object?>? parameters = null)
        => session.ExecuteAsync(sql, parameters, CancellationToken.None).AsTask();

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        await using var result = (await ExecuteAsync(session, sql, parameters)).ShouldBeAssignableTo<DatabaseResultSet>();
        return await ReadRowsAsync(result);
    }

    private static async Task<List<object?[]>> ReadRowsAsync(DatabaseResultSet result)
    {
        result.Status.ShouldBe(DatabaseResultStatus.Success);
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
