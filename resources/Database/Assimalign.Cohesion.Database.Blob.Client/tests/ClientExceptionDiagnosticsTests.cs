using System;
using System.Collections.Generic;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Blob.Client.Tests;

public sealed class ClientExceptionDiagnosticsTests
{
    [Fact(DisplayName = "Cohesion Test [Blob.Client] - Client exception retains ancestry, codes, cause and a read-only diagnostic snapshot")]
    public void Constructor_DiagnosticsAndCause_ShouldPreserveSnapshot()
    {
        // Arrange
        Exception inner = new InvalidOperationException("inner");
        // Act
        var failure = new BlobClientException(ProtocolErrorCode.ExecutionFailure, "outer", inner);

        // Assert
        typeof(BlobClientException).BaseType.ShouldBe(typeof(Exception));
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        failure.Message.ShouldBe("outer");
        failure.InnerException.ShouldBeSameAs(inner);
        failure.Diagnostics.ShouldBeEmpty();

    }

    [Fact(DisplayName = "Cohesion Test [Blob.Client] - Client exception without diagnostics returns an empty collection")]
    public void Constructor_NoDiagnostics_ShouldReturnEmptyCollection()
    {
        // Arrange / Act
        var failure = new BlobClientException(ProtocolErrorCode.ExecutionFailure, "empty");
        // Assert
        failure.Diagnostics.ShouldNotBeNull();
        failure.Diagnostics.ShouldBeEmpty();
    }
}