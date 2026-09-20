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

/// <summary>Proves subquery truth semantics, cardinality, composition, snapshot visibility and constrained inserts.</summary>
public sealed class SqlSubqueryExecutionTests
{
    /// <summary>Membership distinguishes matching, nonmatching, null-containing and empty sets.</summary>
    /// <param name="predicate">The membership predicate.</param>
    /// <param name="expected">The matching non-null keys.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Subquery: IN and NOT IN preserve SQL null and empty-set semantics")]
    [InlineData("id IN (SELECT id FROM choices WHERE id IS NOT NULL)", new[] { 2 })]
    [InlineData("id NOT IN (SELECT id FROM choices WHERE id IS NOT NULL)", new[] { 1, 3 })]
    [InlineData("id IN (SELECT id FROM choices)", new[] { 2 })]
    [InlineData("id NOT IN (SELECT id FROM choices)", new int[0])]
    [InlineData("id IN (SELECT id FROM choices WHERE id < 0)", new int[0])]
    public async Task In_SubqueryResults_ShouldPreserveThreeValuedLogic(string predicate, int[] expected)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("subquery");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedMembershipAsync(session);

        // Act / Assert
        (await RowsAsync(session, $"SELECT id FROM candidates WHERE {predicate} ORDER BY id;"))
            .Select(row => row[0]).ShouldBe(expected.Cast<object?>());
    }

    /// <summary>NOT IN against an empty set is true even for a null left operand.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Subquery: NOT IN empty set includes the null outer operand")]
    public async Task NotIn_EmptySubquery_ShouldReturnEveryOuterRow()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("subquery");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedMembershipAsync(session);

        // Act / Assert
        (await RowsAsync(session, "SELECT id FROM candidates WHERE id NOT IN (SELECT id FROM choices WHERE id < 0) ORDER BY id;"))
            .Select(row => row[0]).ShouldBe(new object?[] { null, 1, 2, 3 });
    }

    /// <summary>Projected membership exposes UNKNOWN as null rather than silently treating it as false.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Subquery: projected membership retains UNKNOWN")]
    public async Task In_NullContainingSubquery_ShouldProjectUnknownForNonmatches()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("subquery");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedMembershipAsync(session);

        // Act
        var rows = await RowsAsync(session,
            "SELECT id, id IN (SELECT id FROM choices), id NOT IN (SELECT id FROM choices) FROM candidates ORDER BY id;");

        // Assert
        rows.Count.ShouldBe(4);
        rows[0].ShouldBe(new object?[] { null, null, null });
        rows[1].ShouldBe(new object?[] { 1, null, null });
        rows[2].ShouldBe(new object?[] { 2, true, false });
        rows[3].ShouldBe(new object?[] { 3, null, null });
    }

    /// <summary>EXISTS checks rows, including null projections; its parsed negation flag reverses existence.</summary>
    /// <param name="predicate">The existence predicate.</param>
    /// <param name="expectedCount">The expected outer row count.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Subquery: EXISTS and NOT EXISTS handle populated and empty results")]
    [InlineData("EXISTS (SELECT id FROM choices)", 4)]
    [InlineData("NOT EXISTS (SELECT id FROM choices)", 0)]
    [InlineData("EXISTS (SELECT id FROM choices WHERE id IS NULL)", 4)]
    [InlineData("EXISTS (SELECT id FROM choices WHERE id < 0)", 0)]
    [InlineData("NOT EXISTS (SELECT id FROM choices WHERE id < 0)", 4)]
    public async Task Exists_SubqueryResults_ShouldTestRowExistence(string predicate, int expectedCount)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("subquery");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedMembershipAsync(session);

        // Act / Assert
        (await RowsAsync(session, $"SELECT id FROM candidates WHERE {predicate};")).Count.ShouldBe(expectedCount);
    }

    /// <summary>Scalar results retain their column type and produce null when their source returns no rows.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Subquery: scalar projection and predicate retain typed values and empty nulls")]
    public async Task Scalar_ProjectionAndPredicate_ShouldReturnOneValueOrNull()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("subquery");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedMembershipAsync(session);

        // Act
        await using var result = (await ExecuteAsync(session,
            "SELECT (SELECT id FROM choices WHERE id IS NOT NULL) AS picked, " +
            "(SELECT id FROM choices WHERE id < 0) AS absent FROM candidates WHERE id = 1;"))
            .ShouldBeAssignableTo<QueryResultSet>();

        // Assert
        result.Columns.Select(column => column.Name).ShouldBe(["picked", "absent"]);
        result.Columns.Select(column => column.Type).ShouldBe([DatabaseType.Int32, DatabaseType.Int32]);
        result.Columns.ShouldAllBe(column => column.IsNullable);
        (await ReadRowsAsync(result)).ShouldHaveSingleItem().ShouldBe(new object?[] { 2, null });
        (await RowsAsync(session, "SELECT id FROM candidates WHERE id = (SELECT id FROM choices WHERE id IS NOT NULL);"))
            .ShouldHaveSingleItem()[0].ShouldBe(2);
        (await RowsAsync(session, "SELECT id FROM candidates WHERE id = (SELECT id FROM choices WHERE id < 0);"))
            .ShouldBeEmpty();
    }

    /// <summary>Two scalar rows fail instead of arbitrarily choosing the first row.</summary>
    /// <param name="statement">The scalar expression position.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Subquery: scalar cardinality above one is rejected")]
    [InlineData("SELECT (SELECT id FROM choices) FROM candidates WHERE id = 1;")]
    [InlineData("SELECT id FROM candidates WHERE id = (SELECT id FROM choices);")]
    public async Task Scalar_MultipleRows_ShouldReject(string statement)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("subquery");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedMembershipAsync(session);

        // Act / Assert
        var error = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, statement));
        error.Message.ShouldContain("scalar");
        error.Message.ShouldContain("one row");
    }

    /// <summary>IN and scalar forms require one output column even when the source is empty.</summary>
    /// <param name="statement">The invalid output shape.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Subquery: scalar and IN require one source column")]
    [InlineData("SELECT (SELECT id, id FROM choices WHERE id < 0) FROM candidates WHERE id = 1;")]
    [InlineData("SELECT id FROM candidates WHERE id IN (SELECT id, id FROM choices WHERE id < 0);")]
    public async Task Subquery_MultipleColumns_ShouldRejectBeforeReadingRows(string statement)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("subquery");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedMembershipAsync(session);

        // Act / Assert
        (await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, statement))).Message.ShouldContain("column");
    }

    /// <summary>Subqueries compose in ON, WHERE, projection, HAVING and ORDER BY with grouped and joined inputs.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Subquery: joins grouping HAVING ordering and LIMIT compose")]
    public async Task Subquery_ComposedClauses_ShouldPreserveRelationalResults()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("subquery");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE sales (id INT, category TEXT, amount INT);");
        await ExecuteAsync(session, "CREATE TABLE labels (id INT, category TEXT);");
        await ExecuteAsync(session, "INSERT INTO sales VALUES (1, 'A', 4), (2, 'A', 6), (3, 'B', 8), (4, 'C', 3);");
        await ExecuteAsync(session, "INSERT INTO labels VALUES (1, 'A'), (2, 'B');");

        // Act / Assert
        (await RowsAsync(session,
            "SELECT s.id, l.category FROM sales s JOIN labels l ON s.category = l.category " +
            "AND s.amount >= (SELECT MIN(amount) FROM sales WHERE category = 'B') " +
            "WHERE s.id IN (SELECT id FROM sales WHERE amount > 0) ORDER BY s.id LIMIT 1;"))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { 3, "B" });
        (await RowsAsync(session,
            "SELECT category, SUM(amount), (SELECT MAX(id) FROM labels) FROM sales " +
            "WHERE category IN (SELECT category FROM labels) GROUP BY category " +
            "HAVING SUM(amount) > (SELECT MIN(amount) FROM sales WHERE category = 'B') " +
            "ORDER BY SUM(amount) + (SELECT MIN(id) FROM labels) DESC LIMIT 1;"))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { "A", 10m, 2 });
        (await RowsAsync(session,
            "SELECT category FROM labels WHERE category IN " +
            "(SELECT s.category FROM sales s JOIN labels l ON s.category = l.category " +
            "GROUP BY s.category HAVING SUM(s.amount) > 8 ORDER BY s.category LIMIT 1);"))
            .ShouldHaveSingleItem()[0].ShouldBe("A");
    }

    /// <summary>Nested queries resolve local names independently, including aliases shared by nested scopes.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Subquery: nesting and locally shadowed aliases execute")]
    public async Task Subquery_NestedScopes_ShouldBindLocalColumns()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("subquery");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedMembershipAsync(session);

        // Act / Assert
        (await RowsAsync(session,
            "SELECT c.id FROM candidates c WHERE c.id IN (SELECT c.id FROM choices c " +
            "WHERE c.id = (SELECT MAX(id) FROM choices WHERE EXISTS (SELECT id FROM candidates WHERE id = 1)));")).ShouldHaveSingleItem()[0].ShouldBe(2);
    }

    /// <summary>The documented nesting ceiling is accepted exactly and rejected clearly above the ceiling.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Subquery: depth 32 executes and depth 33 has a capability diagnostic")]
    public async Task Subquery_NestingLimit_ShouldFailBeforeStackExhaustion()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("subquery");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedMembershipAsync(session);
        string query = "SELECT id FROM candidates WHERE id = 1";
        for (int depth = 0; depth < 32; depth++) { query = $"SELECT ({query}) FROM candidates WHERE id = 1"; }

        // Act / Assert
        (await RowsAsync(session, query)).ShouldHaveSingleItem()[0].ShouldBe(1);
        var error = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, $"SELECT ({query}) FROM candidates WHERE id = 1"));
        error.Message.ShouldContain("COHDBL001", Case.Sensitive);
        error.Message.ShouldContain("32", Case.Sensitive);
    }

    /// <summary>Outer references have a capability diagnostic, including when the outer table is empty.</summary>
    /// <param name="statement">The correlated expression.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Subquery: correlated references are precisely rejected")]
    [InlineData("SELECT c.id FROM candidates c WHERE EXISTS (SELECT x.id FROM choices x WHERE x.id = c.id);")]
    [InlineData("SELECT (SELECT x.id FROM choices x WHERE x.id = c.id) FROM candidates c;")]
    [InlineData("SELECT c.id FROM candidates c WHERE c.id IN (SELECT x.id FROM choices x WHERE x.id = c.id);")]
    public async Task Subquery_CorrelatedReference_ShouldReportCapabilityDiagnostic(string statement)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("subquery");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE candidates (id INT);");
        await ExecuteAsync(session, "CREATE TABLE choices (id INT);");

        // Act / Assert
        var error = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, statement));
        error.Message.ShouldContain("COHDBL001", Case.Sensitive);
        error.Message.ShouldContain("correlat");
    }

    /// <summary>ReadCommitted inner and outer plans consume the statement's captured view after concurrent changes.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Subquery: all forms share the captured MVCC statement snapshot")]
    public async Task Subquery_ConcurrentCommit_ShouldKeepCapturedSnapshot()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("subquery");
        await using var writer = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(writer, "CREATE TABLE candidates (id INT);");
        await ExecuteAsync(writer, "CREATE TABLE choices (id INT);");
        await ExecuteAsync(writer, "INSERT INTO candidates VALUES (1);");
        await ExecuteAsync(writer, "INSERT INTO choices VALUES (1);");
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        try
        {
            var captured = new SqlStatementContext(transaction, database.Coordinator);
            var request = SqlQueryRequest.FromSql(
                "SELECT id, (SELECT id FROM choices) FROM candidates WHERE id IN (SELECT id FROM choices) " +
                "AND EXISTS (SELECT id FROM choices WHERE id IN (SELECT id FROM candidates));");
            var executor = new SqlQueryExecutor(database.DataStorage, database.Catalog, database.IndexManager);

            // Act: change both outer and nested inputs after the ReadCommitted statement captured its view.
            var update = await writer.BeginTransactionAsync(cancellationToken: CancellationToken.None);
            await ExecuteAsync(writer, "UPDATE candidates SET id = 2;");
            await ExecuteAsync(writer, "UPDATE choices SET id = 2;");
            await update.CommitAsync(CancellationToken.None);
            await using var oldResult = (await executor.ExecuteAsync(request, captured, CancellationToken.None))
                .ShouldBeAssignableTo<QueryResultSet>();
            await using var newResult = (await executor.ExecuteAsync(request,
                new SqlStatementContext(transaction, database.Coordinator), CancellationToken.None))
                .ShouldBeAssignableTo<QueryResultSet>();

            // Assert
            (await ReadRowsAsync(oldResult)).ShouldHaveSingleItem().ShouldBe(new object?[] { 1, 1 });
            (await ReadRowsAsync(newResult)).ShouldHaveSingleItem().ShouldBe(new object?[] { 2, 2 });
        }
        finally
        {
            await database.Coordinator.RollbackAsync(transaction, CancellationToken.None);
        }
    }

    /// <summary>Catalog views nested below ordinary SELECT and INSERT plans receive the statement catalog snapshot.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Subquery: system-view children and INSERT SELECT share catalog capture")]
    public async Task Subquery_SystemViewChildren_ShouldExecuteWithCapturedCatalog()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("subquery");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE requested (name TEXT);");
        await ExecuteAsync(session, "CREATE TABLE copied (name TEXT);");
        await ExecuteAsync(session, "INSERT INTO requested VALUES ('requested'), ('missing');");

        // Act / Assert
        (await RowsAsync(session,
            "SELECT name, (SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES) FROM requested " +
            "WHERE name IN (SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES);"))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { "requested", 2L });
        await ExecuteAsync(session,
            "INSERT INTO copied SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES " +
            "WHERE TABLE_NAME IN (SELECT name FROM requested);");
        (await RowsAsync(session, "SELECT name FROM copied;")).ShouldHaveSingleItem()[0].ShouldBe("requested");
        await ExecuteAsync(session,
            "INSERT INTO copied SELECT name FROM requested " +
            "WHERE name IN (SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES);");
        (await RowsAsync(session, "SELECT name FROM copied;")).Count.ShouldBe(2);
    }

    /// <summary>Materialized scalar values and set members preserve the source string comparison collation.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Subquery: scalar and membership retain source collation")]
    public async Task Subquery_CollatedValues_ShouldCompareUsingSourceCollation()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("subquery");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE names (id INT, name TEXT COLLATE case_insensitive);");
        await ExecuteAsync(session, "INSERT INTO names VALUES (1, 'Alice');");

        // Act / Assert
        (await RowsAsync(session, "SELECT id FROM names WHERE 'alice' IN (SELECT name FROM names);"))
            .ShouldHaveSingleItem()[0].ShouldBe(1);
        (await RowsAsync(session, "SELECT id FROM names WHERE 'alice' = (SELECT name FROM names);"))
            .ShouldHaveSingleItem()[0].ShouldBe(1);
        (await RowsAsync(session, "SELECT id FROM names WHERE 'alice' COLLATE binary IN (SELECT name FROM names);"))
            .ShouldBeEmpty();
    }

    /// <summary>INSERT SELECT maps explicit target columns, fills defaults and honors source filtering and ordering.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - INSERT SELECT: explicit columns defaults and empty sources execute")]
    public async Task InsertSelect_ExplicitColumns_ShouldInsertSelectedRowsAndApplyDefaults()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("subquery");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE source_rows (id INT, label TEXT);");
        await ExecuteAsync(session, "CREATE TABLE target_rows (id INT PRIMARY KEY, label TEXT NOT NULL, quantity INT NOT NULL DEFAULT 5 CHECK (quantity > 0));");
        await ExecuteAsync(session, "INSERT INTO source_rows VALUES (1, 'one'), (2, 'two'), (3, 'three');");

        // Act
        await ExecuteAsync(session, "INSERT INTO target_rows (label, id) SELECT label, id FROM source_rows WHERE id >= 2 ORDER BY id DESC LIMIT 1;");
        await ExecuteAsync(session, "INSERT INTO target_rows (id, label) SELECT id, label FROM source_rows WHERE id < 0;");

        // Assert
        (await RowsAsync(session, "SELECT id, label, quantity FROM target_rows;"))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { 3, "three", 5 });
    }

    /// <summary>Self-copy reads the entire original source once and explicit rollback removes all produced rows.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - INSERT SELECT: self-source is materialized and transaction rollback is atomic")]
    public async Task InsertSelect_SameTable_ShouldReadSourceBeforeWritingAndRollback()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("subquery");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE items (id INT PRIMARY KEY);");
        await ExecuteAsync(session, "INSERT INTO items VALUES (1), (2);");
        var transaction = await session.BeginTransactionAsync(cancellationToken: CancellationToken.None);

        // Act
        await ExecuteAsync(session, "INSERT INTO items SELECT id + 10 FROM items;");

        // Assert
        (await RowsAsync(session, "SELECT id FROM items ORDER BY id;")).Select(row => row[0])
            .ShouldBe(new object?[] { 1, 2, 11, 12 });
        await transaction.RollbackAsync(CancellationToken.None);
        (await RowsAsync(session, "SELECT id FROM items ORDER BY id;")).Select(row => row[0])
            .ShouldBe(new object?[] { 1, 2 });
    }

    /// <summary>Source rows receive the same constraint errors as literal rows and failed statements insert nothing.</summary>
    /// <param name="definition">The target constraint definition.</param>
    /// <param name="sourceValues">Source and literal row values.</param>
    /// <param name="constraintKind">The expected constraint name.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - INSERT SELECT: UNIQUE foreign-key and CHECK violations match literal inserts")]
    [InlineData("id INT UNIQUE", "(1), (1)", "UNIQUE")]
    [InlineData("id INT REFERENCES parents(id)", "(1), (99)", "FOREIGN KEY")]
    [InlineData("id INT CHECK (id > 0)", "(1), (-1)", "CHECK")]
    public async Task InsertSelect_ConstraintViolation_ShouldMatchLiteralInsertAndRemainAtomic(
        string definition, string sourceValues, string constraintKind)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("subquery");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE parents (id INT PRIMARY KEY);");
        await ExecuteAsync(session, "INSERT INTO parents VALUES (1);");
        await ExecuteAsync(session, "CREATE TABLE source_rows (id INT);");
        await ExecuteAsync(session, $"CREATE TABLE target_rows ({definition});");
        await ExecuteAsync(session, $"INSERT INTO source_rows VALUES {sourceValues};");

        // Act / Assert
        var literalError = await Should.ThrowAsync<SqlConstraintViolationException>(
            () => ExecuteAsync(session, $"INSERT INTO target_rows VALUES {sourceValues};"));
        var selectError = await Should.ThrowAsync<SqlConstraintViolationException>(
            () => ExecuteAsync(session, "INSERT INTO target_rows SELECT id FROM source_rows;"));
        literalError.ConstraintKind.ShouldBe(constraintKind);
        selectError.ConstraintKind.ShouldBe(literalError.ConstraintKind);
        selectError.ConstraintName.ShouldBe(literalError.ConstraintName);
        (await RowsAsync(session, "SELECT id FROM target_rows;")).ShouldBeEmpty();
    }

    /// <summary>A failed source insert in an explicit transaction preserves prior work and releases statement changes.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - INSERT SELECT: failure preserves preceding transaction statements")]
    public async Task InsertSelect_FailedExplicitStatement_ShouldPreservePriorWrites()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("subquery");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE source_rows (id INT);");
        await ExecuteAsync(session, "CREATE TABLE target_rows (id INT UNIQUE);");
        await ExecuteAsync(session, "INSERT INTO source_rows VALUES (2), (2);");
        var transaction = await session.BeginTransactionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "INSERT INTO target_rows VALUES (1);");

        // Act / Assert
        await Should.ThrowAsync<SqlConstraintViolationException>(
            () => ExecuteAsync(session, "INSERT INTO target_rows SELECT id FROM source_rows;"));
        (await RowsAsync(session, "SELECT id FROM target_rows;")).ShouldHaveSingleItem()[0].ShouldBe(1);
        await ExecuteAsync(session, "INSERT INTO target_rows VALUES (2);");
        await transaction.CommitAsync(CancellationToken.None);
        (await RowsAsync(session, "SELECT id FROM target_rows ORDER BY id;")).Select(row => row[0]).ShouldBe(new object?[] { 1, 2 });
    }

    /// <summary>Nullability is enforced for selected values using the same error as a literal insert.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - INSERT SELECT: NOT NULL enforcement matches literal inserts")]
    public async Task InsertSelect_NullValue_ShouldMatchLiteralNullabilityFailure()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("subquery");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE source_rows (id INT);");
        await ExecuteAsync(session, "CREATE TABLE target_rows (id INT NOT NULL);");
        await ExecuteAsync(session, "INSERT INTO source_rows VALUES (1), (NULL);");

        // Act / Assert
        var literalError = await Should.ThrowAsync<DatabaseException>(
            () => ExecuteAsync(session, "INSERT INTO target_rows VALUES (1), (NULL);"));
        var selectError = await Should.ThrowAsync<DatabaseException>(
            () => ExecuteAsync(session, "INSERT INTO target_rows SELECT id FROM source_rows;"));
        selectError.Message.ShouldBe(literalError.Message);
        (await RowsAsync(session, "SELECT id FROM target_rows;")).ShouldBeEmpty();
    }

    /// <summary>Output count and incompatible declared types are checked even if the SELECT produces zero rows.</summary>
    /// <param name="projection">The invalid source projection.</param>
    /// <param name="empty">Whether the source is empty.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - INSERT SELECT: source column count and type compatibility are validated")]
    [InlineData("id, label", false)]
    [InlineData("id, label", true)]
    [InlineData("label", false)]
    [InlineData("payload", false)]
    [InlineData("payload", true)]
    public async Task InsertSelect_IncompatibleShape_ShouldReject(string projection, bool empty)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("subquery");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE source_rows (id INT, label TEXT, payload BINARY);");
        await ExecuteAsync(session, "CREATE TABLE target_rows (id INT);");
        if (!empty) { await ExecuteAsync(session, "INSERT INTO source_rows VALUES (1, 'not-an-integer', NULL);"); }

        // Act / Assert
        await Should.ThrowAsync<DatabaseException>(
            () => ExecuteAsync(session, $"INSERT INTO target_rows SELECT {projection} FROM source_rows;"));
        (await RowsAsync(session, "SELECT id FROM target_rows;")).ShouldBeEmpty();
    }

    /// <summary>CHECK remains row-local; restoring general subqueries never publishes a subquery CHECK constraint.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Subquery: CHECK constraints continue to reject subqueries")]
    public async Task Check_Subquery_ShouldRejectBeforeTablePublication()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("subquery");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);

        // Act / Assert
        var error = await Should.ThrowAsync<DatabaseException>(
            () => ExecuteAsync(session, "CREATE TABLE invalid_check (id INT CHECK (id IN (SELECT 1)));"));
        error.Message.ShouldContain("subquer");
        database.Catalog.TryGetTable("dbo", "invalid_check", out _).ShouldBeFalse();
    }

    private static SqlDatabaseEngine CreateEngine()
        => SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "subquery-tests" });

    private static async Task SeedMembershipAsync(IDatabaseSession session)
    {
        await ExecuteAsync(session, "CREATE TABLE candidates (id INT);");
        await ExecuteAsync(session, "CREATE TABLE choices (id INT);");
        await ExecuteAsync(session, "INSERT INTO candidates VALUES (1), (2), (3), (NULL);");
        await ExecuteAsync(session, "INSERT INTO choices VALUES (2), (NULL);");
    }

    private static Task<QueryResult> ExecuteAsync(IDatabaseSession session, string sql)
        => session.ExecuteAsync(sql, cancellationToken: CancellationToken.None).AsTask();

    private static async Task<List<object?[]>> RowsAsync(IDatabaseSession session, string sql)
    {
        await using var result = (await ExecuteAsync(session, sql)).ShouldBeAssignableTo<QueryResultSet>();
        return await ReadRowsAsync(result);
    }

    private static async Task<List<object?[]>> ReadRowsAsync(QueryResultSet result)
    {
        result.Status.ShouldBe(QueryResultStatus.Success);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
        }
        return rows;
    }
}
