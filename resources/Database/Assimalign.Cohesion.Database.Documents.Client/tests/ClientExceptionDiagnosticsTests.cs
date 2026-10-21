using System;
using System.Collections.Generic;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

public sealed class ClientExceptionDiagnosticsTests
{
    [Fact(DisplayName = "Cohesion Test [Documents.Client] - Client exception retains ancestry, codes, cause and a read-only diagnostic snapshot")]
    public void Constructor_DiagnosticsAndCause_ShouldPreserveSnapshot()
    {
        // Arrange
        var diagnostics = new List<Diagnostic>
        {
            new() { Code = "late", Message = "late text", Start = 9, End = 11, Line = 2 },
            new() { Code = "unlocated", Message = "warning text", Severity = DiagnosticSeverity.Warning },
            new() { Code = "first", Message = "first text", Start = 2, End = 4, Severity = DiagnosticSeverity.Warning },
            new() { Code = "same", Message = "same offset", Start = 2, End = 6 },
        };
        Exception inner = new InvalidOperationException("inner");
        // Act
        var failure = new DocumentClientException(ProtocolErrorCode.ExecutionFailure, "outer", inner, diagnostics);
        diagnostics.Clear();
        // Assert
        typeof(DocumentClientException).BaseType.ShouldBe(typeof(Exception));
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        failure.Message.ShouldBe("outer");
        failure.InnerException.ShouldBeSameAs(inner);
        failure.Diagnostics.Select(entry => entry.Code).ShouldBe(new[] { "first", "same", "late", "unlocated" });
        failure.Diagnostics[0].Severity.ShouldBe(DiagnosticSeverity.Warning);
        failure.Diagnostics[0].Start.ShouldBe(2);
        failure.Diagnostics[0].End.ShouldBe(4);
        failure.Diagnostics[0].Message.ShouldBe("first text");
        Should.Throw<NotSupportedException>(() => ((IList<Diagnostic>)failure.Diagnostics).Clear());

    }

    [Fact(DisplayName = "Cohesion Test [Documents.Client] - Client exception without diagnostics returns an empty collection")]
    public void Constructor_NoDiagnostics_ShouldReturnEmptyCollection()
    {
        // Arrange / Act
        var failure = new DocumentClientException(ProtocolErrorCode.ExecutionFailure, "empty");
        // Assert
        failure.Diagnostics.ShouldNotBeNull();
        failure.Diagnostics.ShouldBeEmpty();
    }
}