using System;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>Verifies constructor diagnostics, disclosure rules and source-location attachment.</summary>
public sealed class SqlConstraintDiagnosticTests
{
    /// <summary>Construction without statement text preserves metadata and supplies one coded error.</summary>
    /// <param name="kind">The constraint kind supplied by the caller.</param>
    /// <param name="code">The required engine diagnostic code.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Constraint constructors: one coded error preserves safe metadata without disclosing values")]
    [InlineData("UNIQUE", "COHSQLE037")]
    [InlineData("PRIMARY KEY", "COHSQLE037")]
    [InlineData("CHECK", "COHSQLE038")]
    [InlineData("DOMAIN", "COHSQLE038")]
    [InlineData("FOREIGN KEY", "COHSQLE039")]
    [InlineData("NOT NULL", "COHSQLE024")]
    [InlineData("application-defined", "COHSQLE038")]
    public void Constructor_ConstraintKind_ShouldCarryOneUnlocatedError(string kind, string code)
    {
        // Arrange
        var cause = new InvalidOperationException("underlying fault");

        // Act
        var failure = new SqlConstraintViolationException("named_constraint", "dbo.items", kind,
            offendingValue: "private-key-value", innerException: cause);

        // Assert
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(code);
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.Message.ShouldBe(failure.Message);
        diagnostic.Message!.ShouldContain("named_constraint", Case.Sensitive);
        diagnostic.Message!.ShouldContain("dbo.items", Case.Sensitive);
        diagnostic.Message!.ShouldNotContain("private-key-value", Case.Sensitive);
        diagnostic.Start.ShouldBeNull();
        diagnostic.End.ShouldBeNull();
        diagnostic.Location.ShouldBeNull();
        failure.OffendingValue.ShouldBe("private-key-value");
        failure.InnerException.ShouldBeSameAs(cause);
    }

    /// <summary>Attaching a span preserves the exception's metadata and never replaces an existing location.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Constraint locations: attaching a statement span preserves the diagnostic and exception metadata")]
    public void At_StatementSpan_ShouldPreserveMetadataAndExistingLocation()
    {
        // Arrange
        var cause = new InvalidOperationException("underlying fault");
        var failure = new SqlConstraintViolationException("named_constraint", "dbo.items", "CHECK", 7, cause);
        var location = Location.Create(4, 4, 12, 40);

        // Act
        var located = failure.At(location);

        // Assert
        var diagnostic = located.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("COHSQLE038");
        diagnostic.Start.ShouldBe(12);
        diagnostic.End.ShouldBe(40);
        diagnostic.Line.ShouldBe(4);
        diagnostic.Location.ShouldBe(DiagnosticLocation.Absolute);
        located.Message.ShouldBe(failure.Message);
        located.ConstraintName.ShouldBe(failure.ConstraintName);
        located.Table.ShouldBe(failure.Table);
        located.ConstraintKind.ShouldBe(failure.ConstraintKind);
        located.OffendingValue.ShouldBe(7);
        located.InnerException.ShouldBeSameAs(cause);
        failure.At(null).ShouldBeSameAs(failure);
        located.At(Location.Create(1, 1, 0, 1)).ShouldBeSameAs(located);
    }

    /// <summary>A thrown violation without a cause keeps its own stack when a location is attached.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Constraint locations: attaching a span keeps the thrown violation's stack as the inner exception")]
    public void At_ThrownViolationWithoutCause_ShouldKeepOriginalStack()
    {
        // Arrange
        SqlConstraintViolationException thrown;
        try
        {
            throw new SqlConstraintViolationException("named_constraint", "dbo.items", "CHECK");
        }
        catch (SqlConstraintViolationException exception)
        {
            thrown = exception;
        }

        // Act
        var located = thrown.At(Location.Create(1, 1, 0, 10));

        // Assert
        located.ShouldNotBeSameAs(thrown);
        located.InnerException.ShouldBeSameAs(thrown);
        thrown.StackTrace.ShouldNotBeNull();
        thrown.StackTrace!.ShouldContain(nameof(At_ThrownViolationWithoutCause_ShouldKeepOriginalStack), Case.Sensitive);
        located.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHSQLE038");
    }
}
