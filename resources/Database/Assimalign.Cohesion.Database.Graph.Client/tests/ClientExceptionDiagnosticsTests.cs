using System;
using System.Collections.Generic;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

public sealed class ClientExceptionDiagnosticsTests
{
    [Fact(DisplayName = "Cohesion Test [Graph.Client] - Client exception retains ancestry, codes, cause and a read-only diagnostic snapshot")]
    public void Constructor_DiagnosticsAndCause_ShouldPreserveSnapshot()
    {
        // Arrange
        var wire = new List<ProtocolDiagnostic>
        {
            new(ProtocolDiagnosticSeverity.Error, "late", "late text", 9, 11, 2),
            new(ProtocolDiagnosticSeverity.Warning, "unlocated", "warning text", null, null, 1),
            new(ProtocolDiagnosticSeverity.Warning, "first", "first text", 2, 4, 1),
            new(ProtocolDiagnosticSeverity.Error, "same", "same offset", 2, 6, 1),
        };
        Exception inner = new DatabaseClientException(ProtocolErrorCode.ExecutionFailure, "inner", wire, null);
        // Act
        var failure = new GraphClientException(ProtocolErrorCode.ExecutionFailure, "outer", inner);
        wire.Clear();
        // Assert
        typeof(GraphClientException).BaseType.ShouldBe(typeof(Exception));
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        failure.Message.ShouldBe("outer");
        failure.InnerException.ShouldBeSameAs(inner);
        failure.Diagnostics.Select(entry => entry.Code).ShouldBe(new[] { "first", "same", "late", "unlocated" });
        failure.Diagnostics[0].Severity.ShouldBe(DiagnosticSeverity.Warning);
        failure.Diagnostics[0].Start.ShouldBe(2);
        failure.Diagnostics[0].End.ShouldBe(4);
        failure.Diagnostics[0].Message.ShouldBe("first text");
        Should.Throw<NotSupportedException>(() => ((IList<Diagnostic>)failure.Diagnostics).Clear());
        // A second model wrapper also copies the actual client carrier.
        inner = failure;
        var nested = new GraphClientException(ProtocolErrorCode.ExecutionFailure, "outer", inner);
        nested.Diagnostics.Select(entry => entry.Code).ShouldBe(failure.Diagnostics.Select(entry => entry.Code));
        nested.Diagnostics.ShouldNotBeSameAs(failure.Diagnostics);
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Client] - Client exception without diagnostics returns an empty collection")]
    public void Constructor_NoDiagnostics_ShouldReturnEmptyCollection()
    {
        // Arrange / Act
        var failure = new GraphClientException(ProtocolErrorCode.ExecutionFailure, "empty");
        // Assert
        failure.Diagnostics.ShouldNotBeNull();
        failure.Diagnostics.ShouldBeEmpty();
    }
}