using System.Linq;
using System.Threading;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection("SQL referential action locks")]
public sealed class SqlReferentialActionLockTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - SET actions: ReadCommitted delete sees parents committed by the child writer it waited for")]
    [InlineData("SET DEFAULT")]
    [InlineData("SET NULL")]
    public async Task Delete_WaitingForChildWriter_ShouldValidateCurrentParents(string action)
    {
        // Arrange: the child writer holds a shared lock on parent 1 and owns the new parent 2.
        await using var engine = SqlDatabaseEngine.Create("set-action-current", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var deleting = await database.CreateSessionAsync(CancellationToken.None);
        await using var writer = await database.CreateSessionAsync(CancellationToken.None);
        await deleting.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)", cancellationToken: CancellationToken.None);
        await deleting.ExecuteAsync($"CREATE TABLE c (id INT PRIMARY KEY, pid INT DEFAULT 2 REFERENCES p(id) ON DELETE {action}, other INT REFERENCES p(id))", cancellationToken: CancellationToken.None);
        await deleting.ExecuteAsync("INSERT INTO p VALUES (1)", cancellationToken: CancellationToken.None);
        await using var writing = await writer.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        await writer.ExecuteAsync("INSERT INTO p VALUES (2)", cancellationToken: CancellationToken.None);
        await writer.ExecuteAsync("INSERT INTO c VALUES (10, 1, 2)", cancellationToken: CancellationToken.None);
        await using var transaction = await deleting.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);

        // Act: ExecuteAsync reaches the conflicting parent lock before returning its task.
        var deletion = deleting.ExecuteAsync("DELETE FROM p WHERE id = 1", cancellationToken: TestTimeout.Token()).AsTask();
        deletion.IsCompleted.ShouldBeFalse();
        await writing.CommitAsync(CancellationToken.None);
        (await deletion).AffectedCount.ShouldBe(1);
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        await using var result = (await deleting.ExecuteAsync("SELECT id, pid, other FROM c", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        (await SqlT1ScalarTests.ReadAsync(result)).Single().ShouldBe(new object?[] { 10, action == "SET DEFAULT" ? 2 : null, 2 });
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - SET NULL: concurrent child insertion waits for parent deletion and cannot orphan")]
    public async Task Delete_ConcurrentChildInsert_ShouldSerializeOnParentRow()
    {
        // Arrange: the deleting transaction holds the real parent row lock.
        await using var engine = SqlDatabaseEngine.Create("set-null-lock", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var parent = await database.CreateSessionAsync(CancellationToken.None);
        await using var child = await database.CreateSessionAsync(CancellationToken.None);
        await parent.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)", cancellationToken: CancellationToken.None);
        await parent.ExecuteAsync("CREATE TABLE c (id INT PRIMARY KEY, pid INT REFERENCES p(id) ON DELETE SET NULL)", cancellationToken: CancellationToken.None);
        await parent.ExecuteAsync("INSERT INTO p VALUES (1)", cancellationToken: CancellationToken.None);
        await parent.ExecuteAsync("INSERT INTO c VALUES (1, 1)", cancellationToken: CancellationToken.None);
        await parent.ExecuteAsync("BEGIN", cancellationToken: CancellationToken.None);
        await parent.ExecuteAsync("DELETE FROM p", cancellationToken: CancellationToken.None);

        // Act: execution reaches the conflicting shared parent lock synchronously.
        var insertion = child.ExecuteAsync("INSERT INTO c VALUES (2, 1)", cancellationToken: TestTimeout.Token()).AsTask();
        insertion.IsCompleted.ShouldBeFalse();
        await parent.ExecuteAsync("COMMIT", cancellationToken: CancellationToken.None);

        // Assert: the decided deletion defeats the insert's stale parent reference.
        await Should.ThrowAsync<DatabaseException>(async () => await insertion);
        var rows = await child.ExecuteAsync("SELECT id, pid FROM c", cancellationToken: CancellationToken.None);
        await using var result = rows.ShouldBeAssignableTo<Assimalign.Cohesion.Database.Execution.DatabaseResultSet>();
        (await SqlT1ScalarTests.ReadAsync(result)).ShouldHaveSingleItem().ShouldBe(new object?[] { 1, null });
    }
}
