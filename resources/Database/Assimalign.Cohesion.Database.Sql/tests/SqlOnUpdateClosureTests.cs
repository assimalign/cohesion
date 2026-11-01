using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlOnUpdateClosureTests
{
    public static IEnumerable<object[]> Actions()
    {
        foreach (string action in new[] { "CASCADE", "SET NULL", "SET DEFAULT" })
        { for (int path = 0; path < 4; path++) { yield return [action, path]; } }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ON UPDATE: composite and NULL keys execute on all paths (#1723)")]
    [MemberData(nameof(Actions))]
    public async Task ExecuteAsync_CompositeKeys_ShouldApplyMatchSimple(string action, int path)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("update-composite", new());
        var db = await engine.CreateDatabaseAsync("db");
        await using var session = await db.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE p(tag INT PRIMARY KEY,a INT,b TEXT,UNIQUE(a,b))");
        await session.ExecuteAsync($"CREATE TABLE c(tag INT PRIMARY KEY,a INT DEFAULT 9,b TEXT DEFAULT 'default',FOREIGN KEY(a,b) REFERENCES p(a,b) ON UPDATE {action})");
        await session.ExecuteAsync("INSERT INTO p VALUES(1,1,'old'),(2,9,'default'),(3,NULL,'old')");
        await session.ExecuteAsync("INSERT INTO c VALUES(1,1,'old'),(2,NULL,'old')");
        string sql = path switch
        {
            0 => "UPDATE p SET a=2,b='new' WHERE tag=1",
            1 => "UPDATE p SET a=2,b='new' FROM (SELECT 1 AS tag) s WHERE p.tag=s.tag",
            2 => "INSERT INTO p VALUES(1,1,'old') ON CONFLICT(tag) DO UPDATE SET a=2,b='new'",
            _ => "MERGE INTO p USING (SELECT 1 AS tag) s ON p.tag=s.tag WHEN MATCHED THEN UPDATE SET a=2,b='new'",
        };

        // Act
        (await session.ExecuteAsync(sql)).AffectedCount.ShouldBe(1);
        await session.ExecuteAsync("UPDATE p SET b='ignored' WHERE tag=3");

        // Assert
        object?[] expected = action switch { "CASCADE" => [2, "new"], "SET DEFAULT" => [9, "default"], _ => [null, null] };
        (await Rows(session, "SELECT a,b FROM c ORDER BY tag")).ShouldBe([expected, [null, "old"]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ON UPDATE: sequence and identity defaults allocate once per child on all paths (#1723)")]
    [InlineData(false, 0)][InlineData(false, 1)][InlineData(false, 2)][InlineData(false, 3)]
    [InlineData(true, 0)][InlineData(true, 1)][InlineData(true, 2)][InlineData(true, 3)]
    public async Task ExecuteAsync_SequenceDefault_ShouldAllocatePerRow(bool identity, int path)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("update-default-sequence", new());
        var db = await engine.CreateDatabaseAsync("db");
        await using var session = await db.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE p(tag INT PRIMARY KEY,k BIGINT UNIQUE)");
        await session.ExecuteAsync("INSERT INTO p VALUES(1,1),(10,10),(11,11)");
        if (!identity) { await session.ExecuteAsync("CREATE SEQUENCE s START WITH 10"); }
        await session.ExecuteAsync("CREATE TABLE c(tag INT PRIMARY KEY,k BIGINT " +
            (identity ? "GENERATED ALWAYS AS IDENTITY (START WITH 10)" : "DEFAULT NEXT VALUE FOR s") + " REFERENCES p(k) ON UPDATE SET DEFAULT)");
        await session.ExecuteAsync("INSERT INTO c(tag,k) " + (identity ? "OVERRIDING SYSTEM VALUE " : "") + "VALUES(1,1),(2,1)");

        // Act
        await session.ExecuteAsync(UpdateKey(path, "2"));

        // Assert
        (await Rows(session, "SELECT k FROM c ORDER BY tag")).ShouldBe([[10L], [11L]]);
        (await Rows(session, "SELECT lastval()")).ShouldBe([[11L]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ON UPDATE: RESTRICT rejects substitution immediately and NO ACTION sees the complete statement (#1723)")]
    [InlineData("RESTRICT",0)][InlineData("RESTRICT",1)][InlineData("RESTRICT",2)][InlineData("RESTRICT",3)]
    [InlineData("NO ACTION",0)][InlineData("NO ACTION",1)][InlineData("NO ACTION",2)][InlineData("NO ACTION",3)]
    public async Task ExecuteAsync_ParentSubstitution_ShouldDistinguishActions(string action, int path)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("update-substitution", new());
        var db = await engine.CreateDatabaseAsync("db");
        await using var session = await db.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE p(tag INT PRIMARY KEY,k INT UNIQUE)");
        await session.ExecuteAsync($"CREATE TABLE c(k INT REFERENCES p(k) ON UPDATE {action})");
        await session.ExecuteAsync("INSERT INTO p VALUES(1,1),(2,2)");
        await session.ExecuteAsync("INSERT INTO c VALUES(1)");
        string sql = path switch
        {
            0 => "UPDATE p SET k=3-k",
            1 => "UPDATE p SET k=3-p.k FROM (SELECT 1 AS v) s",
            2 => "INSERT INTO p VALUES(1,1),(2,2) ON CONFLICT(tag) DO UPDATE SET k=3-p.k",
            _ => "MERGE INTO p USING (SELECT 1 AS tag UNION ALL SELECT 2) s ON p.tag=s.tag WHEN MATCHED THEN UPDATE SET k=3-p.k",
        };

        // Act
        if (action == "RESTRICT")
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql));
            error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03039");
        }
        else { (await session.ExecuteAsync(sql)).AffectedCount.ShouldBe(2); }

        // Assert
        (await Rows(session, "SELECT tag,k FROM p ORDER BY tag")).ShouldBe(action == "RESTRICT" ? [[1,1],[2,2]] : [[1,2],[2,1]]);
        (await Rows(session, "SELECT k FROM c")).ShouldBe([[1]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ON UPDATE: self-reference substitution distinguishes immediate RESTRICT on every path (#1723)")]
    [InlineData("RESTRICT",0)][InlineData("RESTRICT",1)][InlineData("RESTRICT",2)][InlineData("RESTRICT",3)]
    [InlineData("NO ACTION",0)][InlineData("NO ACTION",1)][InlineData("NO ACTION",2)][InlineData("NO ACTION",3)]
    public async Task ExecuteAsync_SelfSubstitution_ShouldDistinguishActions(string action, int path)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("update-self-substitution", new());
        var db = await engine.CreateDatabaseAsync("db");
        await using var session = await db.CreateSessionAsync();
        await session.ExecuteAsync($"CREATE TABLE p(tag INT PRIMARY KEY,k INT UNIQUE,link INT REFERENCES p(k) ON UPDATE {action})");
        await session.ExecuteAsync("INSERT INTO p VALUES(1,1,1),(2,2,NULL)");
        string sql = path switch
        {
            0 => "UPDATE p SET k=3-k",
            1 => "UPDATE p SET k=3-p.k FROM (SELECT 1 AS v) s",
            2 => "INSERT INTO p VALUES(1,1,1),(2,2,NULL) ON CONFLICT(tag) DO UPDATE SET k=3-p.k",
            _ => "MERGE INTO p USING (SELECT 1 AS tag UNION ALL SELECT 2) s ON p.tag=s.tag WHEN MATCHED THEN UPDATE SET k=3-p.k",
        };
        // Act
        if (action == "RESTRICT")
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql));
            error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03039");
        }
        else { (await session.ExecuteAsync(sql)).AffectedCount.ShouldBe(2); }
        // Assert
        (await Rows(session,"SELECT tag,k,link FROM p ORDER BY tag")).ShouldBe(action == "RESTRICT" ? [[1,1,1],[2,2,null]] : [[1,2,1],[2,1,null]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ON UPDATE: pending self-referencing inserts participate in the same closure (#1723)")]
    [InlineData("CASCADE",2)][InlineData("SET NULL",2)][InlineData("SET DEFAULT",2)]
    [InlineData("CASCADE",3)][InlineData("SET NULL",3)][InlineData("SET DEFAULT",3)]
    public async Task ExecuteAsync_PendingInsert_ShouldApplyAction(string action,int path)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("update-pending-insert",new());
        var db = await engine.CreateDatabaseAsync("db");
        await using var session = await db.CreateSessionAsync();
        await session.ExecuteAsync($"CREATE TABLE p(tag INT PRIMARY KEY,k INT UNIQUE,link INT DEFAULT 0 REFERENCES p(k) ON UPDATE {action})");
        await session.ExecuteAsync("INSERT INTO p VALUES(0,0,NULL),(1,1,NULL)");
        string sql = path == 2
            ? "INSERT INTO p VALUES(1,1,NULL),(2,3,1) ON CONFLICT(tag) DO UPDATE SET k=2"
            : "MERGE INTO p USING (SELECT 1 AS tag UNION ALL SELECT 2) s ON p.tag=s.tag WHEN MATCHED THEN UPDATE SET k=2 WHEN NOT MATCHED THEN INSERT VALUES(s.tag,3,1)";
        // Act
        (await session.ExecuteAsync(sql)).AffectedCount.ShouldBe(2);
        // Assert
        object? value = action switch { "CASCADE" => 2,"SET DEFAULT" => 0,_ => null };
        (await Rows(session,"SELECT tag,k,link FROM p ORDER BY tag")).ShouldBe([[0,0,null],[1,2,null],[2,3,value]]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - ON UPDATE: cyclic cascades converge without recursion (#1723)")]
    public async Task ExecuteAsync_CyclicCascade_ShouldConverge()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("update-cycle", new());
        var db = await engine.CreateDatabaseAsync("db");
        await using var session = await db.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE a(k INT PRIMARY KEY)");
        await session.ExecuteAsync("CREATE TABLE b(k INT PRIMARY KEY REFERENCES a(k) ON UPDATE CASCADE)");
        await session.ExecuteAsync("INSERT INTO a VALUES(1)");
        await session.ExecuteAsync("INSERT INTO b VALUES(1)");
        await session.ExecuteAsync("ALTER TABLE a ADD CONSTRAINT back FOREIGN KEY(k) REFERENCES b(k) ON UPDATE CASCADE");

        // Act
        await session.ExecuteAsync("UPDATE a SET k=2");

        // Assert
        (await Rows(session,"SELECT k FROM a")).ShouldBe([[2]]);
        (await Rows(session,"SELECT k FROM b")).ShouldBe([[2]]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - ON UPDATE: cascades complete a long chain through one statement bracket (#1723)")]
    public async Task ExecuteAsync_CascadeChain_ShouldReachEveryTable()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("update-long-chain",new());
        var db = await engine.CreateDatabaseAsync("db");
        await using var session = await db.CreateSessionAsync();
        for (int index=0;index<64;index++)
        {
            string reference = index == 0 ? "" : $" REFERENCES t{index-1}(k) ON UPDATE CASCADE";
            await session.ExecuteAsync($"CREATE TABLE t{index}(k INT PRIMARY KEY{reference})");
            await session.ExecuteAsync($"INSERT INTO t{index} VALUES(1)");
        }
        // Act
        (await session.ExecuteAsync("UPDATE t0 SET k=2")).AffectedCount.ShouldBe(1);
        // Assert
        for (int index=0;index<64;index++) { (await Rows(session,$"SELECT k FROM t{index}")).ShouldBe([[2]]); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ON UPDATE: incompatible diamond revisits fail with CS05085 and roll back (#1723)")]
    [InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)]
    public async Task ExecuteAsync_ConflictingCascade_ShouldFailAtomically(int path)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("update-conflict", new());
        var db = await engine.CreateDatabaseAsync("db");
        await using var session = await db.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE p(tag INT PRIMARY KEY,k INT UNIQUE)");
        await session.ExecuteAsync("CREATE TABLE c(a INT UNIQUE REFERENCES p(k) ON UPDATE CASCADE,b INT DEFAULT 3 UNIQUE REFERENCES p(k) ON UPDATE SET DEFAULT)");
        await session.ExecuteAsync("CREATE TABLE d(k INT REFERENCES c(a) ON UPDATE CASCADE,FOREIGN KEY(k) REFERENCES c(b) ON UPDATE CASCADE)");
        await session.ExecuteAsync("INSERT INTO p VALUES(1,1),(3,3)");
        await session.ExecuteAsync("INSERT INTO c VALUES(1,1)");
        await session.ExecuteAsync("INSERT INTO d VALUES(1)");

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(UpdateKey(path,"2")));

        // Assert
        error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS05085");
        (await Rows(session,"SELECT a,b FROM c")).ShouldBe([[1,1]]);
        (await Rows(session,"SELECT k FROM d")).ShouldBe([[1]]);
        (await Rows(session,"SELECT k FROM p ORDER BY k")).ShouldBe([[1],[3]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ON UPDATE: final CHECK, NOT NULL and unique failures undo the entire closure (#1723)")]
    [InlineData("k INT NOT NULL REFERENCES p(k) ON UPDATE SET NULL", "INSERT INTO c VALUES(1)")]
    [InlineData("k INT CHECK(k<2) REFERENCES p(k) ON UPDATE CASCADE", "INSERT INTO c VALUES(1)")]
    [InlineData("k INT DEFAULT 3 UNIQUE REFERENCES p(k) ON UPDATE SET DEFAULT", "INSERT INTO c VALUES(1),(3)")]
    public async Task ExecuteAsync_InvalidFinalState_ShouldRollbackClosure(string declaration, string seed)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("update-final-failure", new());
        var db = await engine.CreateDatabaseAsync("db");
        await using var session = await db.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE p(tag INT PRIMARY KEY,k INT UNIQUE)");
        await session.ExecuteAsync("INSERT INTO p VALUES(1,1),(3,3)");
        await session.ExecuteAsync($"CREATE TABLE c({declaration})");
        await session.ExecuteAsync(seed);

        // Act
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("UPDATE p SET k=2 WHERE tag=1"));

        // Assert
        (await Rows(session,"SELECT k FROM p ORDER BY k")).ShouldBe([[1],[3]]);
        (await Rows(session,"SELECT k FROM c ORDER BY k"))[0].ShouldBe([1]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ON UPDATE: MERGE updates removed by delete cascades still execute key actions (#1723)")]
    [InlineData("CASCADE","CASCADE")][InlineData("CASCADE","SET NULL")]
    [InlineData("SET NULL","NO ACTION")][InlineData("SET DEFAULT","NO ACTION")]
    [InlineData("CASCADE","NO ACTION")]
    public async Task ExecuteAsync_UpdatedThenCascadedRow_ShouldCompleteBothClosures(string updateAction, string deleteAction)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("update-delete-merge", new());
        var db = await engine.CreateDatabaseAsync("db");
        await using var session = await db.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t(id INT PRIMARY KEY,pid INT REFERENCES t(id) ON DELETE CASCADE ON UPDATE CASCADE)");
        await session.ExecuteAsync($"CREATE TABLE g(k INT DEFAULT 3 REFERENCES t(id) ON UPDATE {updateAction} ON DELETE {deleteAction})");
        await session.ExecuteAsync("INSERT INTO t VALUES(1,NULL),(2,1),(3,NULL)");
        await session.ExecuteAsync("INSERT INTO g VALUES(2)");
        const string sql = "MERGE INTO t USING (SELECT 1 AS id UNION ALL SELECT 2) s ON t.id=s.id WHEN MATCHED AND t.id=1 THEN DELETE WHEN MATCHED THEN UPDATE SET id=20";
        // Act
        if (updateAction == "CASCADE" && deleteAction == "NO ACTION")
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql));
            error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03039");
            (await Rows(session,"SELECT id FROM t ORDER BY id")).ShouldBe([[1],[2],[3]]);
            (await Rows(session,"SELECT k FROM g")).ShouldBe([[2]]);
            return;
        }
        await session.ExecuteAsync(sql);
        // Assert
        (await Rows(session,"SELECT id FROM t")).ShouldBe([[3]]);
        (await Rows(session,"SELECT k FROM g")).ShouldBe(deleteAction == "CASCADE" ? [] : updateAction == "SET DEFAULT" ? [[3]] : [[null]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ON UPDATE: RETURNING failures roll back the closure and preserve prior transaction writes (#1723)")]
    [InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)]
    public async Task ExecuteAsync_ReturningFailure_ShouldPreserveEarlierWork(int path)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("update-returning-failure",new());
        var db = await engine.CreateDatabaseAsync("db");
        await using var session = await db.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE p(tag INT PRIMARY KEY,k INT UNIQUE)");
        await session.ExecuteAsync("CREATE TABLE c(k INT REFERENCES p(k) ON UPDATE CASCADE)");
        await session.ExecuteAsync("CREATE TABLE log(k INT)");
        await session.ExecuteAsync("INSERT INTO p VALUES(1,1)");
        await session.ExecuteAsync("INSERT INTO c VALUES(1)");
        await session.ExecuteAsync("BEGIN");
        await session.ExecuteAsync("INSERT INTO log VALUES(7)");
        // Act
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(UpdateKey(path,"2")+" RETURNING 1/(k-k)"));
        // Assert
        (await Rows(session,"SELECT k FROM p")).ShouldBe([[1]]);
        (await Rows(session,"SELECT k FROM c")).ShouldBe([[1]]);
        (await Rows(session,"SELECT k FROM log")).ShouldBe([[7]]);
        await session.ExecuteAsync("ROLLBACK");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - ON UPDATE: independent update and delete replacements conflict instead of choosing a parent (#1723)")]
    public async Task ExecuteAsync_IndependentUpdateDeleteActions_ShouldRefuseConflict()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("update-delete-conflict",new());
        var db = await engine.CreateDatabaseAsync("db");
        await using var session = await db.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE p(tag INT PRIMARY KEY,k INT UNIQUE)");
        await session.ExecuteAsync("CREATE TABLE c(k INT REFERENCES p(k) ON UPDATE CASCADE ON DELETE SET NULL)");
        await session.ExecuteAsync("INSERT INTO p VALUES(1,1),(2,2)");
        await session.ExecuteAsync("INSERT INTO c VALUES(1)");
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "MERGE INTO p USING (SELECT 1 AS tag UNION ALL SELECT 2) s ON p.tag=s.tag WHEN MATCHED AND p.tag=2 THEN DELETE WHEN MATCHED THEN UPDATE SET k=2"));
        // Assert
        error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS05085");
        (await Rows(session,"SELECT k FROM p ORDER BY tag")).ShouldBe([[1],[2]]);
        (await Rows(session,"SELECT k FROM c")).ShouldBe([[1]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ON UPDATE: fixed snapshots refuse children inserted after BEGIN on every path (#1723)")]
    [MemberData(nameof(Actions))]
    public async Task ExecuteAsync_NewerChild_ShouldFailSnapshotCrosscheck(string action,int path)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("update-snapshot-crosscheck",new());
        var db = await engine.CreateDatabaseAsync("db");
        await using var session = await db.CreateSessionAsync();
        await using var writer = await db.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE p(tag INT PRIMARY KEY,k INT UNIQUE)");
        await session.ExecuteAsync($"CREATE TABLE c(k INT DEFAULT 0 REFERENCES p(k) ON UPDATE {action})");
        await session.ExecuteAsync("INSERT INTO p VALUES(0,0),(1,1)");
        await session.ExecuteAsync("BEGIN");
        await writer.ExecuteAsync("INSERT INTO c VALUES(1)");
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(UpdateKey(path,"2")));
        // Assert
        error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS04064");
        error.Message.ShouldContain($"ON UPDATE {action}",Case.Sensitive);
        (await Rows(writer,"SELECT k FROM p ORDER BY tag")).ShouldBe([[0],[1]]);
        (await Rows(writer,"SELECT k FROM c")).ShouldBe([[1]]);
        await session.ExecuteAsync("ROLLBACK");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - ON UPDATE: MERGE retains direct uniqueness precedence over a cascade conflict (#1723)")]
    public async Task ExecuteAsync_DirectUniqueFailure_ShouldPrecedeCascadeConflict()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("update-merge-precedence",new());
        var db = await engine.CreateDatabaseAsync("db");
        await using var session = await db.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE p(tag INT PRIMARY KEY,k INT UNIQUE,u INT UNIQUE)");
        await session.ExecuteAsync("CREATE TABLE c(a INT UNIQUE REFERENCES p(k) ON UPDATE CASCADE,b INT DEFAULT 3 UNIQUE REFERENCES p(k) ON UPDATE SET DEFAULT)");
        await session.ExecuteAsync("CREATE TABLE d(k INT REFERENCES c(a) ON UPDATE CASCADE,FOREIGN KEY(k) REFERENCES c(b) ON UPDATE CASCADE)");
        await session.ExecuteAsync("INSERT INTO p VALUES(1,1,1),(3,3,3)");
        await session.ExecuteAsync("INSERT INTO c VALUES(1,1)");
        await session.ExecuteAsync("INSERT INTO d VALUES(1)");
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "MERGE INTO p USING (SELECT 1 AS tag) s ON p.tag=s.tag WHEN MATCHED THEN UPDATE SET k=2,u=3"));
        // Assert
        error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03037");
        (await Rows(session,"SELECT k,u FROM p ORDER BY tag")).ShouldBe([[1,1],[3,3]]);
        (await Rows(session,"SELECT a,b FROM c")).ShouldBe([[1,1]]);
        (await Rows(session,"SELECT k FROM d")).ShouldBe([[1]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ON UPDATE: re-keyed pending inserts propagate actions to pending grandchildren (#1723)")]
    [InlineData("CASCADE",2)][InlineData("SET NULL",2)][InlineData("SET DEFAULT",2)]
    [InlineData("CASCADE",3)][InlineData("SET NULL",3)][InlineData("SET DEFAULT",3)]
    public async Task ExecuteAsync_PendingInsertChain_ShouldPropagateActions(string action,int path)
    {
        // Arrange: a references k; b references a, so updating k changes a pending
        // insert's referenced key before that insert has any physical row version.
        await using var engine = SqlDatabaseEngine.Create("update-pending-chain",new());
        var db = await engine.CreateDatabaseAsync("db");
        await using var session = await db.CreateSessionAsync();
        await session.ExecuteAsync($"CREATE TABLE p(tag INT PRIMARY KEY,k INT UNIQUE,a INT UNIQUE REFERENCES p(k) ON UPDATE CASCADE,b INT DEFAULT 3 REFERENCES p(a) ON UPDATE {action})");
        await session.ExecuteAsync("INSERT INTO p VALUES(1,1,NULL,NULL)");
        string sql = path == 2
            ? "INSERT INTO p VALUES(1,1,NULL,NULL),(2,3,1,NULL),(3,4,3,1) ON CONFLICT(tag) DO UPDATE SET k=2"
            : "MERGE INTO p USING (SELECT 1 AS tag UNION ALL SELECT 2 UNION ALL SELECT 3) s ON p.tag=s.tag WHEN MATCHED THEN UPDATE SET k=2 WHEN NOT MATCHED THEN INSERT VALUES(s.tag,s.tag+1,CASE WHEN s.tag=2 THEN 1 ELSE 3 END,CASE WHEN s.tag=2 THEN NULL ELSE 1 END)";
        // Act
        (await session.ExecuteAsync(sql)).AffectedCount.ShouldBe(3);
        // Assert
        object? value = action switch { "CASCADE" => 2,"SET DEFAULT" => 3,_ => null };
        (await Rows(session,"SELECT tag,k,a,b FROM p ORDER BY tag")).ShouldBe([[1,2,null,null],[2,3,2,null],[3,4,3,value]]);
    }

    internal static string UpdateKey(int path, string value) => path switch
    {
        0 => $"UPDATE p SET k={value} WHERE tag=1",
        1 => $"UPDATE p SET k={value} FROM (SELECT 1 AS tag) s WHERE p.tag=s.tag",
        2 => $"INSERT INTO p(tag,k) VALUES(1,1) ON CONFLICT(tag) DO UPDATE SET k={value}",
        _ => $"MERGE INTO p USING (SELECT 1 AS tag) s ON p.tag=s.tag WHEN MATCHED THEN UPDATE SET k={value}",
    };

    internal static async Task<object?[][]> Rows(SqlDatabaseSession session, string sql)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows.Add(Enumerable.Range(0,row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows.ToArray();
    }
}
