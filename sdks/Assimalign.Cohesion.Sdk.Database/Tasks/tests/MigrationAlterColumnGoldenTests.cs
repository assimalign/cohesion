using System;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database;
using Assimalign.Cohesion.Database.Sql;

namespace Assimalign.Cohesion.Sdk.Database.Tests;

public sealed class MigrationAlterColumnGoldenTests
{
    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - ALTER COLUMN: engine scripts retain base bytes for every shape")]
    public void Generate_AlterColumnCorpus_ShouldRetainBaseBytes()
    {
        // Arrange: framework ArgumentException suffixes must not depend on the runner's locale.
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

            // Act / Assert
            CompareCorpus();
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    private static void CompareCorpus()
    {
        // Arrange: initial capture preceded extraction; expanded by replaying generator/renderer
        // source from the same pinned base objects, 73aeebff31f9fd437f27c20c0f2f2487e594e7b3.
        var actual = new StringBuilder("Base: 73aeebff31f9fd437f27c20c0f2f2487e594e7b3\n");
        foreach (var item in MigrationAlterColumnCases.All())
        {
            actual.Append(item.Name).Append('\n');
            try
            {
                // Act: script generation is metadata-only; the same output serves empty/populated,
                // inline/deferred typed application. Execution routes are covered separately.
                var script = SqlMigrationScriptGenerator.Generate(MigrationAlterColumnCases.Plan(item.Previous, item.Desired));
                foreach (var step in script.Steps)
                {
                    actual.Append(step.StatementText).Append('\n').Append(step.RollbackStatementText ?? "<irreversible>").Append('\n');
                }
                actual.Append("Reversible: ").Append(script.IsReversible).Append('\n');
            }
            catch (DatabaseException refusal)
            {
                actual.Append("Refused: ").Append(refusal.Message).Append('\n');
            }
        }

        // Assert
        actual.ToString().ShouldBe(File.ReadAllText(GoldenPath()).Replace("\r\n", "\n"));
    }

    private static string GoldenPath([CallerFilePath] string source = "")
        => Path.Combine(Path.GetDirectoryName(source)!, "TestObjects", "MigrationAlterColumn.base.txt");
}
