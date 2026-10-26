using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentTextTransactionTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - Text transactions: Snapshot and ReadCommitted own writes commit and rollback")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_TransactionControl_ShouldCommitAndRollbackOnOwningSession(bool readCommitted, bool typed)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("text-transactions", new());
        var database = await engine.CreateDatabaseAsync("documents", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await using var observer = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);

        // Act / Assert
        var begin = await ExecuteAsync(session, readCommitted ? "START TRANSACTION ISOLATION LEVEL READ COMMITTED" : "BEGIN", typed);
        begin.Status.ShouldBe(DatabaseResultStatus.Success);
        begin.AffectedCount.ShouldBe(0);
        var transaction = session.CurrentTransaction.ShouldNotBeNull();
        transaction.IsolationLevel.ShouldBe(readCommitted ? IsolationLevel.ReadCommitted : IsolationLevel.Snapshot);
        await ExecuteAsync(session, "INSERT INTO items (KEY, VALUE) VALUES ('pending', {'n': 1})", typed);
        (await CountAsync(session)).ShouldBe(1);
        (await CountAsync(observer)).ShouldBe(0);
        await ExecuteAsync(session, "COMMIT WORK", typed);
        transaction.State.ShouldBe(TransactionState.Committed);
        session.CurrentTransaction.ShouldBeNull();
        (await CountAsync(observer)).ShouldBe(1);
        await ExecuteAsync(session, "BEGIN TRAN", typed);
        await ExecuteAsync(session, "DELETE FROM items", typed);
        await ExecuteAsync(session, "ROLLBACK TRANSACTION", typed);
        (await CountAsync(observer)).ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Text transactions: statement failures retain CD04101 and rollback recovers")]
    [InlineData("SELECT FROM items", false)]
    [InlineData("SELECT * FROM absent", false)]
    [InlineData("CREATE COLLECTION denied", false)]
    [InlineData("SELECT FROM items", true)]
    [InlineData("SELECT * FROM absent", true)]
    [InlineData("CREATE COLLECTION denied", true)]
    public async Task ExecuteAsync_FailedStatement_ShouldRefuseUntilTextRollback(string failure, bool typed)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("text-failures", new());
        var database = await engine.CreateDatabaseAsync("documents", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await ExecuteAsync(session, "BEGIN", typed);
        await ExecuteAsync(session, "INSERT INTO items (KEY, VALUE) VALUES ('pending', 1)", typed);

        // Act
        var original = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, failure, typed));
        var transaction = session.CurrentTransaction.ShouldNotBeNull();
        var refused = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "DELETE FROM items", typed));
        var malformed = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("invalid", cancellationToken: CancellationToken.None));
        await ExecuteAsync(session, "ROLLBACK", typed);

        // Assert
        transaction.State.ShouldBe(TransactionState.RolledBack);
        refused.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD04101");
        refused.InnerException.ShouldBeSameAs(original);
        malformed.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD04101");
        session.CurrentTransaction.ShouldBeNull();
        (await CountAsync(session)).ShouldBe(0);
        await ExecuteAsync(session, "BEGIN", typed);
        await ExecuteAsync(session, "COMMIT", typed);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Text transactions: every OQL DDL form faults the explicit transaction before mutation")]
    [InlineData("CREATE COLLECTION other")]
    [InlineData("DROP COLLECTION items")]
    [InlineData("CREATE INDEX added ON items (n)")]
    [InlineData("DROP INDEX ix ON items")]
    public async Task ExecuteAsync_DdlInTransaction_ShouldRefuseAndAbort(string statement)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("text-ddl", new());
        var database = await engine.CreateDatabaseAsync("documents", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE COLLECTION items", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX ix ON items (n)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("BEGIN", cancellationToken: CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(statement, cancellationToken: CancellationToken.None));

        // Assert
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CD04105");
        diagnostic.Start.ShouldBe(0);
        diagnostic.End.ShouldBe(statement.Length);
        session.CurrentTransaction.ShouldNotBeNull().State.ShouldBe(TransactionState.Faulted);
        await session.ExecuteAsync("ROLLBACK", cancellationToken: CancellationToken.None);
        // The same DDL executes after recovery, proving refusal made no catalog change.
        await session.ExecuteAsync(statement, cancellationToken: CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Text transactions: absent transaction is coded and a nested begin faults existing work")]
    [InlineData("COMMIT")]
    [InlineData("ROLLBACK")]
    public async Task ExecuteAsync_InvalidState_ShouldPreserveStateContract(string ending)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("text-state", new());
        var database = await engine.CreateDatabaseAsync("documents", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act / Assert
        var absent = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, ending, false));
        absent.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD04104");
        await ExecuteAsync(session, "BEGIN", false);
        var duplicate = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "BEGIN", false));
        duplicate.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK04004");
        session.CurrentTransaction.ShouldNotBeNull().State.ShouldBe(TransactionState.Faulted);
        if (ending == "COMMIT")
        {
            var faultedCommit = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, ending, false));
            faultedCommit.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD04101");
        }
        else { await ExecuteAsync(session, ending, false); }
        session.CurrentTransaction.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Text transactions: text and API share a transaction and canceled control preserves it")]
    public async Task ExecuteAsync_ApiTransactionAndCanceledRollback_ShouldShareOwnershipAndPreserveState()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("text-api", new());
        var database = await engine.CreateDatabaseAsync("documents", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act / Assert
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await session.ExecuteAsync("ROLLBACK", cancellationToken: cancellation.Token));
        session.CurrentTransaction.ShouldBeSameAs(transaction);
        transaction.State.ShouldBe(TransactionState.Active);
        await session.ExecuteAsync("COMMIT", cancellationToken: CancellationToken.None);
        transaction.State.ShouldBe(TransactionState.Committed);
        await session.ExecuteAsync("BEGIN ISOLATION LEVEL SNAPSHOT", cancellationToken: CancellationToken.None);
        await session.CurrentTransaction.ShouldNotBeNull().RollbackAsync(CancellationToken.None);
        session.CurrentTransaction.ShouldBeNull();
    }

    private static async Task<DatabaseResult> ExecuteAsync(DocumentDatabaseSession session, string source, bool typed)
        => typed ? await session.ExecuteAsync(new DocumentQueryRequest((OqlQueryStatement)new OqlQueryParser().Parse(source)), CancellationToken.None)
            : await session.ExecuteAsync(source, cancellationToken: CancellationToken.None);

    private static async Task<long> CountAsync(DocumentDatabaseSession session)
    {
        var result = (await session.ExecuteAsync("SELECT COUNT(*) FROM items", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { return row.GetValue(0).ShouldBeOfType<long>(); }
        throw new InvalidOperationException("COUNT must return a row.");
    }
}
