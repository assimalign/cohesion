using System;

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
}
