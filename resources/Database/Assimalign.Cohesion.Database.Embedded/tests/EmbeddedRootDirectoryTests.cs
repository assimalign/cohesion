using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.KeyValuePair;

namespace Assimalign.Cohesion.Database.Embedded.Tests;

public sealed class EmbeddedRootDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cohesion-embedded-root-" + Guid.NewGuid().ToString("N"));

    [Fact(DisplayName = "Cohesion Test [Database.Embedded] - Engine root: composition uses the wrapped engine's owner-only root")]
    public async Task Create_FileEngine_ShouldPrepareRootBeforeDatabaseWork()
    {
        // Arrange
        var engine = KeyValueDatabaseEngine.Create("embedded", new() { RootPath = _root });
        await using var embedded = EmbeddedDatabase.Create(options => options.Engines.Add(engine));

        // Act
        var database = await engine.CreateDatabaseAsync("sales", CancellationToken.None);

        // Assert
        embedded.TryGetEngine("embedded", out var wrapped).ShouldBeTrue();
        wrapped.ShouldBeSameAs(engine); database.Engine.ShouldBeSameAs(engine);
        if (OperatingSystem.IsWindows()) { CheckWindowsRoot(); }
        else { File.GetUnixFileMode(_root).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [SupportedOSPlatform("windows")]
    private void CheckWindowsRoot()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var acl = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(_root));
        acl.AreAccessRulesProtected.ShouldBeTrue();
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        rules.Length.ShouldBe(3);
        rules.Select(r => r.IdentityReference.Value).Order().ShouldBe(new[] { identity.User!.Value, "S-1-5-18", "S-1-5-32-544" }.Order());
        rules.ShouldAllBe(r => !r.IsInherited && r.FileSystemRights == FileSystemRights.FullControl &&
            r.AccessControlType == AccessControlType.Allow &&
            r.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit));
    }

    public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); } }
}
