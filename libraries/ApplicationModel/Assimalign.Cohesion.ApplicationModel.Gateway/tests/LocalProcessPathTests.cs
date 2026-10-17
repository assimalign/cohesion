using System;
using System.IO;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

public class LocalProcessPathTests
{
    [Theory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Windows launch path: refuses absent or unusable short names")]
    [InlineData(null)]
    [InlineData(260)]
    [InlineData(300)]
    public void ResolveWindowsPath_NoUsableShortPath_ThrowsCodedActionableRefusal(int? shortLength)
    {
        // Arrange
        string path = @"C:\" + new string('x', 253) + ".exe";
        int calls = 0;

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() =>
            LocalProcessPath.ResolveWindowsPath(path, "executable", candidate =>
            {
                candidate.ShouldBe(path);
                calls++;
                return shortLength is int length ? new string('x', length) : null;
            }));

        // Assert
        calls.ShouldBe(1);
        exception.Message.ShouldContain("COHLOCAL001", Case.Sensitive);
        exception.Message.ShouldContain("260 characters", Case.Sensitive);
        exception.Message.ShouldContain(path, Case.Sensitive);
        exception.Message.ShouldContain("Move the application to a shorter path", Case.Sensitive);
        exception.Message.ShouldContain("enable 8.3 names", Case.Sensitive);
        exception.Message.ShouldContain("recreate", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Windows launch path: uses short names at MAX_PATH")]
    [InlineData(259, false)]
    [InlineData(260, true)]
    [InlineData(330, true)]
    public void ResolveWindowsPath_PathLengthBoundary_UsesShortPathOnlyWhenRequired(int length, bool expectedLookup)
    {
        // Arrange
        string path = @"C:\" + new string('x', length - 7) + ".exe";
        bool lookedUp = false;

        // Act
        string resolved = LocalProcessPath.ResolveWindowsPath(path, "executable", _ =>
        {
            lookedUp = true;
            return @"C:\SHORT~1\HOST.EXE";
        });

        // Assert
        lookedUp.ShouldBe(expectedLookup);
        resolved.ShouldBe(expectedLookup ? @"C:\SHORT~1\HOST.EXE" : path);
    }

    [WindowsProcessTheory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Windows launch path: passes a relative command and directory in their full form")]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolveCommand_RelativePathPastMaxPathAsWritten_ReturnsFullForm(bool quoted)
    {
        // Arrange
        // Each "pad\.." cancels out: 260+ characters as written, a short path at the drive root once
        // normalized, whatever the current directory's depth.
        string toRoot = Path.GetRelativePath(Environment.CurrentDirectory, Path.GetPathRoot(Environment.CurrentDirectory)!);
        string relative = string.Concat(Enumerable.Repeat(@"pad\..\", 40)) + Path.Combine(toRoot, "tools", "probe.exe");
        string expected = Path.GetFullPath(relative);
        string command = quoted ? $"\"{relative}\"" : relative;

        // Act
        string resolvedCommand = LocalProcessPath.ResolveCommand(command, NoShortPathLookup);
        string resolvedDirectory = LocalProcessPath.ResolveWorkingDirectory(Path.GetDirectoryName(relative)!, NoShortPathLookup);

        // Assert
        relative.Length.ShouldBeGreaterThanOrEqualTo(260);
        expected.ShouldBe(Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory)!, "tools", "probe.exe"));
        resolvedCommand.ShouldBe(expected);
        resolvedDirectory.ShouldBe(Path.GetDirectoryName(expected));
    }

    [WindowsProcessTheory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Windows launch path: keeps a short qualified command as written and normalizes a long one")]
    [InlineData(@"""C:\tools\probe.exe""", @"""C:\tools\probe.exe""")]
    [InlineData(@"C:\tools\..\tools\probe.exe", @"C:\tools\..\tools\probe.exe")]
    [InlineData(@"C:\tools\PADDING\probe.exe", @"C:\tools\probe.exe")]
    public void ResolveCommand_QualifiedPath_KeepsUsablePathAsWritten(string command, string expected)
    {
        // Arrange
        // PADDING stands for 40 "pad\.." pairs: 260+ characters as written, short once normalized.
        command = command.Replace("PADDING", string.Join('\\', Enumerable.Repeat(@"pad\..", 40)), StringComparison.Ordinal);

        // Act
        string resolved = LocalProcessPath.ResolveCommand(command, NoShortPathLookup);

        // Assert
        resolved.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Windows launch path: removes extended prefixes before CreateProcess")]
    [InlineData(@"\\?\C:\SHORT~1\HOST.EXE", @"C:\SHORT~1\HOST.EXE")]
    [InlineData(@"\\?\UNC\server\share\HOST.EXE", @"\\server\share\HOST.EXE")]
    public void ResolveWindowsPath_ExtendedShortName_ReturnsOrdinaryLaunchPath(string shortPath, string expected)
    {
        // Arrange
        string path = @"C:\" + new string('x', 300) + ".exe";

        // Act
        string resolved = LocalProcessPath.ResolveWindowsPath(path, "executable", _ => shortPath);

        // Assert
        resolved.ShouldBe(expected);
    }

    private static string? NoShortPathLookup(string path) =>
        throw new InvalidOperationException($"No short-name lookup was expected for '{path}'.");
}
