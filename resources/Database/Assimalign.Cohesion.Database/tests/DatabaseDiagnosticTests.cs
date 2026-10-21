using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Tests;

public sealed class DatabaseDiagnosticTests
{
    [Fact(DisplayName = "Cohesion Test [Database] - Diagnostics: transaction aborts snapshot and sort structured diagnostics")]
    public void Constructor_AbortDiagnostics_ShouldCopyAndOrderEntries()
    {
        // Arrange
        var diagnostics = new List<Diagnostic>
        {
            new() { Code = "unlocated" },
            new() { Code = "late", Start = 20 },
            new() { Code = "first", Start = 1 },
        };
        var cause = new InvalidOperationException();

        // Act
        var failure = new DatabaseTransactionAbortedException("Retry the transaction.", diagnostics, cause);
        diagnostics.Clear();

        // Assert
        failure.Diagnostics.Select(item => item.Code).ShouldBe(["first", "late", "unlocated"]);
        failure.Message.ShouldBe("Retry the transaction.");
        failure.InnerException.ShouldBeSameAs(cause);
        Should.Throw<NotSupportedException>(() => ((IList<Diagnostic>)failure.Diagnostics).Clear());
        Should.Throw<ArgumentNullException>(() => new DatabaseTransactionAbortedException("message", diagnostics: null!));
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Diagnostics: ordinary exceptions have an empty carrier")]
    public void Constructor_NoDiagnostics_ShouldExposeEmptyList()
    {
        // Arrange / Act / Assert
        new DatabaseException().Diagnostics.ShouldBeEmpty();
        new DatabaseException("message").Diagnostics.ShouldBeEmpty();
        new DatabaseException("message", new InvalidOperationException()).Diagnostics.ShouldBeEmpty();
        new DatabaseParseException("message").Diagnostics.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Diagnostics: snapshots preserve every diagnostic and sort source offsets stably")]
    public void Constructor_Diagnostics_ShouldCopyAndOrderAllEntries()
    {
        // Arrange
        var diagnostics = new List<Diagnostic>
        {
            new() { Code = "late", Start = 20, End = 21 },
            new() { Code = "first", Start = 1, End = 2 },
            new() { Code = "same", Start = 1, End = 3 },
            new() { Code = "unlocated" },
        };
        var cause = new InvalidOperationException();

        // Act
        var failure = new DatabaseParseException("first error", diagnostics, cause);
        diagnostics.Clear();

        // Assert
        failure.Diagnostics.Select(item => item.Code).ShouldBe(new[] { "first", "same", "late", "unlocated" });
        failure.Message.ShouldBe("first error");
        failure.InnerException.ShouldBeSameAs(cause);
        Should.Throw<NotSupportedException>(() => ((IList<Diagnostic>)failure.Diagnostics).Clear());
    }
}
