using System;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Internal;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed class EngineRootDirectoryPolicyTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Engine root: ownership recognizes only engine-created root entries")]
    [InlineData("empty", true)]
    [InlineData("database", true)]
    [InlineData("temp", true)]
    [InlineData("nested-temp", true)]
    [InlineData("file", false)]
    [InlineData("empty-directory", false)]
    [InlineData("other-model", false)]
    [InlineData("uppercase", false)]
    public void IsOwned_RootEntries_ShouldRecognizeOnlyEngineDirectories(string entry, bool expected)
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "cohesion-owned-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string temp = Path.Combine(root, ".spill");
            switch (entry)
            {
                case "database":
                case "uppercase":
                case "other-model":
                    string directory = Path.Combine(root, entry == "uppercase" ? "Sales" : "sales");
                    Directory.CreateDirectory(directory);
                    File.WriteAllBytes(Path.Combine(directory, entry == "other-model" ? "blob.dat" : "sql.dat"), [1]);
                    break;
                case "file": File.WriteAllBytes(Path.Combine(root, "foreign"), [1]); break;
                case "empty-directory": Directory.CreateDirectory(Path.Combine(root, "sales")); break;
                case "temp": Directory.CreateDirectory(temp); break;
                case "nested-temp": temp = Path.Combine(temp, "runs"); Directory.CreateDirectory(temp); break;
            }

            // Act
            bool owned = DatabaseRootDirectory.IsOwned(root, "sql", temp);

            // Assert
            owned.ShouldBe(expected);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Engine root: a successful permission change emits one warning with path and octal modes")]
    public void PrepareExistingUnix_SuccessfulChange_ShouldEmitExactWarning()
    {
        // Arrange
        using var events = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Warning);

        // Act
        DatabaseRootDirectory.PrepareExistingUnix("root", (UnixFileMode)493, () => true, _ => { },
            SqlDatabaseEventSource.Log.RootPermissionsTightened);

        // Assert
        var warning = events.Events.Where(e => e.EventId == 31).ShouldHaveSingleItem();
        warning.EventName.ShouldBe("EngineRootPermissionsTightened");
        warning.Level.ShouldBe(EventLevel.Warning);
        warning.PayloadNames.ShouldBe(["path", "oldMode", "newMode"]);
        warning.Payload!.ShouldBe(["root", "0755", "0700"]);
        events.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Engine root policy: modes within 0750 skip inspection, chmod and warning")]
    [InlineData(448)]
    [InlineData(488)]
    [InlineData(480)]
    [InlineData(456)]
    [InlineData(0)]
    public void PrepareExistingUnix_AllowedMode_ShouldLeaveRootAlone(int mode)
    {
        // Arrange
        int inspections = 0, changes = 0, warnings = 0;

        // Act
        DatabaseRootDirectory.PrepareExistingUnix("root", (UnixFileMode)mode,
            () => { inspections++; return false; }, _ => changes++, (_, _, _) => warnings++);

        // Assert
        inspections.ShouldBe(0); changes.ShouldBe(0); warnings.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Engine root policy: wider owned roots chmod before one warning")]
    [InlineData(493)]
    [InlineData(504)]
    [InlineData(449)]
    [InlineData(450)]
    [InlineData(452)]
    public void PrepareExistingUnix_WideOwnedMode_ShouldSet0700BeforeWarning(int mode)
    {
        // Arrange
        int inspections = 0, changes = 0, warnings = 0;

        // Act
        DatabaseRootDirectory.PrepareExistingUnix("root", (UnixFileMode)mode, () => { inspections++; return true; },
            next => { next.ShouldBe((UnixFileMode)448); changes++; },
            (path, oldMode, newMode) =>
            {
                changes.ShouldBe(1); path.ShouldBe("root"); oldMode.ShouldBe((UnixFileMode)mode);
                newMode.ShouldBe((UnixFileMode)448); warnings++;
            });

        // Assert
        inspections.ShouldBe(1); changes.ShouldBe(1); warnings.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Engine root policy: wider foreign roots refuse without chmod or warning")]
    [InlineData(493)]
    [InlineData(504)]
    public void PrepareExistingUnix_WideForeignRoot_ShouldRefuseWithoutChangingMode(int mode)
    {
        // Arrange
        int changes = 0, warnings = 0;

        // Act
        var error = Should.Throw<DatabaseException>(() => DatabaseRootDirectory.PrepareExistingUnix("root", (UnixFileMode)mode,
            () => false, _ => changes++, (_, _, _) => warnings++));

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK06007");
        error.InnerException.ShouldBeNull(); changes.ShouldBe(0); warnings.ShouldBe(0);
        error.Message.ShouldContain(Convert.ToString(mode, 8).PadLeft(4, '0'), Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Engine root policy: failed chmod or directory inspection retains its cause and refuses")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PrepareExistingUnix_FailedSystemCall_ShouldRefuseWithCause(bool inspectionFails, bool denied)
    {
        // Arrange
        Exception cause = denied ? new UnauthorizedAccessException("chmod denied") : new IOException("read-only volume");
        int warnings = 0;

        // Act
        var error = Should.Throw<DatabaseException>(() => DatabaseRootDirectory.PrepareExistingUnix("root", (UnixFileMode)493,
            () => inspectionFails ? throw cause : true, _ => throw cause, (_, _, _) => warnings++));

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK06007");
        error.InnerException.ShouldBeSameAs(cause); warnings.ShouldBe(0);
        error.Message.ShouldContain("root", Case.Sensitive);
        error.Message.ShouldContain("0755", Case.Sensitive);
        error.Message.ShouldContain("0700 or 0750", Case.Sensitive);
        error.Message.ShouldContain("chmod 700 \"root\"", Case.Sensitive);
    }
}
