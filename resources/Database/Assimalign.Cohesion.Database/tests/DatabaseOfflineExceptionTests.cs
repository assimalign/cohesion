using System;
using System.IO;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Tests;

/// <summary>
/// The area root's offline refusal (#1243): a <see cref="DatabaseException"/> whose model code
/// leads its message. The engines build it from the storage's offline error; their storage
/// operations tests cover that path end to end.
/// </summary>
public class DatabaseOfflineExceptionTests
{
    [Fact(DisplayName = "Cohesion Test [Database] - Offline: the refusal is a database exception carrying its model code and cause")]
    public void Constructor_CodeMessageAndCause_ShouldBeKept()
    {
        // Arrange
        var cause = new IOException("fsync failed");

        // Act
        var error = new DatabaseOfflineException("COHDBX002", "COHDBX002: Database 'x' is offline.", cause);

        // Assert
        error.ShouldBeAssignableTo<DatabaseException>();
        error.Code.ShouldBe("COHDBX002");
        error.Message.ShouldStartWith("COHDBX002", Case.Sensitive);
        error.InnerException.ShouldBeSameAs(cause);
    }

    [Theory(DisplayName = "Cohesion Test [Database] - Offline: a refusal without a code is rejected")]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_BlankCode_ShouldThrow(string code)
    {
        // Act & Assert
        Should.Throw<ArgumentException>(() => new DatabaseOfflineException(code, "offline", null));
    }
}
