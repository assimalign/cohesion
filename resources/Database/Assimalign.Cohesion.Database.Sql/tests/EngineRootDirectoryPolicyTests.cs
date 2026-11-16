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
    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Engine root: entry inspection recognizes engine data across models")]
    [InlineData("empty", true)]
    [InlineData("database", true)]
    [InlineData("temp", true)]
    [InlineData("nested-temp", true)]
    [InlineData("file", false)]
    [InlineData("empty-directory", true)]
    [InlineData("other-model", true)]
    [InlineData("default-temp", true)]
    [InlineData("finder", true)]
    [InlineData("nested-foreign", false)]
    [InlineData("nested-directory", false)]
    [InlineData("temp-file", false)]
    [InlineData("temp-peer", false)]
    [InlineData("uppercase", false)]
    public void FindForeignEntry_RootEntries_ShouldIdentifyForeignEntries(string entry, bool expected)
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
                case "default-temp": Directory.CreateDirectory(Path.Combine(root, ".cohesion-temp")); break;
                case "finder": File.WriteAllBytes(Path.Combine(root, ".DS_Store"), [1]); break;
                case "nested-foreign":
                    Directory.CreateDirectory(Path.Combine(root, "sales"));
                    File.WriteAllBytes(Path.Combine(root, "sales", "foreign"), [1]); break;
                case "nested-directory": Directory.CreateDirectory(Path.Combine(root, "sales", "foreign")); break;
                case "temp-file": File.WriteAllBytes(temp, [1]); break;
                case "temp-peer": Directory.CreateDirectory(temp + "-other"); break;
                case "file": File.WriteAllBytes(Path.Combine(root, "foreign"), [1]); break;
                case "empty-directory": Directory.CreateDirectory(Path.Combine(root, "sales")); break;
                case "temp": Directory.CreateDirectory(temp); break;
                case "nested-temp": temp = Path.Combine(temp, "runs"); Directory.CreateDirectory(temp); break;
            }

            // Act
            string? foreign = DatabaseRootDirectory.FindForeignEntry(root, temp);

            // Assert
            (foreign is null).ShouldBe(expected);
            if (!expected) { foreign.ShouldNotBeNull(); Path.IsPathFullyQualified(foreign).ShouldBeTrue(); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Engine root: every model data file admits its storage directory")]
    [InlineData("sql")]
    [InlineData("keyvalue")]
    [InlineData("document")]
    [InlineData("graph")]
    [InlineData("blob")]
    public void FindForeignEntry_ModelDataFile_ShouldAdmitStorageDirectory(string set)
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "cohesion-model-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sales"));
        File.WriteAllBytes(Path.Combine(root, "sales", set + ".dat"), [1]);
        File.WriteAllBytes(Path.Combine(root, "sales", "extra"), [1]);
        try
        {
            // Act
            string? foreign = DatabaseRootDirectory.FindForeignEntry(root, null);

            // Assert
            foreign.ShouldBeNull();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Engine root: interrupted file sets are engine data")]
    [InlineData("sql.log", true)]
    [InlineData("sql.bak", true)]
    [InlineData("keyvalue.log", true)]
    [InlineData("keyvalue.bak", true)]
    [InlineData("document.log", true)]
    [InlineData("document.bak", true)]
    [InlineData("graph.log", true)]
    [InlineData("graph.bak", true)]
    [InlineData("blob.log", true)]
    [InlineData("blob.bak", true)]
    [InlineData("catalog.dat", true)]
    [InlineData("catalog.log", true)]
    [InlineData("catalog.bak", true)]
    [InlineData("sql.create", true)]
    [InlineData("catalog.foreign", false)]
    [InlineData("foreign.log", false)]
    [InlineData("SQL.log", false)]
    public void FindForeignEntry_InterruptedFileSet_ShouldRecognizeExactFileNames(string file, bool expected)
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "cohesion-leftover-root-" + Guid.NewGuid().ToString("N"));
        string database = Path.Combine(root, "sales");
        Directory.CreateDirectory(database);
        File.WriteAllBytes(Path.Combine(database, file), [1]);
        try
        {
            // Act
            string? foreign = DatabaseRootDirectory.FindForeignEntry(root, null);

            // Assert
            foreign.ShouldBe(expected ? null : database);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Engine root: temp path matching agrees with option validation on every platform")]
    public void FindForeignEntry_CaseVariantTempDirectory_ShouldMatchPathValidation()
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "cohesion-temp-case-" + Guid.NewGuid().ToString("N"));
        string actual = Path.Combine(root, ".spill");
        string configured = Path.Combine(root, ".SPILL", "runs");
        Directory.CreateDirectory(actual);
        try
        {
            bool accepted = Path.GetRelativePath(actual, configured) == "runs";

            // Act
            string? foreign = DatabaseRootDirectory.FindForeignEntry(root, configured);

            // Assert
            foreign.ShouldBe(accepted ? null : actual);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Engine root: a successful permission change emits one warning with path and octal modes")]
    public void PrepareExistingUnix_SuccessfulChange_ShouldEmitExactWarning()
    {
        // Arrange
        using var events = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Warning);

        // Act
        DatabaseRootDirectory.PrepareExistingUnix("root", (UnixFileMode)493, () => null, _ => { },
            SqlDatabaseEventSource.Log.RootPermissionsTightened);

        // Assert
        var warning = events.Events.Where(e => e.EventId == 31).ShouldHaveSingleItem();
        warning.EventName.ShouldBe("EngineRootPermissionsTightened");
        warning.Level.ShouldBe(EventLevel.Warning);
        warning.PayloadNames.ShouldBe(["path", "oldMode", "newMode"]);
        warning.Payload!.ShouldBe(["root", "0755", "0700"]);
        events.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Engine root policy: no group write or other access skips inspection, chmod and warning")]
    [InlineData(448)]
    [InlineData(488)]
    [InlineData(480)]
    [InlineData(456)]
    [InlineData(0)]
    [InlineData(1472)] // 02700
    [InlineData(1512)] // 02750
    [InlineData(960)] // 01700
    [InlineData(2496)] // 04700
    public void PrepareExistingUnix_AllowedMode_ShouldLeaveRootAlone(int mode)
    {
        // Arrange
        int inspections = 0, changes = 0, warnings = 0;

        // Act
        DatabaseRootDirectory.PrepareExistingUnix("root", (UnixFileMode)mode,
            () => { inspections++; return "foreign"; }, _ => changes++, (_, _, _) => warnings++);

        // Assert
        inspections.ShouldBe(0); changes.ShouldBe(0); warnings.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Engine root policy: wider owned roots chmod before one warning")]
    [InlineData(493)]
    [InlineData(504)]
    [InlineData(1528)] // 02770
    [InlineData(1533)] // 02775
    [InlineData(449)]
    [InlineData(450)]
    [InlineData(452)]
    public void PrepareExistingUnix_WideOwnedMode_ShouldSet0700BeforeWarning(int mode)
    {
        // Arrange
        int inspections = 0, changes = 0, warnings = 0;

        // Act
        DatabaseRootDirectory.PrepareExistingUnix("root", (UnixFileMode)mode, () => { inspections++; return null; },
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
    [InlineData(1528)] // 02770
    [InlineData(1533)] // 02775
    public void PrepareExistingUnix_WideForeignRoot_ShouldRefuseWithoutChangingMode(int mode)
    {
        // Arrange
        int changes = 0, warnings = 0;

        // Act
        var error = Should.Throw<DatabaseException>(() => DatabaseRootDirectory.PrepareExistingUnix("root", (UnixFileMode)mode,
            () => "foreign", _ => changes++, (_, _, _) => warnings++));

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK06007");
        error.InnerException.ShouldBeNull(); changes.ShouldBe(0); warnings.ShouldBe(0);
        error.Message.ShouldContain("foreign entry 'foreign'", Case.Sensitive);
        error.Message.ShouldContain("will not change the mode of a directory holding it", Case.Sensitive);
        error.Message.ShouldContain("If the directory belongs to this engine, run chmod 700 \"root\"", Case.Sensitive);
        error.Message.ShouldContain("point RootPath at a new subdirectory, which the engine creates with mode 0700", Case.Sensitive);
        error.Diagnostics[0].Message.ShouldBe(error.Message);
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
            () => inspectionFails ? throw cause : null, _ => throw cause, (_, _, _) => warnings++));

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK06007");
        error.InnerException.ShouldBeSameAs(cause); warnings.ShouldBe(0);
        error.Message.ShouldContain("This account cannot change the mode", Case.Sensitive);
        error.Message.ShouldContain("Point RootPath at a new subdirectory, which the engine creates with mode 0700", Case.Sensitive);
        error.Message.ShouldContain("make this account the directory's owner and run chmod 700", Case.Sensitive);
        error.Diagnostics[0].Message.ShouldBe(error.Message);
        error.Message.ShouldContain("root", Case.Sensitive);
        error.Message.ShouldContain("0755", Case.Sensitive);
        error.Message.ShouldContain("0700 or 0750", Case.Sensitive);
        error.Message.ShouldContain("chmod 700 \"root\"", Case.Sensitive);
    }
}
