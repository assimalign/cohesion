using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlTransactionSpellingTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Transactions: aliases open one root scope and publish on commit")]
    [InlineData("START TRANSACTION", "END")]
    [InlineData("START TRANSACTION", "COMMIT WORK")]
    [InlineData("BEGIN WORK", "END")]
    [InlineData("BEGIN WORK", "COMMIT WORK")]
    public async Task ExecuteAsync_TransactionAliases_ShouldPublishToObserver(string begin, string commit)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("transaction-spellings", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("app");
        await using var writer = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await using var observer = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(writer, "CREATE TABLE t (id INT);");

        // Act / Assert
        await ExecuteAsync(writer, begin);
        var transaction = writer.CurrentTransaction.ShouldNotBeNull();
        transaction.State.ShouldBe(TransactionState.Active);
        foreach (string nested in new[] { "START TRANSACTION", "BEGIN WORK" })
        {
            var error = await writer.ExecuteAsync(nested, cancellationToken: CancellationToken.None);
            error.Status.ShouldBe(DatabaseResultStatus.Error);
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS04001");
            writer.CurrentTransaction.ShouldBeSameAs(transaction);
        }
        await ExecuteAsync(writer, "INSERT INTO t VALUES (1);");
        (await RowsAsync(observer, "SELECT id FROM t;")).ShouldBeEmpty();
        await ExecuteAsync(writer, commit);
        transaction.State.ShouldBe(TransactionState.Committed);
        writer.CurrentTransaction.ShouldBeNull();
        (await RowsAsync(observer, "SELECT id FROM t;")).ShouldHaveSingleItem().ShouldBe([1]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Transactions: ROLLBACK WORK undoes INSERT UPDATE and DELETE")]
    public async Task ExecuteAsync_RollbackWork_ShouldUndoEveryMutation()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("rollback-work", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("app");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT, name TEXT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1, 'ada'), (2, 'grace');");
        await ExecuteAsync(session, "BEGIN WORK;");
        var transaction = session.CurrentTransaction.ShouldNotBeNull();

        // Act
        await ExecuteAsync(session, "INSERT INTO t VALUES (3, 'alan');");
        await ExecuteAsync(session, "UPDATE t SET name = 'changed' WHERE id = 1;");
        await ExecuteAsync(session, "DELETE FROM t WHERE id = 2;");
        var changed = await RowsAsync(session, "SELECT id, name FROM t ORDER BY id;");
        await ExecuteAsync(session, "ROLLBACK WORK;");

        // Assert
        changed.Count.ShouldBe(2);
        changed[0].ShouldBe([1, "changed"]);
        changed[1].ShouldBe([3, "alan"]);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        await using var observer = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        var rows = await RowsAsync(observer, "SELECT id, name FROM t ORDER BY id;");
        rows.Count.ShouldBe(2);
        rows[0].ShouldBe([1, "ada"]);
        rows[1].ShouldBe([2, "grace"]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Transactions: aliases keep canonical state and DDL diagnostics")]
    public async Task ExecuteAsync_TransactionAliasStateErrors_ShouldPreserveCanonicalDiagnostics()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alias-diagnostics", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("app");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);

        // Act
        var missing = await session.ExecuteAsync("END", cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "START TRANSACTION;");
        var transaction = session.CurrentTransaction.ShouldNotBeNull();
        var ddl = await session.ExecuteAsync("CREATE TABLE t (id INT);", cancellationToken: CancellationToken.None);

        // Assert
        missing.Status.ShouldBe(DatabaseResultStatus.Error);
        var diagnostic = missing.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CS04002");
        diagnostic.Message.ShouldBe("COMMIT requires an open transaction.");
        ddl.Status.ShouldBe(DatabaseResultStatus.Error);
        ddl.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS04003");
        session.CurrentTransaction.ShouldBeSameAs(transaction);
        transaction.State.ShouldBe(TransactionState.Active);
        await ExecuteAsync(session, "ROLLBACK WORK;");
        await ExecuteAsync(session, "CREATE TABLE t (id INT);");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Transactions: start work and abort columns and CASE END execute unchanged")]
    public async Task ExecuteAsync_ContextualTransactionWords_ShouldRemainOrdinaryColumns()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("transaction-columns", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("app");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);

        // Act
        await ExecuteAsync(session, "CREATE TABLE shifts (start INT, work TEXT, abort BOOLEAN);");
        await ExecuteAsync(session, "INSERT INTO shifts (start, work, abort) VALUES (1, 'day', FALSE);");
        var columns = await RowsAsync(session, "SELECT start, work, abort FROM shifts;");
        var expression = await RowsAsync(session, "SELECT CASE WHEN start = 1 THEN work ELSE 'other' END FROM shifts;");

        // Assert
        columns.ShouldHaveSingleItem().ShouldBe([1, "day", false]);
        expression.ShouldHaveSingleItem().ShouldBe(["day"]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Transactions: every rejected typed request preserves the active scope")]
    [InlineData("START", "CS00003")]
    [InlineData("START WORK", "CS00003")]
    [InlineData("BEGIN SAVEPOINT marker", "CS00003")]
    [InlineData("ROLLBACK TO marker", "CS00003")]
    [InlineData("BEGIN WORK TRANSACTION", "CS00003")]
    [InlineData("COMMIT WORK WORK", "CS00003")]
    [InlineData("END WORK", "CS00003")]
    [InlineData("END TRANSACTION", "CS00003")]
    [InlineData("BEGIN TRAN", "CS00003")]
    [InlineData("BEGIN TRANSACTION named", "CS00003")]
    [InlineData("END IF", "CS00003")]
    [InlineData("COMMIT PREPARED 'x'", "CS00003")]
    [InlineData("START TRANSACTION ISOLATION LEVEL READ COMMITTED", "CS00003")]
    [InlineData("START TRANSACTION READ ONLY", "CS00003")]
    [InlineData("BEGIN WORK ISOLATION LEVEL SERIALIZABLE", "CS00003")]
    [InlineData("ROLLBACK WORK TO SAVEPOINT a", "CS00003")]
    [InlineData("COMMIT AND NO CHAIN", "CS00003")]
    [InlineData("COMMIT AND CHAIN", "CS00003")]
    [InlineData("ROLLBACK AND NO CHAIN", "CS00003")]
    [InlineData("END AND NO CHAIN", "CS00003")]
    [InlineData("ABORT", "CS00002")]
    [InlineData("ABORT WORK", "CS00002")]
    [InlineData("ABORT TRANSACTION", "CS00002")]
    [InlineData("SAVEPOINT a", "CS00002")]
    [InlineData("RELEASE SAVEPOINT a", "CS00002")]
    [InlineData("SET TRANSACTION ISOLATION LEVEL READ COMMITTED", "CS00002")]
    [InlineData("PREPARE TRANSACTION 'x'", "CS00002")]
    [InlineData("\"START\" TRANSACTION", "CS00002")]
    [InlineData("START \"TRANSACTION\"", "CS00003")]
    [InlineData("BEGIN \"WORK\"", "CS00003")]
    public async Task ExecuteAsync_RejectedTypedTransactionForm_ShouldKeepScopeActive(string sql, string code)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("typed-alias-rejection", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("app");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "START TRANSACTION;");
        var transaction = session.CurrentTransaction.ShouldNotBeNull();
        var invalid = new SqlQueryParser().Parse(sql + ";").ShouldBeOfType<SqlQueryStatement>();
        invalid.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ShouldHaveSingleItem().Code.ShouldBe(code);

        // Act / Assert
        try
        {
            var result = await session.ExecuteAsync(new SqlQueryRequest(invalid), cancellationToken: CancellationToken.None);
            result.Status.ShouldBe(DatabaseResultStatus.Error);
            result.Diagnostics.ShouldNotBeNull().ShouldContain(diagnostic => diagnostic.Code == code);
        }
        catch (DatabaseParseException)
        {
            // The shared-diagnostics work moves this rejection from Error to DatabaseParseException.
        }
        session.CurrentTransaction.ShouldBeSameAs(transaction);
        transaction.State.ShouldBe(TransactionState.Active);
        await ExecuteAsync(session, "ROLLBACK WORK;");
    }

    private static async Task ExecuteAsync(SqlDatabaseSession session, string sql)
        => (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).Status.ShouldBe(DatabaseResultStatus.Success);

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql)
    {
        await using var result = (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None))
            .ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
        }
        return rows;
    }
}
