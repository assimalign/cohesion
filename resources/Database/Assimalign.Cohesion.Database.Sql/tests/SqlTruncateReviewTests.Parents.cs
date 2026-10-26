using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlTruncateReviewTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - FK DDL: a parent missing after locking requires retry before rebind (#1716)")]
    [InlineData("CREATE TABLE added (id INT REFERENCES p(id))")]
    [InlineData("ALTER TABLE c ADD CONSTRAINT fk FOREIGN KEY(id) REFERENCES p(id)")]
    [InlineData("ALTER TABLE c ADD COLUMN pid INT REFERENCES p(id)")]
    public async Task ExecuteAsync_ParentMissingAfterForeignKeyLocks_ShouldRefuseBeforeRebind(string sql)
    {
        // Arrange: this existing hook is after locking; there is no hook between bind and lock lookup.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("truncate-fk-missing-parent", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(session, "CREATE TABLE p (id INT PRIMARY KEY)", timeout.Token);
        await ExecAsync(session, "CREATE TABLE c (id INT)", timeout.Token);
        int locked = 0;
        SqlPlanExecutor.ReferencedTablesLocked = () =>
        {
            database.Catalog.DropTableAsync("dbo", "p", timeout.Token).GetAwaiter().GetResult();
            locked++;
        };
        try
        {
            // Act
            var failure = await Should.ThrowAsync<DatabaseException>(() => ExecAsync(session, sql, timeout.Token));

            // Assert
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS04064");
            locked.ShouldBe(1);
            database.Catalog.Tables.Select(table => table.Name).ShouldBe(new[] { "c" });
            var child = database.Catalog.Tables.Single();
            child.Constraints.ShouldBeEmpty();
            child.Columns.ShouldHaveSingleItem().Name.ShouldBe("id");
        }
        finally { SqlPlanExecutor.ReferencedTablesLocked = null; }
    }
}
