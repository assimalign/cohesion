using System.Linq;
using Assimalign.Cohesion.Database.Types;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public sealed class GqlTransactionStatementTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Transaction controls retain the standalone command and source span (#1540)")]
    [InlineData("START TRANSACTION", GqlTransactionCommand.Start)]
    [InlineData("BEGIN", GqlTransactionCommand.Start)]
    [InlineData("BEGIN TRANSACTION", GqlTransactionCommand.Start)]
    [InlineData("begin transaction;", GqlTransactionCommand.Start)]
    [InlineData("/* open */ BeGiN /* gap */ TrAnSaCtIoN; -- done", GqlTransactionCommand.Start)]
    [InlineData("/* open */ begin;", GqlTransactionCommand.Start)]
    [InlineData("/* open */ start transaction;", GqlTransactionCommand.Start)]
    [InlineData("COMMIT;", GqlTransactionCommand.Commit)]
    [InlineData("ROLLBACK", GqlTransactionCommand.Rollback)]
    public void Parse_Control_ShouldPreserveCommandAndStatementSpan(string text, GqlTransactionCommand command)
    {
        // Arrange
        var parser = new GqlQueryParser();

        // Act
        var statement = (GqlQueryStatement)parser.Parse(text);

        // Assert
        statement.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
        statement.GqlExpression.TransactionCommand.ShouldBe(command);
        statement.GqlExpression.Stages.ShouldBeEmpty();
        statement.GqlExpression.Location!.Start.ShouldBeGreaterThanOrEqualTo(0);
        int semicolon = text.IndexOf(';');
        statement.GqlExpression.Location.End.ShouldBe(semicolon < 0 ? text.Length : semicolon);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Invalid transaction controls reject composition and unsupported options (#1540)")]
    [InlineData("START")]
    [InlineData("START TRANSACTION RETURN 1")]
    [InlineData("COMMIT; RETURN 1")]
    [InlineData("ROLLBACK TO SAVEPOINT marker extra")]
    [InlineData("EXPLAIN COMMIT")]
    [InlineData("BEGIN TRANSACTION RETURN 1")]
    [InlineData("BEGIN TRANSACTION; COMMIT")]
    [InlineData("EXPLAIN BEGIN TRANSACTION")]
    [InlineData("BEGIN TRANSACTION TRANSACTION")]
    [InlineData("BEGIN 'TRANSACTION'")]
    [InlineData("BEGIN `TRANSACTION`")]
    public void Parse_InvalidControl_ShouldRefuseComposition(string text)
    {
        // Arrange
        var parser = new GqlQueryParser();

        // Act
        var statement = parser.Parse(text);

        // Assert
        statement.Diagnostics.ShouldContain(item => item.Severity == DiagnosticSeverity.Error);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - BEGIN and START retain CG00002 for every transaction option (#1725)")]
    [InlineData("BEGIN")]
    [InlineData("BEGIN TRANSACTION")]
    [InlineData("START TRANSACTION")]
    public void Parse_TransactionOptions_ShouldKeepCodedRefusal(string begin)
    {
        // Arrange
        var parser = new GqlQueryParser();

        // Act / Assert
        foreach (string option in new[] { "WORK", "TRAN", "READ ONLY", "READ WRITE", "ISOLATION LEVEL SNAPSHOT",
            "ISOLATION LEVEL READ COMMITTED", "ISOLATION LEVEL SERIALIZABLE", "DEFERRABLE", "NOT DEFERRABLE" })
        {
            var statement = parser.Parse(begin + " " + option);
            var error = statement.Diagnostics.ShouldHaveSingleItem();
            error.Code.ShouldBe("CG00002");
            error.Severity.ShouldBe(DiagnosticSeverity.Error);
            error.Start.ShouldBe(begin.Length + 1);
        }
    }
}
