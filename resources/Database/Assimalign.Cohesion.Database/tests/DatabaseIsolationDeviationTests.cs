using System.IO;
using System.Runtime.CompilerServices;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Tests;

public sealed class DatabaseIsolationDeviationTests
{
    [Fact(DisplayName = "Cohesion Test [Database] - Architecture: mutable isolation documents its scoped construction-state deviation")]
    public void TryConfigureIsolationLevel_ConstructionStateException_ShouldCarryOwnerMarker()
    {
        // Arrange: this architectural exception is source documentation, not runtime metadata.
        string source = File.ReadAllText(ProjectFile("src/DatabaseTransaction.cs"));
        string design = File.ReadAllText(ProjectFile("docs/DESIGN.md"));
        const string marker = "// Deviates from the repo database-area.md rule 6 (state fixed at construction) per design decision: SQL SET TRANSACTION may change the isolation level before the first data statement (#1012); pending owner sign-off.";

        // Act
        int position = source.IndexOf("protected bool TryConfigureIsolationLevel", System.StringComparison.Ordinal);

        // Assert: keep the exception at its member and in the owning design, for owner review.
        position.ShouldBeGreaterThan(0);
        source[..position].TrimEnd().ShouldEndWith(marker, Case.Sensitive);
        design.ShouldContain("database-area.md rule 6", Case.Sensitive);
        design.ShouldContain("pending owner sign-off", Case.Sensitive);
    }

    private static string ProjectFile(string relativePath, [CallerFilePath] string testFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", relativePath));
}
