using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSequenceThirdPassTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - DISCARD documentation: the statement matrix names the original host reset values")]
    public void Documentation_DiscardMatrix_ShouldDescribeInitialTimeoutRestoration()
    {
        // Arrange / Act
        string row = File.ReadLines(Path.Combine(DatabaseDirectory(), "Assimalign.Cohesion.Database.Sql.Language", "docs", "DIALECT.md"))
            .Single(line => line.StartsWith("| `DISCARD ALL` |", StringComparison.Ordinal));

        // Assert: count the documented reset boundary in the single statement-matrix entry.
        row.Split("before the session's first statement", StringSplitOptions.None).Length.ShouldBe(2);
        row.ShouldNotContain("clear sequence state and session timeout overrides", Case.Insensitive);
    }

    private static string DatabaseDirectory([CallerFilePath] string source = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));

}
