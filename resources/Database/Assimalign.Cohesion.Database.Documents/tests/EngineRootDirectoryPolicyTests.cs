using System;
using System.IO;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Internal;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class EngineRootDirectoryPolicyTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Documents] - Engine root policy: failed inspection names the listed directory and retains its cause")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PrepareExistingUnix_FailedInspection_ShouldNameDirectoryAndRefuseWithCause(bool databaseDirectory, bool denied)
    {
        // Arrange
        string root = Path.GetFullPath("root");
        string listed = databaseDirectory ? Path.Combine(root, "sales") : root;
        Exception cause = denied ? new UnauthorizedAccessException("inspection denied") : new IOException("listing failed");
        int changes = 0, warnings = 0;

        // Act
        var error = Should.Throw<DatabaseException>(() => DatabaseRootDirectory.PrepareExistingUnix(root, (UnixFileMode)493,
            () => databaseDirectory ? DatabaseRootDirectory.InspectUnixDirectory(root, listed, () => throw cause) : throw cause,
            _ => changes++, (_, _, _) => warnings++));

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK06007");
        error.InnerException.ShouldBeSameAs(cause);
        changes.ShouldBe(0); warnings.ShouldBe(0);
        error.Message.ShouldContain("could not list directory '" + listed + "'", Case.Sensitive);
        error.Message.ShouldContain("could not confirm that engine root directory '" + root + "' holds only engine data", Case.Sensitive);
        error.Message.ShouldContain("will not change the root's mode", Case.Sensitive);
        error.Message.ShouldContain("Point RootPath at a new subdirectory, which the engine creates with mode 0700", Case.Sensitive);
        error.Message.ShouldContain("make this account the directory's owner and run chmod 700 \"" + root + "\"", Case.Sensitive);
        error.Message.ShouldNotContain("This account cannot change the mode", Case.Sensitive);
        error.Diagnostics[0].Message.ShouldBe(error.Message);
    }
}
