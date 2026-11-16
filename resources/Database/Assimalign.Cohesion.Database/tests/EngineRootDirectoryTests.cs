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
    [InlineData("Sql", 1528)]
    [InlineData("Sql", 1533)]
    [InlineData("KeyValuePair", 1528)]
    [InlineData("KeyValuePair", 1533)]
    [InlineData("Documents", 1528)]
    [InlineData("Documents", 1533)]
    [InlineData("Graph", 1528)]
    [InlineData("Graph", 1533)]
    [InlineData("Blob", 1528)]
    [InlineData("Blob", 1533)]
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
        CheckWarning(events, model, mode);
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
        CheckWarning(events, model, mode);
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
    [InlineData("Sql", 1528)]
    [InlineData("Sql", 1533)]
    [InlineData("KeyValuePair", 1528)]
    [InlineData("KeyValuePair", 1533)]
    [InlineData("Documents", 1528)]
    [InlineData("Documents", 1533)]
    [InlineData("Graph", 1528)]
    [InlineData("Graph", 1533)]
    [InlineData("Blob", 1528)]
    [InlineData("Blob", 1533)]
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
        CheckRefusal(error, mode, Path.Combine(_root, "foreign"));
        File.GetUnixFileMode(_root).ShouldBe((UnixFileMode)mode);
        File.ReadAllBytes(Path.Combine(_root, "foreign")).ShouldBe([1, 2, 3]);
        Directory.GetFileSystemEntries(_root).Length.ShouldBe(1);
        events.Events.ShouldNotContain(e => e.EventName == "EngineRootPermissionsTightened" || e.EventId == 0);
    }

    [UnixTheory(DisplayName = "Cohesion Test [Database] - Engine root: 0750, 02700, 02750 and 01700 stay unchanged without inspecting foreign entries")]
    [InlineData("Sql", 488)]
    [InlineData("Sql", 1472)]
    [InlineData("Sql", 1512)]
    [InlineData("Sql", 960)]
    [InlineData("KeyValuePair", 488)]
    [InlineData("KeyValuePair", 1472)]
    [InlineData("KeyValuePair", 1512)]
    [InlineData("KeyValuePair", 960)]
    [InlineData("Documents", 488)]
    [InlineData("Documents", 1472)]
    [InlineData("Documents", 1512)]
    [InlineData("Documents", 960)]
    [InlineData("Graph", 488)]
    [InlineData("Graph", 1472)]
    [InlineData("Graph", 1512)]
    [InlineData("Graph", 960)]
    [InlineData("Blob", 488)]
    [InlineData("Blob", 1472)]
    [InlineData("Blob", 1512)]
    [InlineData("Blob", 960)]
    [UnsupportedOSPlatform("windows")]
    public void Create_AdmittedUnixMode_ShouldKeepModeAndContents(string model, int mode)
    {
        // Arrange
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "foreign"), [5]);
        File.SetUnixFileMode(_root, (UnixFileMode)mode);
        using var events = Record(model);

        // Act
        using var engine = Create(model, _root);

        // Assert
        File.GetUnixFileMode(_root).ShouldBe((UnixFileMode)mode);
        File.ReadAllBytes(Path.Combine(_root, "foreign")).ShouldBe([5]);
        events.Events.ShouldNotContain(e => e.EventName == "EngineRootPermissionsTightened" || e.EventId == 0);
    }

    [UnixTheory(DisplayName = "Cohesion Test [Database] - Engine root: a storage directory containing a foreign file is refused")]
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
        File.WriteAllBytes(Path.Combine(directory, "foreign"), [1]);
        File.SetUnixFileMode(_root, (UnixFileMode)493);

        // Act
        var error = Should.Throw<DatabaseException>(() => Create(model, _root));

        // Assert
        CheckRefusal(error, 493, directory);
        File.GetUnixFileMode(_root).ShouldBe((UnixFileMode)493);
    }

    [UnixTheory(DisplayName = "Cohesion Test [Database] - Engine root: creation under a setgid parent emits no tightening warning")]
    [InlineData("Sql")]
    [InlineData("KeyValuePair")]
    [InlineData("Documents")]
    [InlineData("Graph")]
    [InlineData("Blob")]
    [UnsupportedOSPlatform("windows")]
    public void Create_SetgidParent_ShouldCreateRootWithoutWarning(string model)
    {
        // Arrange
        Directory.CreateDirectory(_root);
        File.SetUnixFileMode(_root, (UnixFileMode)1472); // 02700
        string root = Path.Combine(_root, "data");
        using var events = Record(model);

        // Act
        using var engine = Create(model, root);

        // Assert: the platform may propagate setgid; the engine does not check it.
        (File.GetUnixFileMode(root) & (UnixFileMode)511).ShouldBe(ownerMode);
        File.GetUnixFileMode(_root).ShouldBe((UnixFileMode)1472);
        events.Events.ShouldNotContain(e => e.EventName == "EngineRootPermissionsTightened" || e.EventId == 0);
    }

    [UnixTheory(DisplayName = "Cohesion Test [Database] - Engine root: databases from two models reopen after shared-root tightening")]
    [InlineData("Sql", "Blob")]
    [InlineData("KeyValuePair", "Documents")]
    [InlineData("Graph", "Sql")]
    [UnsupportedOSPlatform("windows")]
    public async Task Create_SharedUnixRoot_ShouldTightenAndOpenBothModels(string first, string second)
    {
        // Arrange
        await using (var seed = Create(first, _root)) { await seed.CreateDatabaseAsync("first", CancellationToken.None); }
        await using (var seed = Create(second, _root)) { await seed.CreateDatabaseAsync("second", CancellationToken.None); }
        File.SetUnixFileMode(_root, (UnixFileMode)493);
        using var firstEvents = Record(first);
        using var secondEvents = Record(second);

        // Act
        await using var firstEngine = Create(first, _root);
        await using var secondEngine = Create(second, _root);
        var firstDatabase = await firstEngine.OpenDatabaseAsync("first", CancellationToken.None);
        var secondDatabase = await secondEngine.OpenDatabaseAsync("second", CancellationToken.None);

        // Assert
        firstDatabase.Name.ToString().ShouldBe("first");
        secondDatabase.Name.ToString().ShouldBe("second");
        File.GetUnixFileMode(_root).ShouldBe(ownerMode);
        CheckWarning(firstEvents, first, 493);
        secondEvents.Events.ShouldNotContain(e => e.EventName == "EngineRootPermissionsTightened" || e.EventId == 0);
    }

    [UnixTheory(DisplayName = "Cohesion Test [Database] - Engine root: interrupted create and drop directories reach the existing coded action after tightening")]
    [InlineData("Sql", false)]
    [InlineData("KeyValuePair", false)]
    [InlineData("Documents", false)]
    [InlineData("Graph", false)]
    [InlineData("Blob", false)]
    [InlineData("Sql", true)]
    [InlineData("KeyValuePair", true)]
    [InlineData("Documents", true)]
    [InlineData("Graph", true)]
    [InlineData("Blob", true)]
    [UnsupportedOSPlatform("windows")]
    public async Task Create_UnixLeftoverDirectory_ShouldTightenThenReportDropAction(string model, bool interruptedDrop)
    {
        // Arrange
        string directory = Path.Combine(_root, "sales");
        Directory.CreateDirectory(directory);
        if (interruptedDrop)
        {
            foreach (string file in new[] { DataFileSet(model) + ".log", DataFileSet(model) + ".bak" })
            { File.WriteAllBytes(Path.Combine(directory, file), []); }
            if (model is "Sql" or "KeyValuePair")
            {
                foreach (string file in new[] { "catalog.dat", "catalog.log", "catalog.bak" })
                { File.WriteAllBytes(Path.Combine(directory, file), []); }
            }
            if (model == "Sql") { File.WriteAllBytes(Path.Combine(directory, "sql.create"), []); }
        }
        File.SetUnixFileMode(_root, (UnixFileMode)493);
        using var events = Record(model);

        // Act
        await using var engine = Create(model, _root);
        var error = await Should.ThrowAsync<DatabaseException>(() => engine.CreateDatabaseAsync("sales", CancellationToken.None).AsTask());

        // Assert
        File.GetUnixFileMode(_root).ShouldBe(ownerMode);
        CheckWarning(events, model, 493);
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(model switch
        { "Documents" => "CD06103", "Graph" => "CG06018", "Blob" => "CB06004", _ => "CK06004" });
        error.Message.ShouldContain(model is "Sql" or "KeyValuePair" ? "without a database in it" : "DropDatabaseAsync", Case.Sensitive);
        await engine.DropDatabaseAsync("sales", CancellationToken.None);
        Directory.Exists(directory).ShouldBeFalse();
    }

    [UnixTheory(DisplayName = "Cohesion Test [Database] - Engine root: Sql recognizes default and configured temp space beside an existing database")]
    [InlineData(false)]
    [InlineData(true)]
    [UnsupportedOSPlatform("windows")]
    public async Task Create_SqlUnixTempDirectory_ShouldTightenAndOpenDatabase(bool configured)
    {
        // Arrange
        await using (var seed = Create("Sql", _root)) { await seed.CreateDatabaseAsync("sales", CancellationToken.None); }
        Directory.CreateDirectory(Path.Combine(_root, ".cohesion-temp"));
        string temp = Path.Combine(_root, ".spill", "runs");
        if (configured) { Directory.CreateDirectory(temp); }
        File.SetUnixFileMode(_root, (UnixFileMode)493);
        using var events = Record("Sql");

        // Act
        await using var engine = SqlDatabaseEngine.Create("root-test", new()
        { RootPath = _root, TempDirectory = configured ? (FileSystemPath?)temp : null });
        var database = await engine.OpenDatabaseAsync("sales", CancellationToken.None);

        // Assert
        database.Name.ToString().ShouldBe("sales");
        File.GetUnixFileMode(_root).ShouldBe(ownerMode);
        CheckWarning(events, "Sql", 493);
    }

    [UnixTheory(DisplayName = "Cohesion Test [Database] - Engine root: a symbolic link blocks tightening and is named in the refusal")]
    [InlineData("Sql")]
    [InlineData("KeyValuePair")]
    [InlineData("Documents")]
    [InlineData("Graph")]
    [InlineData("Blob")]
    [UnsupportedOSPlatform("windows")]
    public void Create_UnixRootWithSymbolicLink_ShouldNameEntryAndPreserveMode(string model)
    {
        // Arrange
        string target = Path.Combine(_root, "sales");
        Directory.CreateDirectory(target);
        string link = Path.Combine(_root, "shortcut");
        Directory.CreateSymbolicLink(link, target);
        File.SetUnixFileMode(_root, (UnixFileMode)493);
        using var events = Record(model);

        // Act
        var error = Should.Throw<DatabaseException>(() => Create(model, _root));

        // Assert
        CheckRefusal(error, 493, link);
        File.GetUnixFileMode(_root).ShouldBe((UnixFileMode)493);
        events.Events.ShouldNotContain(e => e.EventName == "EngineRootPermissionsTightened" || e.EventId == 0);
    }

    [UnixTheory(DisplayName = "Cohesion Test [Database] - Engine root: relative paths become absolute in warnings and refusals")]
    [InlineData("Sql")]
    [InlineData("KeyValuePair")]
    [UnsupportedOSPlatform("windows")]
    public void Create_RelativeUnixRoot_ShouldReportAbsolutePaths(string model)
    {
        // Arrange
        string relative = "cohesion-relative-root-" + Guid.NewGuid().ToString("N");
        string root = Path.GetFullPath(relative);
        Directory.CreateDirectory(root);
        try
        {
            File.SetUnixFileMode(root, (UnixFileMode)493);
            using var events = Record(model);

            // Act
            using (var engine = Create(model, relative)) { }
            File.WriteAllBytes(Path.Combine(root, "foreign"), [1]);
            File.SetUnixFileMode(root, (UnixFileMode)493);
            var error = Should.Throw<DatabaseException>(() => Create(model, relative));

            // Assert
            CheckWarning(events, model, 493, root);
            CheckRefusal(error, 493, Path.Combine(root, "foreign"), root);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [WindowsTheory(DisplayName = "Cohesion Test [Database] - Engine root: missing parents inherit normally and only the root receives protected grants")]
    [InlineData("Sql")]
    [InlineData("KeyValuePair")]
    [InlineData("Documents")]
    [InlineData("Graph")]
    [InlineData("Blob")]
    [SupportedOSPlatform("windows")]
    public void Create_MissingWindowsParents_ShouldKeepInheritedGrantsOnParents(string model)
    {
        // Arrange
        using var identity = WindowsIdentity.GetCurrent();
        var ancestor = Directory.CreateDirectory(_root);
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        acl.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl,
            inheritance, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.ReadAndExecute,
            inheritance, PropagationFlags.None, AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(ancestor, acl);
        string parent = Path.Combine(_root, "outer", "inner");
        string root = Path.Combine(parent, "data");
        Directory.Exists(Path.Combine(_root, "outer")).ShouldBeFalse();

        // Act
        using var engine = Create(model, root + Path.DirectorySeparatorChar);

        // Assert
        foreach (string directory in new[] { Path.Combine(_root, "outer"), parent })
        {
            var parentAcl = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(directory));
            parentAcl.AreAccessRulesProtected.ShouldBeFalse();
            var rules = parentAcl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
            rules.Length.ShouldBe(2);
            rules.ShouldAllBe(r => r.IsInherited && r.AccessControlType == AccessControlType.Allow &&
                r.InheritanceFlags == inheritance && r.PropagationFlags == PropagationFlags.None);
            rules.Single(r => r.IdentityReference.Value == identity.User!.Value).FileSystemRights.ShouldBe(FileSystemRights.FullControl);
            rules.Single(r => r.IdentityReference.Value == "S-1-1-0").FileSystemRights.ShouldBe(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize);
        }
        var rootAcl = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(root));
        rootAcl.AreAccessRulesProtected.ShouldBeTrue();
        CheckGrants(rootAcl, new[] { identity.User!.Value, "S-1-5-18", "S-1-5-32-544" }.Distinct().ToArray(), inherited: false);
    }

    [WindowsTheory(DisplayName = "Cohesion Test [Database] - Engine root: every file engine grants inheritable Windows access to the distinct required principals")]
    [InlineData("Sql")]
    [InlineData("KeyValuePair")]
    [InlineData("Documents")]
    [InlineData("Graph")]
    [InlineData("Blob")]
    [SupportedOSPlatform("windows")]
    public async Task Create_NewWindowsRoot_ShouldUseProtectedGrants(string model)
    {
        // Arrange
        using var identity = WindowsIdentity.GetCurrent();
        string[] principals = new[] { identity.User!.Value, "S-1-5-18", "S-1-5-32-544" }.Distinct().ToArray();

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
        rules.Length.ShouldBe(principals.Length);
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
        rules.Length.ShouldBe(principals.Length);
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

    private void CheckRefusal(DatabaseException error, int mode, string entry, string? root = null)
    {
        root ??= _root;
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK06007");
        error.Message.ShouldContain("foreign entry '" + entry + "'", Case.Sensitive);
        error.Message.ShouldContain(root, Case.Sensitive);
        error.Message.ShouldContain(Convert.ToString(mode, 8).PadLeft(4, '0'), Case.Sensitive);
        error.Message.ShouldContain("0700 or 0750", Case.Sensitive);
        error.Message.ShouldContain("chmod 700 \"" + root + "\"", Case.Sensitive);
    }

    private void CheckWarning(EventSourceRecorder events, string model, int oldMode, string? root = null)
    {
        var warning = events.Events.Where(e => e.EventName == "EngineRootPermissionsTightened").ShouldHaveSingleItem();
        warning.EventId.ShouldBe(model switch { "Sql" => 31, "KeyValuePair" => 16, "Documents" => 18, "Graph" => 19, _ => 16 });
        warning.Level.ShouldBe(EventLevel.Warning);
        warning.PayloadNames.ShouldBe(["path", "oldMode", "newMode"]);
        warning.Payload!.ShouldBe([root ?? _root, Convert.ToString(oldMode, 8).PadLeft(4, '0'), "0700"]);
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
