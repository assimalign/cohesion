using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlCreateTableAsTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - CTAS: inferred domain columns retain literal and function defaults")]
    [InlineData("7", 7)]
    [InlineData("abs(-7)", 7)]
    public async Task ExecuteAsync_DomainDefault_ShouldApplyToOmittedColumns(string expression, int expected)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("ctas-domain-default", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        if (expression == "7")
        {
            await ExecAsync(session, "CREATE DOMAIN positive AS INT DEFAULT 7 NOT NULL CHECK(VALUE > 0)", timeout.Token);
        }
        else
        {
            // The catalog also accepts resolved expression defaults; SQL CREATE DOMAIN
            // currently admits literal defaults only. Exercise the stored call vector.
            await ExecAsync(session, "CREATE TABLE reference_default(n INT DEFAULT " + expression + ")", timeout.Token);
            var reference = database.Catalog.Tables.Single(t => t.Name == "reference_default").Columns[0];
            reference.DefaultFunctionCalls.ShouldNotBeEmpty();
            await database.Catalog.CreateDomainAsync(new SqlCatalogDomain("positive", "INTEGER", reference.DefaultLiteral, true,
                null, null, [], DatabaseObjectOwner.Adhoc, null, defaultFunctionCalls: reference.DefaultFunctionCalls), timeout.Token);
            database.Definitions.RefreshDefinitions(timeout.Token);
        }
        await ExecAsync(session, "CREATE TABLE src (n positive, other INT)", timeout.Token);
        await ExecAsync(session, "CREATE TABLE copy AS SELECT * FROM src WITH NO DATA", timeout.Token);

        // Act
        await ExecAsync(session, "INSERT INTO copy(other) VALUES(1)", timeout.Token);

        // Assert
        (await RowsAsync(session, "SELECT n FROM copy", timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(expected);
        var original = database.Catalog.Tables.Single(t => t.Name == "src").Columns[0];
        var copied = database.Catalog.Tables.Single(t => t.Name == "copy").Columns[0];
        copied.DefaultLiteral.ShouldBe(original.DefaultLiteral);
        copied.DefaultFunctionCalls.ShouldBe(original.DefaultFunctionCalls);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - CTAS: WITH DATA retains indexed access")]
    public async Task ExecuteAsync_WithDataPredicate_ShouldUseIndex()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("ctas-index", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await SeedAsync(session, timeout.Token);

        // Act
        await ExecAsync(session, "CREATE TABLE copy AS SELECT * FROM src WHERE id=5", timeout.Token);

        // Assert
        session.LastStatementMetrics.ShouldNotBeNull().AccessPath.ShouldStartWith("seek:");
        session.LastStatementMetrics!.RecordsExamined.ShouldBe(1);
        (await RowsAsync(session, "SELECT * FROM copy", timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(5);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - CTAS: wide metadata is refused before reservation or source evaluation")]
    public async Task ExecuteAsync_OverwideMetadata_ShouldRefuseBeforeReservation()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("ctas-wide", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        int reserved = 0;
        SqlPlanExecutor.TableAsStage = (stage, _) => { if (stage == "Reserved") { reserved++; } };
        try
        {
            // Act
            string columns = string.Join(',', Enumerable.Range(0, 128).Select(i => $"1/0 AS \"{new string('x', 128)}{i}\""));
            var failure = await Should.ThrowAsync<DatabaseException>(() => ExecAsync(session, "CREATE TABLE copy AS SELECT " + columns, timeout.Token));

            // Assert
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05042");
            reserved.ShouldBe(0);
            database.Catalog.Tables.ShouldBeEmpty();
            database.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
        }
        finally { SqlPlanExecutor.TableAsStage = null; }
    }

}
