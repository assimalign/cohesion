using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlTimingCollection.Name)]
public sealed class SqlReferentialActionTimingTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - SET DEFAULT: 100,000 children with a second self reference validate in linear work")]
    public async Task Delete_HundredThousandChildren_ShouldCompleteWithinTimeout()
    {
        // Arrange
        // The large closure is deliberately admitted for this work/validation test.
        // Plain DELETE now charges its retained action rows just like joined DML.
        await using var engine = SqlDatabaseEngine.Create("linear-set-default", new() { StatementMemoryBudget = 512L * 1024 * 1024 });
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE c (id INT PRIMARY KEY, pid INT DEFAULT 0 REFERENCES p(id) ON DELETE SET DEFAULT, anchor INT REFERENCES c(id), FOREIGN KEY(pid) REFERENCES p(id))", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX ix_pid ON c(pid)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO p VALUES (0), (1)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO c VALUES (0, 0, NULL)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE digit (id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO digit VALUES (0), (1), (2), (3), (4), (5), (6), (7), (8), (9)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO c SELECT a.id + 10 * b.id + 100 * c.id + 1000 * d.id + 10000 * e.id + 1, 1, 0 FROM digit a CROSS JOIN digit b CROSS JOIN digit c CROSS JOIN digit d CROSS JOIN digit e", cancellationToken: TestTimeout.Token(120));

        // Act: the one-minute token is a hang guard; recorded comparisons prove linear work.
        var result = await session.ExecuteAsync("DELETE FROM p WHERE id = 1", cancellationToken: TestTimeout.Token(60));

        // Assert
        result.AffectedCount.ShouldBe(1);
        session.LastStatementMetrics.ShouldNotBeNull().ConstraintKeyComparisons.ShouldBeLessThan(100L * 100_001);
        (await Rows(session, "SELECT COUNT(*) FROM c WHERE pid = 0")).Single()[0].ShouldBe(100_001L);
        (await Rows(session, "SELECT COUNT(*) FROM c WHERE pid = 1")).Single()[0].ShouldBe(0L);
        (await Rows(session, "SELECT COUNT(*) FROM c WHERE anchor = 0")).Single()[0].ShouldBe(100_000L);
    }

    private static async Task<List<object?[]>> Rows(SqlDatabaseSession session, string sql)
    {
        await using var result = (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        return await SqlT1ScalarTests.ReadAsync(result);
    }
}
