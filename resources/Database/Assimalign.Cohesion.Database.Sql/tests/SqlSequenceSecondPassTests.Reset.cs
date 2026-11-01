using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSequenceSecondPassTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - DISCARD ALL: host timeout reset values survive user overrides")]
    public async Task Execute_DiscardAll_ShouldRestoreInitialTimeouts()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("second-pass", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        session.StatementTimeout = TimeSpan.FromSeconds(40);
        session.LockTimeout = TimeSpan.FromSeconds(35);
        await Rows(session, "SELECT 1");
        session.StatementTimeout = TimeSpan.Zero;
        session.LockTimeout = null;

        // Act
        await Exec(session, "DISCARD ALL");

        // Assert
        session.StatementTimeout.ShouldBe(TimeSpan.FromSeconds(40));
        session.LockTimeout.ShouldBe(TimeSpan.FromSeconds(35));
        await Exec(session, "DISCARD ALL");
        session.StatementTimeout.ShouldBe(TimeSpan.FromSeconds(40));
        session.LockTimeout.ShouldBe(TimeSpan.FromSeconds(35));
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Retained state: timeouts and sequence session entries clear at their reset values")]
    public async Task Execute_SessionSettings_ShouldReportRetainedStatePrecisely()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("second-pass", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        session.StatementTimeout = TimeSpan.FromSeconds(35);
        session.LockTimeout = TimeSpan.FromSeconds(30);
        session.HasRetainedState.ShouldBeFalse();
        await Exec(session, "CREATE SEQUENCE s");

        // Act / Assert
        session.HasRetainedState.ShouldBeFalse();
        session.LockTimeout = TimeSpan.Zero;
        session.HasRetainedState.ShouldBeTrue();
        session.LockTimeout = TimeSpan.FromSeconds(30);
        session.HasRetainedState.ShouldBeFalse();
        session.StatementTimeout = null;
        session.HasRetainedState.ShouldBeTrue();
        await Exec(session, "DISCARD ALL");
        session.HasRetainedState.ShouldBeFalse();
        await Rows(session, "SELECT setval('s', 10, false)");
        session.HasRetainedState.ShouldBeFalse();
        await Rows(session, "SELECT setval('s', 20, true)");
        session.HasRetainedState.ShouldBeTrue();
        await Exec(session, "DISCARD ALL");
        session.HasRetainedState.ShouldBeFalse();
        await Rows(session, "SELECT nextval('s')");
        await Exec(session, "BEGIN");
        await Exec(session, "ROLLBACK");
        session.HasRetainedState.ShouldBeTrue();
        await Exec(session, "DISCARD ALL");
        session.HasRetainedState.ShouldBeFalse();
    }

}
