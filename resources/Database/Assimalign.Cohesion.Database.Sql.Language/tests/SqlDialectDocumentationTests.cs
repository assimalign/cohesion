using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlDialectDocumentationTests
{
    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Dialect: clause totals and count explanation include FILTER (#1720)")]
    public void Dialect_DeclaredClauseTotals_ShouldIncludeFilter()
    {
        // Arrange
        string dialect = ReadDialect();
        int supported = SqlLanguageProfile.Instance.Clauses.Count();
        const int exclusions = 5;

        // Act
        string matrix = dialect.Split("## Statement matrix")[1].Split("\n## ")[0];

        // Assert
        matrix.ShouldContain($"**{supported} of {supported + exclusions} declared clauses**", Case.Sensitive);
        matrix.Split("Phase 22")[0].ShouldContain("FILTER (#1720)", Case.Sensitive);
        dialect.ShouldContain($"These contextual exclusions do not change the {supported}/{supported + exclusions} declared-clause count.", Case.Sensitive);
    }

    private static string ReadDialect([CallerFilePath] string source = "")
        => File.ReadAllText(Path.Combine(Path.GetDirectoryName(source)!, "..", "docs", "DIALECT.md"));
}
