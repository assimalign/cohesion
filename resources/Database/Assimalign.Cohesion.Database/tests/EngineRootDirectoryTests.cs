using System;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Blob;
using Assimalign.Cohesion.Database.Documents;
using Assimalign.Cohesion.Database.Graph;
using Assimalign.Cohesion.Database.KeyValuePair;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Tests;

[Collection(nameof(DatabaseEventSourceCollection))]
public sealed class EngineRootDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cohesion-root-mode-" + Guid.NewGuid().ToString("N"));
    private const UnixFileMode ownerMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    [UnixTheory(DisplayName = "Cohesion Test [Database] - Engine root: every file engine creates a Unix root as 0700")]
    [InlineData("Sql")]
    [InlineData("KeyValuePair")]
    [InlineData("Documents")]
    [InlineData("Graph")]
    [InlineData("Blob")]
    [UnsupportedOSPlatform("windows")]
    public void Create_NewUnixRoot_ShouldUseOwnerOnlyMode(string model)
    {
        // Arrange: the root does not exist.
        Directory.Exists(_root).ShouldBeFalse();
        using var events = Record(model);

        // Act
        using var engine = Create(model, _root);

        // Assert
        File.GetUnixFileMode(_root).ShouldBe(ownerMode);
        events.Events.ShouldNotContain(e => e.EventName == "EngineRootPermissionsTightened" || e.EventId == 0);
    }

    [UnixTheory(DisplayName = "Cohesion Test [Database] - Engine root: empty 0755 and 0770 roots tighten once with an octal warning")]
    [InlineData("Sql", 493)]
    [InlineData("KeyValuePair", 493)]
    [InlineData("Documents", 493)]
    [InlineData("Graph", 493)]
    [InlineData("Blob", 493)]
    [InlineData("Sql", 504)]
    [InlineData("KeyValuePair", 504)]
    [InlineData("Documents", 504)]
    [InlineData("Graph", 504)]
    [InlineData("Blob", 504)]
    [UnsupportedOSPlatform("windows")]
    public void Create_WideEmptyUnixRoot_ShouldTightenAndWarnOnce(string model, int mode)
    {
        // Arrange
        Directory.CreateDirectory(_root);
        File.SetUnixFileMode(_root, (UnixFileMode)mode);
        using var events = Record(model);

        // Act
        using (var first = Create(model, _root)) { }
        using var second = Create(model, _root);

        // Assert
        File.GetUnixFileMode(_root).ShouldBe(ownerMode);
        CheckWarning(events, mode);
    }

    [UnixTheory(DisplayName = "Cohesion Test [Database] - Engine root: earlier database directories reopen after tightening")]
    [InlineData("Sql", 493)]
    [InlineData("KeyValuePair", 493)]
    [InlineData("Documents", 493)]
    [InlineData("Graph", 493)]
    [InlineData("Blob", 493)]
    [InlineData("Sql", 504)]
    [InlineData("KeyValuePair", 504)]
    [InlineData("Documents", 504)]
    [InlineData("Graph", 504)]
    [InlineData("Blob", 504)]
    [UnsupportedOSPlatform("windows")]
    public async Task Create_EarlierUnixDatabases_ShouldTightenAndOpenExistingData(string model, int mode)
    {
        // Arrange
        await using (var seed = Create(model, _root))
        { await seed.CreateDatabaseAsync("Sales", CancellationToken.None); }
        string directory = Path.Combine(_root, "sales");
        string data = Path.Combine(directory, DataFileSet(model) + ".dat");
        File.SetUnixFileMode(directory, (UnixFileMode)493);
        File.SetUnixFileMode(data, (UnixFileMode)420);
        File.SetUnixFileMode(_root, (UnixFileMode)mode);
        using var events = Record(model);

        // Act
        await using var engine = Create(model, _root);
        var opened = await engine.OpenDatabaseAsync("sales", CancellationToken.None);

        // Assert: the original stored name and child modes survive.
        opened.Name.ToString().ShouldBe("Sales");
        File.GetUnixFileMode(_root).ShouldBe(ownerMode);
        File.GetUnixFileMode(directory).ShouldBe((UnixFileMode)493);
        File.GetUnixFileMode(data).ShouldBe((UnixFileMode)420);
        CheckWarning(events, mode);
    }

    [UnixTheory(DisplayName = "Cohesion Test [Database] - Engine root: foreign files refuse 0755 and 0770 roots without chmod")]
    [InlineData("Sql", 493)]
    [InlineData("KeyValuePair", 493)]
    [InlineData("Documents", 493)]
    [InlineData("Graph", 493)]
    [InlineData("Blob", 493)]
    [InlineData("Sql", 504)]
    [InlineData("KeyValuePair", 504)]
    [InlineData("Documents", 504)]
    [InlineData("Graph", 504)]
    [InlineData("Blob", 504)]
    [UnsupportedOSPlatform("windows")]
    public void Create_WideUnixRootWithForeignFile_ShouldRefuseAndPreserveMode(string model, int mode)
    {
        // Arrange
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "foreign"), [1, 2, 3]);
        File.SetUnixFileMode(_root, (UnixFileMode)mode);
        using var events = Record(model);

        // Act
        var error = Should.Throw<DatabaseException>(() => Create(model, _root));

        // Assert
        CheckRefusal(error, mode);
        File.GetUnixFileMode(_root).ShouldBe((UnixFileMode)mode);
        File.ReadAllBytes(Path.Combine(_root, "foreign")).ShouldBe([1, 2, 3]);
        Directory.GetFileSystemEntries(_root).Length.ShouldBe(1);
        events.Events.ShouldNotContain(e => e.EventName == "EngineRootPermissionsTightened" || e.EventId == 0);
    }

    [UnixTheory(DisplayName = "Cohesion Test [Database] - Engine root: 0750 keeps its group bits and does not inspect foreign entries")]
    [InlineData("Sql")]
    [InlineData("KeyValuePair")]
    [InlineData("Documents")]
    [InlineData("Graph")]
    [InlineData("Blob")]
    [UnsupportedOSPlatform("windows")]
    public void Create_GroupReadableUnixRoot_ShouldKeepModeAndContents(string model)
    {
        // Arrange
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "foreign"), [5]);
        File.SetUnixFileMode(_root, (UnixFileMode)488);
        using var events = Record(model);

        // Act
        using var engine = Create(model, _root);

        // Assert
        File.GetUnixFileMode(_root).ShouldBe((UnixFileMode)488);
        File.ReadAllBytes(Path.Combine(_root, "foreign")).ShouldBe([5]);
        events.Events.ShouldNotContain(e => e.EventName == "EngineRootPermissionsTightened" || e.EventId == 0);
    }

    [UnixTheory(DisplayName = "Cohesion Test [Database] - Engine root: foreign directories and another model's data are refused")]
    [InlineData("Sql")]
    [InlineData("KeyValuePair")]
    [InlineData("Documents")]
    [InlineData("Graph")]
    [InlineData("Blob")]
    [UnsupportedOSPlatform("windows")]
    public void Create_WideUnixRootWithUnrecognizedDirectory_ShouldRefuse(string model)
    {
        // Arrange
        string directory = Path.Combine(_root, "sales");
        Directory.CreateDirectory(directory);
        string otherSet = model == "Blob" ? "sql" : "blob";
        File.WriteAllBytes(Path.Combine(directory, otherSet + ".dat"), [1]);
        File.SetUnixFileMode(_root, (UnixFileMode)493);

        // Act
        var error = Should.Throw<DatabaseException>(() => Create(model, _root));

        // Assert
        CheckRefusal(error, 493);
        File.GetUnixFileMode(_root).ShouldBe((UnixFileMode)493);
    }

    [WindowsTheory(DisplayName = "Cohesion Test [Database] - Engine root: every file engine creates exactly three inheritable Windows grants")]
    [InlineData("Sql")]
    [InlineData("KeyValuePair")]
    [InlineData("Documents")]
    [InlineData("Graph")]
    [InlineData("Blob")]
    [SupportedOSPlatform("windows")]
    public async Task Create_NewWindowsRoot_ShouldUseThreeProtectedGrants(string model)
    {
        // Arrange
        using var identity = WindowsIdentity.GetCurrent();
        string[] principals = [identity.User!.Value, "S-1-5-18", "S-1-5-32-544"];

        // Act
        await using var engine = Create(model, _root);
        var acl = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(_root));
        await engine.CreateDatabaseAsync("sales", CancellationToken.None);
        var childAcl = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(Path.Combine(_root, "sales")));

        // Assert
        acl.AreAccessRulesProtected.ShouldBeTrue();
        CheckGrants(acl, principals, inherited: false);
        CheckGrants(childAcl, principals, inherited: true);
        string data = Path.Combine(_root, "sales", DataFileSet(model) + ".dat");
        var fileAcl = FileSystemAclExtensions.GetAccessControl(new FileInfo(data));
        var rules = fileAcl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        rules.Length.ShouldBe(3);
        rules.Select(r => r.IdentityReference.Value).Order().ShouldBe(principals.Order());
        rules.ShouldAllBe(r => r.IsInherited && r.AccessControlType == AccessControlType.Allow && r.FileSystemRights == FileSystemRights.FullControl);
    }

    [WindowsTheory(DisplayName = "Cohesion Test [Database] - Engine root: existing Windows ACLs and foreign contents remain unchanged")]
    [InlineData("Sql")]
    [InlineData("KeyValuePair")]
    [InlineData("Documents")]
    [InlineData("Graph")]
    [InlineData("Blob")]
    [SupportedOSPlatform("windows")]
    public void Create_ExistingWindowsRoot_ShouldLeaveAclUnchanged(string model)
    {
        // Arrange
        var root = Directory.CreateDirectory(_root);
        var acl = FileSystemAclExtensions.GetAccessControl(root);
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(root, acl);
        string before = FileSystemAclExtensions.GetAccessControl(root).GetSecurityDescriptorSddlForm(AccessControlSections.All);
        File.WriteAllBytes(Path.Combine(_root, "foreign"), [9]);

        // Act
        using var engine = Create(model, _root);

        // Assert
        FileSystemAclExtensions.GetAccessControl(root).GetSecurityDescriptorSddlForm(AccessControlSections.All).ShouldBe(before);
        File.ReadAllBytes(Path.Combine(_root, "foreign")).ShouldBe([9]);
    }

    [SupportedOSPlatform("windows")]
    private static void CheckGrants(DirectorySecurity acl, string[] principals, bool inherited)
    {
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        rules.Length.ShouldBe(3);
        rules.Select(r => r.IdentityReference.Value).Order().ShouldBe(principals.Order());
        foreach (var rule in rules)
        {
            rule.IsInherited.ShouldBe(inherited);
            rule.FileSystemRights.ShouldBe(FileSystemRights.FullControl);
            rule.AccessControlType.ShouldBe(AccessControlType.Allow);
            rule.InheritanceFlags.ShouldBe(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
            rule.PropagationFlags.ShouldBe(PropagationFlags.None);
        }
    }

    private void CheckRefusal(DatabaseException error, int mode)
    {
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK06007");
        error.Message.ShouldContain(_root, Case.Sensitive);
        error.Message.ShouldContain(Convert.ToString(mode, 8).PadLeft(4, '0'), Case.Sensitive);
        error.Message.ShouldContain("0700 or 0750", Case.Sensitive);
        error.Message.ShouldContain("chmod 700 \"" + _root + "\"", Case.Sensitive);
    }

    private void CheckWarning(EventSourceRecorder events, int oldMode)
    {
        var warning = events.Events.Where(e => e.EventName == "EngineRootPermissionsTightened").ShouldHaveSingleItem();
        warning.Level.ShouldBe(EventLevel.Warning);
        warning.PayloadNames.ShouldBe(["path", "oldMode", "newMode"]);
        warning.Payload!.ShouldBe([_root, Convert.ToString(oldMode, 8).PadLeft(4, '0'), "0700"]);
        events.Events.ShouldNotContain(e => e.EventId == 0);
    }

    private static EventSourceRecorder Record(string model)
    {
        // Initialize the model's existing source before attaching the canonical recorder.
        string primingRoot = Path.Combine(Path.GetTempPath(), "cohesion-root-source-" + Guid.NewGuid().ToString("N"));
        try { using var prime = Create(model, primingRoot); }
        finally { if (Directory.Exists(primingRoot)) { Directory.Delete(primingRoot, recursive: true); } }
        return new EventSourceRecorder(EventSource.GetSources().Single(s => s.Name == "Assimalign.Cohesion.Database." + model), EventLevel.Warning);
    }

    private static string DataFileSet(string model) => model switch
    { "Sql" => "sql", "KeyValuePair" => "keyvalue", "Documents" => "document", "Graph" => "graph", _ => "blob" };

    private static DatabaseEngine Create(string model, string? root) => model switch
    {
        "Sql" => SqlDatabaseEngine.Create("root-test", new() { RootPath = root is null ? (FileSystemPath?)null : (FileSystemPath)root }),
        "KeyValuePair" => KeyValueDatabaseEngine.Create("root-test", new() { RootPath = root is null ? (FileSystemPath?)null : (FileSystemPath)root }),
        "Documents" => DocumentDatabaseEngine.Create("root-test", new() { RootPath = root is null ? (FileSystemPath?)null : (FileSystemPath)root }),
        "Graph" => GraphDatabaseEngine.Create("root-test", new() { RootPath = root is null ? (FileSystemPath?)null : (FileSystemPath)root }),
        _ => BlobDatabaseEngine.Create("root-test", new() { RootPath = root is null ? (FileSystemPath?)null : (FileSystemPath)root })
    };

    public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); } }

    private sealed class UnixTheoryAttribute : TheoryAttribute
    {
        public UnixTheoryAttribute() { if (OperatingSystem.IsWindows()) { Skip = "Requires Unix file modes."; } }
    }

    private sealed class WindowsTheoryAttribute : TheoryAttribute
    {
        public WindowsTheoryAttribute() { if (!OperatingSystem.IsWindows()) { Skip = "Requires Windows ACLs."; } }
    }
}
