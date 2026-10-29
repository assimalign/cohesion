using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlOnUpdateRegressionTests
{
    public static IEnumerable<object[]> CollatedChanges()
    {
        foreach (string action in new[] { "CASCADE", "SET NULL", "SET DEFAULT" })
        {
            for (int path = 0; path < 4; path++)
            {
                foreach (bool self in new[] { false, true })
                {
                    yield return [action, path, self, "case_insensitive", "Ada", "ADA"];
                    yield return [action, path, self, "case_accent_insensitive", "Cafe", "Café"];
                }
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ON UPDATE: collation-equal representation changes refuse atomically on every update path (#1723)")]
    [MemberData(nameof(CollatedChanges))]
    public async Task ExecuteAsync_CollationEqualKeyChange_ShouldRefuseBeforeWrites(
        string action, int path, bool self, string collation, string oldKey, string newKey)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("on-update-image", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session, action, self, collation, oldKey);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, UpdateSql(path, oldKey, newKey)));

        // Assert
        error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CK00001");
        error.Message.ShouldContain($"ON UPDATE {action} action execution", Case.Sensitive);
        (await RowsAsync(session, "SELECT id,v FROM p ORDER BY v,id")).ShouldBe([[oldKey, 0], ["Zed", 0]]);
        (await RowsAsync(session, self ? $"SELECT pid FROM p WHERE id='{oldKey}'" : "SELECT pid FROM c")).ShouldBe([[oldKey]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ON UPDATE: identical stored keys still pass on every update path (#1723)")]
    [MemberData(nameof(CollatedChanges))]
    public async Task ExecuteAsync_UnchangedCollatedKey_ShouldAllowWrites(
        string action, int path, bool self, string collation, string oldKey, string newKey)
    {
        // Arrange
        _ = newKey;
        await using var engine = SqlDatabaseEngine.Create("on-update-unchanged", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session, action, self, collation, oldKey);

        // Act
        await ExecuteAsync(session, UpdateSql(path, oldKey, oldKey));

        // Assert
        (await RowsAsync(session, $"SELECT id,v FROM p WHERE id='{oldKey}'")).ShouldBe([[oldKey, 1]]);
        (await RowsAsync(session, self ? $"SELECT pid FROM p WHERE id='{oldKey}'" : "SELECT pid FROM c")).ShouldBe([[oldKey]]);
    }

    public static IEnumerable<object[]> CollatedRestrictions()
    {
        foreach (var change in CollatedChanges().Where(change => (string)change[0] == "CASCADE"))
        {
            foreach (string action in new[] { "NO ACTION", "RESTRICT" })
            {
                yield return [action, change[1], change[2], change[3], change[4], change[5]];
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ON UPDATE: NO ACTION and RESTRICT retain collation-aware parent matching (#1723)")]
    [MemberData(nameof(CollatedRestrictions))]
    public async Task ExecuteAsync_RestrictedCollationEqualKeyChange_ShouldKeepProvidingParent(
        string action, int path, bool self, string collation, string oldKey, string newKey)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("on-update-restrict", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session, action, self, collation, oldKey);

        // Act
        await ExecuteAsync(session, UpdateSql(path, oldKey, newKey));

        // Assert
        (await RowsAsync(session, $"SELECT id,v FROM p WHERE id='{newKey}'")).ShouldBe([[newKey, 1]]);
        (await RowsAsync(session, self ? $"SELECT pid FROM p WHERE id='{newKey}'" : "SELECT pid FROM c")).ShouldBe([[oldKey]]);
    }

    public static IEnumerable<object[]> DeleteChains()
    {
        foreach (string deleteAction in new[] { "SET NULL", "SET DEFAULT" })
        {
            foreach (string updateAction in new[] { "CASCADE", "SET NULL", "SET DEFAULT" })
            {
                for (int path = 0; path < 3; path++) { yield return [deleteAction, updateAction, path]; }
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ON UPDATE: delete action chains refuse before another group's outgoing FK validation (#1723)")]
    [MemberData(nameof(DeleteChains))]
    public async Task ExecuteAsync_DeleteActionChain_ShouldRefuseStoredUpdateActionBeforeForeignKeyViolation(
        string deleteAction, string updateAction, int path)
    {
        // Arrange: a is visited before b, but only b's incoming key needs an update action.
        await using var engine = SqlDatabaseEngine.Create("on-update-chain", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE p(id INT PRIMARY KEY)");
        await ExecuteAsync(session, $"CREATE TABLE a(ppid INT DEFAULT 2 REFERENCES p(id) ON DELETE {deleteAction},bpid INT DEFAULT 2)");
        await ExecuteAsync(session, $"CREATE TABLE b(pid INT DEFAULT 2 UNIQUE REFERENCES p(id) ON DELETE {deleteAction})");
        await ExecuteAsync(session, $"ALTER TABLE a ADD CONSTRAINT fk_b FOREIGN KEY(bpid) REFERENCES b(pid) ON UPDATE {updateAction}");
        await ExecuteAsync(session, "INSERT INTO p VALUES(1),(2)");
        await ExecuteAsync(session, "INSERT INTO b VALUES(1)");
        await ExecuteAsync(session, "INSERT INTO a VALUES(1,1)");
        string sql = path switch
        {
            0 => "DELETE FROM p WHERE id=1",
            1 => "DELETE FROM p USING (SELECT 1 AS id) s WHERE p.id=s.id",
            _ => "MERGE INTO p USING (SELECT 1 AS id) s ON p.id=s.id WHEN MATCHED THEN DELETE",
        };

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, sql));

        // Assert
        error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CK00001");
        error.Message.ShouldContain($"ON UPDATE {updateAction} action execution", Case.Sensitive);
        error.Message.ShouldContain("fk_b", Case.Sensitive);
        (await RowsAsync(session, "SELECT id FROM p ORDER BY id")).ShouldBe([[1], [2]]);
        (await RowsAsync(session, "SELECT ppid,bpid FROM a")).ShouldBe([[1, 1]]);
        (await RowsAsync(session, "SELECT pid FROM b")).ShouldBe([[1]]);
    }

    private static async Task SeedAsync(SqlDatabaseSession session, string action, bool self, string collation, string oldKey)
    {
        string reference = $"TEXT COLLATE {collation} DEFAULT 'Zed' REFERENCES p(id) ON UPDATE {action}";
        await ExecuteAsync(session, $"CREATE TABLE p(id TEXT COLLATE {collation} PRIMARY KEY,v INT" + (self ? $",pid {reference})" : ")"));
        if (!self) { await ExecuteAsync(session, $"CREATE TABLE c(pid {reference})"); }
        await ExecuteAsync(session, self ? $"INSERT INTO p VALUES('{oldKey}',0,'{oldKey}'),('Zed',0,NULL)" : $"INSERT INTO p VALUES('{oldKey}',0),('Zed',0)");
        if (!self) { await ExecuteAsync(session, $"INSERT INTO c VALUES('{oldKey}')"); }
    }

    private static string UpdateSql(int path, string oldKey, string newKey) => path switch
    {
        0 => $"UPDATE p SET id=CASE WHEN id='{oldKey}' THEN '{newKey}' ELSE id END,v=1",
        1 => $"UPDATE p SET id=s.next_id,v=1 FROM (SELECT '{oldKey}' AS old_id,'{newKey}' AS next_id UNION ALL SELECT 'Zed','Zed') s WHERE p.id=s.old_id",
        2 => $"INSERT INTO p(id,v) VALUES('Zed',1),('{oldKey}',1) ON CONFLICT(id) DO UPDATE SET id=CASE WHEN p.id='{oldKey}' THEN '{newKey}' ELSE p.id END,v=excluded.v",
        _ => $"MERGE INTO p USING (SELECT 'New' AS id UNION ALL SELECT 'Zed' UNION ALL SELECT '{oldKey}') s ON p.id=s.id WHEN MATCHED THEN UPDATE SET id=CASE WHEN p.id='{oldKey}' THEN '{newKey}' ELSE p.id END,v=1 WHEN NOT MATCHED THEN INSERT(id,v) VALUES(s.id,1)",
    };

    private static async Task ExecuteAsync(SqlDatabaseSession session, string sql)
        => (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).Status.ShouldBe(DatabaseResultStatus.Success);

    private static async Task<object?[][]> RowsAsync(SqlDatabaseSession session, string sql)
    {
        var result = (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
        }
        return rows.ToArray();
    }
}
