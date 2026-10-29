using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlIdentityReviewTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Referential defaults: grant only sequences assigned by the action")]
    [InlineData("SET NULL", "", 0)]
    [InlineData("SET DEFAULT", "DEFAULT 2", 0)]
    [InlineData("SET DEFAULT", "DEFAULT nextval('s')", 1)]
    public async Task ExecuteAsync_ReferentialDefault_ShouldSkipUnassignedIdentity(string action, string value, int expected)
    {
        // Arrange: c.id has a sequence, but the referential action assigns only c.pid.
        await using var engine = SqlDatabaseEngine.Create("referential-grant-review", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE SEQUENCE s START 2");
        await Exec(session, "CREATE TABLE p(id BIGINT PRIMARY KEY)");
        await Exec(session, "INSERT INTO p VALUES(1),(2)");
        await Exec(session, "CREATE TABLE c(id SERIAL,pid BIGINT " + value + " REFERENCES p(id) ON DELETE " + action + ")");
        await Exec(session, "INSERT INTO c(pid) VALUES(1)");
        int grants = 0;
        SqlSequenceExecution.AfterSequenceGrant = _ => grants++;
        try
        {
            // Act
            await Exec(session, "DELETE FROM p WHERE id=1");
            // Assert
            grants.ShouldBe(expected);
            (await Rows(session, "SELECT id FROM c"))[0][0].ShouldBe(1);
        }
        finally { SqlSequenceExecution.AfterSequenceGrant = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Identity DML: request sequence grants only for allocating actions")]
    [InlineData("UPDATE t SET k=2", 0)][InlineData("DELETE FROM t", 0)]
    [InlineData("UPDATE t SET id=DEFAULT", 1)]
    [InlineData("MERGE INTO t USING (SELECT 1 AS k) s ON t.k=s.k WHEN MATCHED THEN UPDATE SET k=2", 0)]
    public async Task ExecuteAsync_NonAllocatingAction_ShouldSkipIdentityGrant(string sql, int expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("identity-grant-review", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE TABLE t(id SERIAL,k INT)");
        await Exec(session, "INSERT INTO t(k) VALUES(1)");
        int grants = 0;
        SqlSequenceExecution.AfterSequenceGrant = _ => grants++;
        try
        {
            // Act
            await Exec(session, sql);
            // Assert
            grants.ShouldBe(expected);
        }
        finally { SqlSequenceExecution.AfterSequenceGrant = null; }
    }

}
