using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlTransactionParserTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Transactions: SQL++ spellings preserve command isolation and span")]
    [InlineData("BEGIN", OqlTransactionCommand.Begin, OqlTransactionIsolation.Snapshot)]
    [InlineData("begin work;", OqlTransactionCommand.Begin, OqlTransactionIsolation.Snapshot)]
    [InlineData("BEGIN TRAN", OqlTransactionCommand.Begin, OqlTransactionIsolation.Snapshot)]
    [InlineData("BEGIN TRANSACTION", OqlTransactionCommand.Begin, OqlTransactionIsolation.Snapshot)]
    [InlineData("START WORK", OqlTransactionCommand.Begin, OqlTransactionIsolation.Snapshot)]
    [InlineData("START TRAN", OqlTransactionCommand.Begin, OqlTransactionIsolation.Snapshot)]
    [InlineData("START TRANSACTION", OqlTransactionCommand.Begin, OqlTransactionIsolation.Snapshot)]
    [InlineData("BEGIN ISOLATION LEVEL SNAPSHOT", OqlTransactionCommand.Begin, OqlTransactionIsolation.Snapshot)]
    [InlineData("START WORK ISOLATION LEVEL SNAPSHOT", OqlTransactionCommand.Begin, OqlTransactionIsolation.Snapshot)]
    [InlineData("BEGIN ISOLATION LEVEL READ COMMITTED", OqlTransactionCommand.Begin, OqlTransactionIsolation.ReadCommitted)]
    [InlineData("BEGIN TRANSACTION ISOLATION LEVEL READ COMMITTED", OqlTransactionCommand.Begin, OqlTransactionIsolation.ReadCommitted)]
    [InlineData("start tran isolation level read committed;", OqlTransactionCommand.Begin, OqlTransactionIsolation.ReadCommitted)]
    [InlineData("COMMIT", OqlTransactionCommand.Commit, OqlTransactionIsolation.Snapshot)]
    [InlineData("COMMIT WORK", OqlTransactionCommand.Commit, OqlTransactionIsolation.Snapshot)]
    [InlineData("COMMIT TRAN", OqlTransactionCommand.Commit, OqlTransactionIsolation.Snapshot)]
    [InlineData("COMMIT TRANSACTION;", OqlTransactionCommand.Commit, OqlTransactionIsolation.Snapshot)]
    [InlineData("ROLLBACK", OqlTransactionCommand.Rollback, OqlTransactionIsolation.Snapshot)]
    [InlineData("ROLLBACK WORK", OqlTransactionCommand.Rollback, OqlTransactionIsolation.Snapshot)]
    [InlineData("ROLLBACK TRAN", OqlTransactionCommand.Rollback, OqlTransactionIsolation.Snapshot)]
    [InlineData("ROLLBACK TRANSACTION;", OqlTransactionCommand.Rollback, OqlTransactionIsolation.Snapshot)]
    public void Parse_TransactionControl_ShouldPreserveCommandIsolationAndSpan(string source, OqlTransactionCommand command, OqlTransactionIsolation isolation)
    {
        // Arrange / Act
        var statement = new OqlQueryParser().Parse(source).ShouldBeOfType<OqlQueryStatement>();

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var expression = statement.OqlExpression.ShouldBeOfType<OqlTransactionExpression>();
        expression.Command.ShouldBe(command);
        expression.Isolation.ShouldBe(isolation);
        expression.Text.ShouldBe(source);
        expression.Location!.Start.ShouldBe(0);
        expression.Location.End.ShouldBe(source.TrimEnd(';').Length);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Transactions: malformed commands never become executable control")]
    [InlineData("START", "CD00002")]
    [InlineData("START ISOLATION LEVEL READ COMMITTED", "CD00002")]
    [InlineData("BEGIN WORK TRANSACTION", "CD00002")]
    [InlineData("BEGIN ISOLATION READ COMMITTED", "CD00002")]
    [InlineData("BEGIN ISOLATION LEVEL READ", "CD00002")]
    [InlineData("BEGIN ISOLATION LEVEL SERIALIZABLE", "CD00002")]
    [InlineData("COMMIT ISOLATION LEVEL READ COMMITTED", "CD00002")]
    [InlineData("ROLLBACK extra", "CD00002")]
    [InlineData("BEGIN; COMMIT", "CD00002")]
    [InlineData("EXPLAIN BEGIN", "CK00001")]
    [InlineData("SAVEPOINT", "CD00002")]
    [InlineData("ROLLBACK TO SAVEPOINT", "CD00002")]
    [InlineData("SET TRANSACTION ISOLATION LEVEL READ COMMITTED", "CK00001")]
    public void Parse_InvalidControl_ShouldReportCodedError(string source, string code)
    {
        // Arrange / Act
        var statement = new OqlQueryParser().Parse(source);

        // Assert
        statement.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Language] - Transactions: transaction words remain contextual names")]
    public void Parse_TransactionWordsAsFields_ShouldKeepIdentifiers()
    {
        // Arrange / Act
        var statement = new OqlQueryParser().Parse("SELECT begin, start, commit, rollback, work, tran, transaction, isolation, level, read, committed, snapshot, savepoint FROM items");

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Transactions: SAVEPOINT remains an implicit source alias")]
    [InlineData("SELECT * FROM items savepoint")]
    [InlineData("DELETE FROM items savepoint WHERE savepoint.n = 1")]
    [InlineData("UPDATE items savepoint SET n = 2 WHERE savepoint.n = 1")]
    public void Parse_SavepointAsSourceAlias_ShouldKeepIdentifier(string source)
    {
        // Arrange / Act
        var statement = new OqlQueryParser().Parse(source).ShouldBeOfType<OqlQueryStatement>();

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        string? alias = statement.OqlExpression switch
        {
            OqlSelectExpression select => select.Alias,
            OqlDeleteExpression delete => delete.Alias,
            OqlUpdateExpression update => update.Alias,
            _ => null,
        };
        alias.ShouldBe("savepoint");
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Transactions: rollback to a savepoint rejects trailing input")]
    [InlineData("ROLLBACK TO SAVEPOINT point extra")]
    [InlineData("ROLLBACK WORK TO SAVEPOINT point extra")]
    [InlineData("ROLLBACK TRAN TO SAVEPOINT point extra")]
    [InlineData("ROLLBACK TRANSACTION TO SAVEPOINT point extra")]
    public void Parse_RollbackToSavepointSuffix_ShouldReportSyntax(string source)
    {
        // Arrange / Act
        var statement = new OqlQueryParser().Parse(source);

        // Assert
        var diagnostic = statement.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CD00002");
        diagnostic.Start.ShouldBe(source.IndexOf("extra", StringComparison.Ordinal));
        diagnostic.End.ShouldBe(source.Length);
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Language] - Transactions: invalid direct AST enums are refused")]
    public void Construct_UndefinedEnums_ShouldRefuseValues()
    {
        // Arrange / Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => new OqlTransactionExpression((OqlTransactionCommand)99));
        Should.Throw<ArgumentOutOfRangeException>(() => new OqlTransactionExpression(OqlTransactionCommand.Begin, (OqlTransactionIsolation)99));
    }
}
