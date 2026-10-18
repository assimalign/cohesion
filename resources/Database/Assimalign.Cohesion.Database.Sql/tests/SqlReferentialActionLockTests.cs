using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection("SQL referential action locks")]
public sealed class SqlReferentialActionLockTests
{
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
