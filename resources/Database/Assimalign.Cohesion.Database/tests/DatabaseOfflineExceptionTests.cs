using System;
using System.IO;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Tests;

/// <summary>
/// The area root's offline refusal (#1243): a <see cref="DatabaseException"/> whose model code
/// is carried by Diagnostics. The engines build it from the storage's offline error; their storage
/// operations tests cover that path end to end.
/// </summary>
public class DatabaseOfflineExceptionTests
{
    [Theory(DisplayName = "Cohesion Test [Database] - Unconfirmed commit: every model retains its code and cause (#1350)")]
    [InlineData("CS04004")]
    [InlineData("COHDBK002")]
    [InlineData("CD06102")]
    [InlineData("CG06012")]
    [InlineData("CB06002")]
    public void Create_UnconfirmedCommit_ShouldPopulateDiagnostics(string code)
    {
        // Arrange
        var cause = new TransactionCommitUnconfirmedException("flush", new IOException("device"));

        // Act
        var failure = DatabaseTransactionCommitUnconfirmedException.Create(code, "database", cause);

        // Assert
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(code);
        diagnostic.Message.ShouldBe(failure.Message);
        failure.Message.ShouldStartWith("Database 'database' went offline", Case.Sensitive);
        failure.InnerException.ShouldBeSameAs(cause);
    }

    [Theory(DisplayName = "Cohesion Test [Database] - Offline: every model code is exposed through diagnostics (#1350)")]
    [InlineData("CS04004")]
    [InlineData("COHDBK002")]
    [InlineData("CD06102")]
    [InlineData("CG06012")]
    [InlineData("CB06002")]
    public void Constructor_ModelCode_ShouldPopulateDiagnostics(string code)
    {
        // Arrange / Act
        var cause = new IOException("device failure");
        var failure = new DatabaseOfflineException(code, "The database is offline.", cause);

        // Assert
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(code);
        diagnostic.Message.ShouldBe(failure.Message);
        failure.InnerException.ShouldBeSameAs(cause);
    }

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
        error.Message.ShouldBe("Database 'x' is offline.");
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBX002");
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
