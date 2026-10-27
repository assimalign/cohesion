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
        new DatabaseNotFoundException("message").Diagnostics.ShouldBeEmpty();
        var absent = new DatabaseNotFoundException("message", null);
        absent.Diagnostics.ShouldBeEmpty();
        absent.InnerException.ShouldBeNull();
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

    [Fact(DisplayName = "Cohesion Test [Database] - Diagnostics: precise database absence snapshots the typed carrier and preserves its cause")]
    public void Constructor_NotFoundDiagnostics_ShouldCopyAndPreservePreciseAbsence()
    {
        // Arrange
        var input = new List<Diagnostic> { new() { Code = "CK03002", Severity = DiagnosticSeverity.Error } };
        var cause = new System.IO.IOException("Original absence cause.");
        // Act
        var failure = new DatabaseNotFoundException("Database absent.", input, cause);
        input.Clear();
        // Assert
        failure.GetType().ShouldBe(typeof(DatabaseNotFoundException));
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK03002");
        failure.Message.ShouldBe("Database absent.");
        failure.InnerException.ShouldBeSameAs(cause);
        Should.Throw<NotSupportedException>(() => ((IList<Diagnostic>)failure.Diagnostics).Clear());
        Should.Throw<ArgumentNullException>(() => new DatabaseNotFoundException("message", null!, null));
        new DatabaseNotFoundException("message", Array.Empty<Diagnostic>(), null).Diagnostics.ShouldBeEmpty();
    }
}
