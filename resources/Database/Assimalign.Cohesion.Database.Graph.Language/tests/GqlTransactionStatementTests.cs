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
    [InlineData("/* open */ begin;", GqlTransactionCommand.Start)]
    [InlineData("/* open */ start transaction;", GqlTransactionCommand.Start)]
    [InlineData("COMMIT;", GqlTransactionCommand.Commit)]
    [InlineData("ROLLBACK", GqlTransactionCommand.Rollback)]
    public void Parse_Control_ShouldPreserveCommandAndStatementSpan(string text, GqlTransactionCommand command)
    {
        var statement = (GqlQueryStatement)new GqlQueryParser().Parse(text);
        statement.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
        statement.GqlExpression.TransactionCommand.ShouldBe(command);
        statement.GqlExpression.Stages.ShouldBeEmpty();
        statement.GqlExpression.Location!.Start.ShouldBeGreaterThanOrEqualTo(0);
        statement.GqlExpression.Location.End.ShouldBeLessThanOrEqualTo(text.Length);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Invalid transaction controls reject composition and unsupported options (#1540)")]
    [InlineData("START")]
    [InlineData("START TRANSACTION RETURN 1")]
    [InlineData("COMMIT; RETURN 1")]
    [InlineData("ROLLBACK TO savepoint")]
    [InlineData("EXPLAIN COMMIT")]
    [InlineData("BEGIN TRANSACTION")]
    public void Parse_InvalidControl_ShouldRefuseComposition(string text)
    {
        new GqlQueryParser().Parse(text).Diagnostics.ShouldContain(item => item.Severity == DiagnosticSeverity.Error);
    }
}
