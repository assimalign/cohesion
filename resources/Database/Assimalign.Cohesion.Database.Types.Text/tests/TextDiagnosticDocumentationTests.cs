using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Types.Text.Tests;

public sealed class TextDiagnosticDocumentationTests
{
    [Fact(DisplayName = "Cohesion Test [Text] - Diagnostic registry lists exactly the child-root codes without inventing model translations (#1626)")]
    public void Documentation_TextErrorCodes_ShouldMatchEveryEnumMember()
    {
        // Arrange
        string registry = File.ReadAllText(RegistryPath());
        string design = File.ReadAllText(Path.Combine(Path.GetDirectoryName(RegistryPath())!, "..", "..", "Assimalign.Cohesion.Database.Types.Text", "docs", "DESIGN.md"));

        // Act
        string table = registry.Split("## Text analysis conditions").Last().Split("\n## ")[0];
        string[] codes = table.Split('\n').Where(line => line.StartsWith("| `", StringComparison.Ordinal))
            .Select(line => line.Split('`')[1]).ToArray();

        // Assert
        codes.Order().ShouldBe(Enum.GetNames<TextErrorCode>().Order());
        foreach (string code in codes) { design.ShouldContain("`" + code + "`", Case.Sensitive); }
        table.ShouldContain("No model translates these codes yet", Case.Sensitive);
    }

    private static string RegistryPath([CallerFilePath] string source = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "Assimalign.Cohesion.Database", "docs", "ERROR-CODES.md"));
}
